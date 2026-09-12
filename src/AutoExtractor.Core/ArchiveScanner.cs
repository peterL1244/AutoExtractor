using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace AutoExtractor.Core;

public static class SignatureDetector
{
    const int Limit = 4 * 1024 * 1024;
    public static SignatureInfo Detect(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] bytes = new byte[(int)Math.Min(f.Length, Limit)];
        f.ReadExactly(bytes);
        if (IsElfExecutable(bytes))
            return new(ArchiveFormat.Unknown, true, false, false);
        bool exe = bytes.Length >= 64 && bytes[0] == 'M' && bytes[1] == 'Z';
        if (exe)
        {
            int pe = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(60, 4));
            exe = pe >= 64 && pe <= bytes.Length - 24 && bytes.AsSpan(pe, 4).SequenceEqual(new byte[] { 80, 69, 0, 0 });
        }
        int end = exe ? bytes.Length : Math.Min(bytes.Length, 1);
        for (int i = 0; i < end; i++)
        {
            var b = bytes.AsSpan(i);
            var format = At(b);
            if (format != ArchiveFormat.Unknown)
            {
                // SFX payloads need a complete structural header, not an arbitrary embedded marker.
                if (exe && format == ArchiveFormat.SevenZip && !Valid7z(b))
                    continue;
                if (exe && format == ArchiveFormat.Zip && (b.Length < 30 || b[4] > 63 || b[5] != 0))
                    continue;
                if (format == ArchiveFormat.Rar)
                {
                    var rar = RarInfo(b);
                    if (exe && !rar.Valid)
                        continue;
                    return new(format, exe, exe, rar.Volume, rar.Number);
                }
                bool volume = b.StartsWith(new byte[] { 80, 75, 7, 8 }) ||
                    format == ArchiveFormat.Zip && HasZipVolumeFooter(f, bytes);
                return new(format, exe, exe, volume);
            }
        }
        // Bounded tail read distinguishes split ZIP final volumes from arbitrary continuation bytes.
        if (f.Length > bytes.Length)
        {
            f.Position = Math.Max(0, f.Length - 65557);
            bytes = new byte[(int)(f.Length - f.Position)];
            f.ReadExactly(bytes);
        }
        for (int i = Math.Max(0, bytes.Length - 65557); i <= bytes.Length - 22; i++)
            if (bytes.AsSpan(i, 4).SequenceEqual(new byte[] { 80, 75, 5, 6 }) && i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 20, 2)) == bytes.Length)
                return new(ArchiveFormat.Zip, exe, exe, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 4, 2)) > 0);
        return new(ArchiveFormat.Unknown, exe, false, false);
    }
    static bool IsElfExecutable(ReadOnlySpan<byte> b)
    {
        if (b.Length < 52 || !b.StartsWith(new byte[] { 127, 69, 76, 70 }) || b[4] is not (1 or 2) || b[5] is not (1 or 2) || b[6] != 1 || b[4] == 2 && b.Length < 64)
            return false;
        ushort type = b[5] == 1 ? BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(16, 2)) : BinaryPrimitives.ReadUInt16BigEndian(b.Slice(16, 2));
        return type is 2 or 3;
    }
    static bool HasZipVolumeFooter(FileStream file, ReadOnlySpan<byte> header)
    {
        // Native ZIP terminal volumes may begin with a local file record. Their volume
        // identity lives in EOCD, so do not let the recognized first record bypass the tail.
        ReadOnlySpan<byte> tail = header;
        if (file.Length > header.Length)
        {
            file.Position = Math.Max(0, file.Length - 65557);
            byte[] bytes = new byte[(int)(file.Length - file.Position)];
            file.ReadExactly(bytes);
            tail = bytes;
        }
        for (int i = Math.Max(0, tail.Length - 65557); i <= tail.Length - 22; i++)
            if (tail.Slice(i, 4).SequenceEqual(new byte[] { 80, 75, 5, 6 }) &&
                i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.Slice(i + 20, 2)) == tail.Length)
                return BinaryPrimitives.ReadUInt16LittleEndian(tail.Slice(i + 4, 2)) > 0;
        return false;
    }
    static ArchiveFormat At(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 32 && b.StartsWith(new byte[] { 55, 122, 188, 175, 39, 28 }))
            return ArchiveFormat.SevenZip;
        if (b.Length >= 7 && b.StartsWith(new byte[] { 82, 97, 114, 33, 26, 7 }) && (b[6] == 0 || b.Length >= 8 && b[6] == 1 && b[7] == 0))
            return ArchiveFormat.Rar;
        if (b.Length >= 4 && b[0] == 80 && b[1] == 75 && ((b[2] == 3 && b[3] == 4 && b.Length >= 30) || (b[2] == 5 && b[3] == 6 && b.Length >= 22) || (b[2] == 7 && b[3] == 8 && b.Length >= 8)))
            return ArchiveFormat.Zip;
        return ArchiveFormat.Unknown;
    }
    static bool Valid7z(ReadOnlySpan<byte> b)
    {
        uint crc = 0xffffffff;
        foreach (byte x in b.Slice(12, 20))
        {
            crc ^= x;
            for (int j = 0; j < 8; j++)
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        return ~crc == BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(8, 4));
    }
    static (bool Valid, bool Volume, int? Number) RarInfo(ReadOnlySpan<byte> b)
    {
        if (b.Length < 14)
            return (false, false, null);
        if (b[6] == 0)
        {
            if (b[9] != 0x73)
                return (false, false, null);
            int flags = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(10, 2)), size = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(12, 2));
            if (size < 13 || size > b.Length - 7)
                return (false, false, null);
            return ((Crc(b.Slice(9, size - 2)) & 65535) == BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(7, 2)), (flags & 1) != 0, (flags & 0x100) != 0 ? 0 : null);
        }
        int pos = 12;
        long size5 = Vint(b, ref pos);
        int start = pos;
        if (size5 < 3 || size5 > b.Length - start)
            return (false, false, null);
        long type = Vint(b, ref pos), flags5 = Vint(b, ref pos);
        if (type is not (1 or 4) || flags5 < 0)
            return (false, false, null);
        bool valid = Crc(b.Slice(12, (int)size5 + start - 12)) == BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(8, 4));
        if (type == 4)
            return (valid, false, null);
        if ((flags5 & 1) != 0)
            Vint(b, ref pos);
        if ((flags5 & 2) != 0)
            Vint(b, ref pos);
        long archiveFlags = Vint(b, ref pos);
        if (archiveFlags < 0)
            return (false, false, null);
        long number = (archiveFlags & 2) != 0 ? Vint(b, ref pos) : 0;
        return (valid, (archiveFlags & 1) != 0, number >= 0 && number < int.MaxValue ? (int)number : null);
    }
    static long Vint(ReadOnlySpan<byte> b, ref int pos)
    {
        long v = 0;
        for (int shift = 0; shift < 63 && pos < b.Length; shift += 7)
        {
            byte x = b[pos++];
            v |= (long)(x & 127) << shift;
            if ((x & 128) == 0)
                return v;
        }
        return -1;
    }
    static uint Crc(ReadOnlySpan<byte> b)
    {
        uint c = 0xffffffff;
        foreach (byte x in b)
        {
            c ^= x;
            for (int j = 0; j < 8; j++)
                c = (c >> 1) ^ ((c & 1) != 0 ? 0xedb88320u : 0);
        }
        return ~c;
    }
    internal static bool IsMedia(string p)
    {
        using var f = File.OpenRead(p);
        Span<byte> b = stackalloc byte[12];
        int n = f.Read(b);
        b = b[..n];
        return b.StartsWith(new byte[] { 255, 216, 255 }) || b.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) || b.StartsWith("GIF8"u8) || b.Length >= 8 && b.Slice(4, 4).SequenceEqual("ftyp"u8) || b.StartsWith("RIFF"u8) || b.StartsWith(new byte[] { 26, 69, 223, 163 });
    }
    internal static bool HasIndependentHeader(string p)
    {
        using var f = File.OpenRead(p);
        Span<byte> b = stackalloc byte[8];
        int n = f.Read(b);
        b = b[..n];
        return b.StartsWith(new byte[] { 80, 75, 3, 4 }) || b.StartsWith(new byte[] { 55, 122, 188, 175, 39, 28 }) || b.StartsWith(new byte[] { 82, 97, 114, 33, 26, 7 }) || b.StartsWith("MZ"u8);
    }
}
public sealed class ArchiveScanner : IArchiveScanner
{
    static readonly HashSet<string> Native = new(StringComparer.OrdinalIgnoreCase) { ".jar", ".apk", ".aab", ".docx", ".xlsx", ".pptx", ".docm", ".xlsm", ".pptm", ".odt", ".ods", ".odp", ".epub", ".nupkg", ".pak", ".unity3d", ".assets", ".vpk", ".rpa", ".pck" };
    record Item(string Path, SignatureInfo Sig, string Key, string Stem, int Number, VolumeKind Kind, bool CrossDirectoryAmbiguous = false);
    public Task<ScanResult> ScanAsync(IEnumerable<string> inputs, CancellationToken cancellationToken = default)
    {
        var inputPaths = inputs.Select(Path.GetFullPath).ToArray();
        return Task.Run(() => Scan(inputPaths, cancellationToken), cancellationToken);
    }
    ScanResult Scan(string[] inputs, CancellationToken ct)
    {
        var messages = new List<string>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var explicitFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<string>();
        foreach (var input in inputs)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(input))
            {
                explicitFiles.Add(input);
                foreach (var p in Directory.EnumerateFiles(Path.GetDirectoryName(input)!))
                    paths.Add(p);
            }
            else if (Directory.Exists(input))
            {
                roots.Add(input);
                Walk(input, paths, messages, ct);
            }
        }
        var items = new List<Item>();
        foreach (var p in paths.Order(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0 || Native.Contains(Path.GetExtension(p)))
                    continue;
                var sig = SignatureDetector.Detect(p);
                if (sig.Format == ArchiveFormat.Unknown && (sig.IsExecutable || SignatureDetector.IsMedia(p)))
                    continue;
                items.Add(Parse(p, sig));
            }
            catch (IOException e) { messages.Add($"无法读取 {p}: {e.Message}"); }
            catch (UnauthorizedAccessException e) { messages.Add($"无法读取 {p}: {e.Message}"); }
        }
        // A loose numeric basename can be a product ID. Preserve explicit volume syntax
        // and plausible continuation members, but do not merge independent SFX packages.
        var looseSfx = items.Where(x => x.Kind == VolumeKind.ByteSplit && x.Sig.IsSelfExtracting && !x.Sig.IsVolume &&
            !Regex.IsMatch(Path.GetFileName(x.Path), @"\.\d+(?:\.[^.]+)?$")).ToArray();
        foreach (var x in looseSfx)
            if (!items.Any(y => y.Path != x.Path && y.Kind == VolumeKind.ByteSplit &&
                string.Equals(y.Stem, x.Stem, StringComparison.OrdinalIgnoreCase) &&
                (y.Number == x.Number || !looseSfx.Contains(y))))
                items[items.IndexOf(x)] = x with
                {
                    Key = x.Path,
                    Stem = Path.GetFileNameWithoutExtension(x.Path),
                    Number = 0,
                    Kind = VolumeKind.Single
                };
        // Include anchors from already-scoped paths only. Keep each member's local key until
        // the family-level ambiguity check decides whether separate directories can be joined.
        for (int i = 0; i < items.Count; i++)
            if (items[i].Kind == VolumeKind.Single)
            {
                var x = items[i];
                var anchorKind = Path.GetExtension(x.Path).ToLowerInvariant() switch
                {
                    ".zip" => VolumeKind.ZipSplit,
                    ".rar" => VolumeKind.RarLegacy,
                    _ => VolumeKind.Single
                };
                var family = anchorKind == VolumeKind.Single ? null : items.FirstOrDefault(y => y.Kind == anchorKind &&
                    string.Equals(y.Stem, Path.GetFileNameWithoutExtension(x.Path), StringComparison.OrdinalIgnoreCase) &&
                    (x.Sig.IsVolume || string.Equals(Path.GetDirectoryName(x.Path), Path.GetDirectoryName(y.Path), StringComparison.OrdinalIgnoreCase)));
                if (family != null)
                    items[i] = x with
                    {
                        Key = LocalKey(x.Path, family.Stem, family.Kind),
                        Stem = family.Stem,
                        Kind = family.Kind,
                        Number = family.Kind == VolumeKind.ZipSplit ? int.MaxValue : 0
                    };
            }
        items = JoinScopedFamilies(items);
        var candidates = new List<ArchiveCandidate>();
        foreach (var group in items.GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var a = group.OrderBy(x => x.Number).ToArray();
            if (!a.Any(x => explicitFiles.Contains(x.Path) || roots.Any(r => Within(x.Path, r))))
                continue;
            var head = a.FirstOrDefault(x => x.Sig.Format != ArchiveFormat.Unknown);
            if (head == null && a.All(x => x.Kind == VolumeKind.Single))
                continue;
            var format = head?.Sig.Format ?? ArchiveFormat.Unknown;
            var kind = a[0].Kind;
            if (kind == VolumeKind.ByteSplit && a[0].Sig.IsVolume && format == ArchiveFormat.Zip)
                kind = VolumeKind.ZipSplit;
            if (kind == VolumeKind.ByteSplit && a[0].Sig.IsVolume && format == ArchiveFormat.Rar)
                kind = VolumeKind.RarParts;
            bool duplicate = a.GroupBy(x => x.Number).Any(g => g.Count() > 1);
            int first = kind == VolumeKind.RarLegacy ? 0 : 1;
            var numbered = a.Where(x => x.Number != int.MaxValue).ToArray();
            bool gap = numbered.Length > 0 && (numbered[0].Number != first || numbered.Where((x, i) => x.Number != first + i).Any());
            var status = CandidateStatus.Ready;
            string explanation = "已识别文件头；完整性由解压引擎验证。";
            if (format == ArchiveFormat.Unknown)
            {
                status = CandidateStatus.NeedsConfirmation;
                explanation = "没有识别到首卷，请手动确认文件及顺序。";
            }
            else if (duplicate)
            {
                status = CandidateStatus.NeedsConfirmation;
                explanation = "分卷序号重复，请手动选择。";
            }
            else if (kind != VolumeKind.Single && (gap || kind == VolumeKind.ZipSplit && a[0].Kind == VolumeKind.ZipSplit && !a.Any(x => x.Number == int.MaxValue)))
            {
                status = CandidateStatus.MissingVolumes;
                explanation = "分卷不连续或缺少首卷/末卷。";
            }
            else if (kind is VolumeKind.ByteSplit or VolumeKind.RarParts or VolumeKind.RarLegacy && a.Length == 1)
            {
                status = CandidateStatus.NeedsConfirmation;
                explanation = "仅发现一个编号文件，无法确定是否缺卷。";
            }
            if (kind == VolumeKind.ByteSplit && a.Skip(1).Any(x => x.Sig.Format != ArchiveFormat.Unknown && SignatureDetector.HasIndependentHeader(x.Path)))
            {
                status = CandidateStatus.NeedsConfirmation;
                explanation = "多个编号文件含独立文件头，需确认分卷类型。";
            }
            if (kind is VolumeKind.RarParts or VolumeKind.RarLegacy)
            {
                if (format != ArchiveFormat.Rar || a.Any(x => x.Sig.Format != ArchiveFormat.Rar || !x.Sig.IsVolume))
                {
                    status = CandidateStatus.NeedsConfirmation;
                    explanation = "RAR 分卷文件名与实际格式或分卷标记不一致。";
                }
                else if (a.Any(x => x.Sig.VolumeNumber.HasValue && x.Sig.VolumeNumber != x.Number - first))
                {
                    status = CandidateStatus.NeedsConfirmation;
                    explanation = "RAR 卷号元数据与文件名不一致。";
                }
            }
            if (kind == VolumeKind.ZipSplit && (format != ArchiveFormat.Zip || !a[0].Sig.IsVolume || a.Any(x => x.Sig.Format is not (ArchiveFormat.Zip or ArchiveFormat.Unknown))))
            {
                status = CandidateStatus.NeedsConfirmation;
                explanation = "ZIP 分卷文件名与实际格式或分卷标记不一致。";
            }
            // Later recognizable volumes may inform the preview format, but cannot validate an unknown head.
            if (kind != VolumeKind.Single && a[0].Sig.Format == ArchiveFormat.Unknown)
            {
                status = CandidateStatus.NeedsConfirmation;
                explanation = "首个文件没有可识别的归档头，请手动确认首卷。";
            }
            if (kind == VolumeKind.Single && head?.Sig.IsVolume == true)
            {
                status = CandidateStatus.NeedsConfirmation;
                explanation = "检测到分卷标记，但无法确定其余分卷名称。";
            }
            if (a.Any(x => x.CrossDirectoryAmbiguous))
            {
                status = CandidateStatus.NeedsConfirmation;
                explanation = "不同导入目录含相同分卷序号；保留各目录分组，请分别确认，未自动混合。";
            }
            var ext = format switch
            {
                ArchiveFormat.Zip => "zip",
                ArchiveFormat.SevenZip => "7z",
                ArchiveFormat.Rar => "rar",
                _ => "bin"
            };
            var stem = a[0].Stem.TrimEnd('.', '_', '-');
            if (stem.Length == 0)
                stem = "archive";
            var members = a.Select((x, i) => new ArchiveMember(x.Path, kind switch { VolumeKind.Single => Path.GetFileNameWithoutExtension(x.Path) + "." + ext, VolumeKind.ByteSplit => $"{stem}.{ext}.{x.Number:000}", VolumeKind.RarParts => $"{stem}.part{x.Number:000}.rar", VolumeKind.RarLegacy => x.Number == 0 ? $"{stem}.rar" : $"{stem}.r{x.Number - 1:00}", VolumeKind.ZipSplit => i == a.Length - 1 ? $"{stem}.zip" : $"{stem}.z{i + 1:00}", _ => Path.GetFileName(x.Path) }, i)).ToArray();
            candidates.Add(new()
            {
                DisplayName = stem,
                Format = format,
                VolumeKind = kind,
                Members = members,
                Status = status,
                Explanation = explanation,
                EntryMemberIndex = kind == VolumeKind.ZipSplit ? a.Length - 1 : 0,
                IsSelfExtracting = head?.Sig.IsSelfExtracting ?? false
            });
        }
        return new(candidates, messages);
    }
    static Item Parse(string p, SignatureInfo s)
    {
        string n = Path.GetFileName(p), dir = Path.GetDirectoryName(p)!;
        Match m;
        VolumeKind kind;
        int number;
        string stem;
        if ((m = Regex.Match(n, @"^(.*)\.part(\d+)\.(?:rar|exe)(?:\.[^.]+)?$", RegexOptions.IgnoreCase)).Success)
        {
            kind = VolumeKind.RarParts;
            stem = m.Groups[1].Value;
            number = Num(m.Groups[2].Value);
        }
        else if ((m = Regex.Match(n, @"^(.*)\.z(\d+)(?:\.[^.]+)?$", RegexOptions.IgnoreCase)).Success)
        {
            kind = VolumeKind.ZipSplit;
            stem = m.Groups[1].Value;
            number = Num(m.Groups[2].Value);
        }
        else if ((m = Regex.Match(n, @"^(.*)\.r(\d{2})(?:\.[^.]+)?$", RegexOptions.IgnoreCase)).Success)
        {
            kind = VolumeKind.RarLegacy;
            stem = m.Groups[1].Value;
            number = Num(m.Groups[2].Value) + 1;
        }
        else if (!Regex.IsMatch(n, @"\.(zip|rar|7z)$", RegexOptions.IgnoreCase) && (m = Regex.Match(n, @"^(.*?)(\d+)(?:\.[^.]+)?$")).Success)
        {
            kind = VolumeKind.ByteSplit;
            stem = m.Groups[1].Value.TrimEnd('.');
            stem = Regex.Replace(stem, @"\.(zip|7z|rar)$", "", RegexOptions.IgnoreCase);
            number = Num(m.Groups[2].Value);
        }
        else
            return new(p, s, p, Path.GetFileNameWithoutExtension(p), 0, VolumeKind.Single);
        if (kind == VolumeKind.ByteSplit && s.Format == ArchiveFormat.Rar && s.IsVolume)
            kind = VolumeKind.RarParts;
        return new(p, s, LocalKey(p, stem, kind), stem, number, kind);
    }
    static string LocalKey(string path, string stem, VolumeKind kind) => Path.Combine(Path.GetDirectoryName(path)!, stem) + "|" + kind;
    static List<Item> JoinScopedFamilies(List<Item> items)
    {
        var result = new List<Item>(items.Count);
        foreach (var family in items.GroupBy(x => x.Kind == VolumeKind.Single ? x.Key : x.Stem + "|" + x.Kind, StringComparer.OrdinalIgnoreCase))
        {
            var members = family.ToArray();
            if (members[0].Kind == VolumeKind.Single || members.Select(x => Path.GetDirectoryName(x.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
            {
                result.AddRange(members);
                continue;
            }
            if (members.GroupBy(x => x.Number).Any(g => g.Count() > 1))
            {
                // A name is not archive identity. Preserve separate sets instead of combining
                // duplicate heads or choosing one of multiple possible continuation volumes.
                result.AddRange(members.Select(x => x with { CrossDirectoryAmbiguous = true }));
                continue;
            }
            string key = "scoped-family|" + family.Key;
            result.AddRange(members.Select(x => x with { Key = key }));
        }
        return result;
    }
    static int Num(string s) => int.TryParse(s, out int n) ? n : int.MaxValue - 1;
    static bool Within(string path, string root) => path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    static void Walk(string d, HashSet<string> files, List<string> messages, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0)
                return;
            string n = Path.GetFileName(d);
            if (n.StartsWith(".autoextractor", StringComparison.OrdinalIgnoreCase) || n.Equals("AutoExtractor_Output", StringComparison.OrdinalIgnoreCase))
                return;
            foreach (var f in Directory.EnumerateFiles(d))
                files.Add(f);
            foreach (var child in Directory.EnumerateDirectories(d))
                Walk(child, files, messages, ct);
        }
        catch (IOException e) { messages.Add(e.Message); }
        catch (UnauthorizedAccessException e) { messages.Add(e.Message); }
    }
}
