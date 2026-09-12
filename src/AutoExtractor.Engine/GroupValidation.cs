using System.Globalization;
using System.Text.RegularExpressions;
using AutoExtractor.Core;

namespace AutoExtractor.Engine;

/// <summary>Reconciles the entire proposed rename set with the engine's consumed-volume metadata.</summary>
internal static class GroupValidation
{
    public static void Validate(ArchiveCandidate candidate, ArchiveListing listing, string output)
    {
        if (candidate.Format != listing.Format || candidate.Format == ArchiveFormat.Unknown)
            Reject("声明格式与引擎识别结果不一致。");
        var members = candidate.Members.OrderBy(m => m.Order).ToArray();
        if (members.Length == 0 || members.Select(m => m.Order).Distinct().Count() != members.Length)
            Reject("分卷顺序无效或重复。");
        var headerEnd = output.Replace("\r\n", "\n").IndexOf("\n----------\n", StringComparison.Ordinal);
        if (headerEnd < 0)
            Reject("引擎未提供可核验的归档头。");
        var header = output.Replace("\r\n", "\n")[..headerEnd];
        // Match only structural properties before the entry list. Alias paths prohibit newlines.
        var types = Values(header, "Type");
        var volumes = Values(header, "Volumes");
        var totalSizes = Values(header, "Total Physical Size");
        var physicalSizes = Values(header, "Physical Size");
        var offsets = Values(header, "Offset");
        bool split = types.Length == 2 && types[0] == "Split";
        bool nativeMulti = Values(header, "Multivolume").Contains("+");
        if (Values(header, "Tail Size").Any(value => Parse(value) != 0))
            Reject("归档尾部有未被解压引擎使用的数据，不能确认全部成员属于同一组。");

        if (candidate.VolumeKind == VolumeKind.Single)
        {
            if (members.Length != 1 || types.Length != 1 || split || nativeMulti || volumes.Any(value => Parse(value) != 1))
                Reject("单归档任务不能包含额外成员或分卷集合。");
            return;
        }
        if (members.Length < 2)
            Reject("分卷任务需要经过确认的完整成员集合。");
        if (volumes.Length == 0 || Parse(volumes[0]) != members.Length || (split ? volumes.Skip(1).Any(value => Parse(value) != 1) : volumes.Length != 1))
            Reject("引擎实际读取的卷数与待还原文件数不同，存在未使用或缺失成员。");
        long length = 0;
        foreach (var member in members)
            length = checked(length + new FileInfo(member.SourcePath).Length);
        if (totalSizes.Length != 1 || Parse(totalSizes[0]) != length)
            Reject("引擎实际读取的总大小与待还原成员不符。");
        string ext = candidate.Format switch
        {
            ArchiveFormat.Zip => "zip",
            ArchiveFormat.SevenZip => "7z",
            ArchiveFormat.Rar => "rar",
            _ => ""
        };
        string first = members[0].RestoredName;
        string[] expected;
        switch (candidate.VolumeKind)
        {
            case VolumeKind.ByteSplit:
                if (!split || nativeMulti || physicalSizes.Length != 2)
                    Reject("该文件组不是引擎确认的字节切分归档。");
                // A concatenator opens every numbered member even when the archive itself ignores
                // trailing bytes. Reconcile the inner archive's full footprint as well.
                if (Parse(physicalSizes[^1]) + offsets.Sum(Parse) != length)
                    Reject("切分成员包含归档未使用的尾部数据，不能安全还原文件名。");
                var suffix = "." + ext + ".001";
                if (!first.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    Reject("字节分卷别名必须从 .001 开始且格式一致。");
                var byteStem = first[..^suffix.Length];
                expected = Enumerable.Range(1, members.Length).Select(n => $"{byteStem}.{ext}.{n:D3}").ToArray();
                break;
            case VolumeKind.RarParts:
                if (split || !nativeMulti || candidate.Format != ArchiveFormat.Rar || types.Length != 1)
                    Reject("该文件组不是原生 RAR 分卷。");
                var match = Regex.Match(first, @"^(.*)\.part(0*1)\.rar$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!match.Success)
                    Reject("RAR 分卷别名必须从 part1 开始。");
                int width = match.Groups[2].Value.Length;
                expected = Enumerable.Range(1, members.Length).Select(n => $"{match.Groups[1].Value}.part{n.ToString("D" + width, CultureInfo.InvariantCulture)}.rar").ToArray();
                break;
            case VolumeKind.RarLegacy:
                if (split || !nativeMulti || candidate.Format != ArchiveFormat.Rar || types.Length != 1 || !first.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
                    Reject("该文件组不是原生旧式 RAR 分卷。");
                var rarStem = first[..^4];
                expected = [first, .. Enumerable.Range(0, members.Length - 1).Select(n => $"{rarStem}.r{n:D2}")];
                break;
            case VolumeKind.ZipSplit:
                if (split || !nativeMulti || candidate.Format != ArchiveFormat.Zip || types.Length != 1 || !first.EndsWith(".z01", StringComparison.OrdinalIgnoreCase))
                    Reject("该文件组不是原生 ZIP 分卷。");
                var zipStem = first[..^4];
                expected = [.. Enumerable.Range(1, members.Length - 1).Select(n => $"{zipStem}.z{n:D2}"), zipStem + ".zip"];
                break;
            default:
                Reject("不支持的分卷结构。");
                return;
        }
        if (!members.Select(m => m.RestoredName).SequenceEqual(expected, StringComparer.OrdinalIgnoreCase))
            Reject("分卷别名不是引擎使用的连续成员集合。");
        var entry = candidate.Members[candidate.EntryMemberIndex].RestoredName;
        if (!entry.Equals(candidate.VolumeKind == VolumeKind.ZipSplit ? expected[^1] : expected[0], StringComparison.OrdinalIgnoreCase))
            Reject("分卷入口与引擎实际使用的首卷／末卷不一致。");
    }
    static string[] Values(string header, string field) => Regex.Matches(header, "(?m)^" + Regex.Escape(field) + @" = ([^\r\n]*)$").Select(m => m.Groups[1].Value).ToArray();
    static long Parse(string value) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) ? result : throw new ArchiveException(EngineFailure.Unsupported, "引擎分卷元数据缺少可核验的大小或卷数。");
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    static void Reject(string detail) => throw new ArchiveException(EngineFailure.Unsupported, "分组验证未通过，尚未还原任何成员名称。" + detail);
}
