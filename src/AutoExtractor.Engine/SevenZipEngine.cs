using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AutoExtractor.Core;

namespace AutoExtractor.Engine;

/// <summary>Runs only the bundled engine. Archive content is never executed.</summary>
public sealed class SevenZipEngine : IArchiveEngine
{
    readonly string executablePath;
    const int MaxOutputCharacters = 32 * 1024 * 1024;
    const int MaxEntries = 200_000;
    public SevenZipEngine(string executablePath) => this.executablePath = Path.GetFullPath(executablePath);

    public async Task<ArchiveListing> ListAsync(string archivePath, string? password, CancellationToken cancellationToken = default)
        => await ListCoreAsync(archivePath, password, null, cancellationToken);

    internal Task<ArchiveListing> ValidateGroupAsync(string aliasPath, ArchiveCandidate candidate, string? password, CancellationToken cancellationToken)
        => ListCoreAsync(aliasPath, password, candidate, cancellationToken);

    async Task<ArchiveListing> ListCoreAsync(string archivePath, string? password, ArchiveCandidate? candidate, CancellationToken cancellationToken)
    {
        var result = await RunAsync(["l", "-slt", "-sccUTF-8", "-bd", PasswordArgument(password), "--", Path.GetFullPath(archivePath)], cancellationToken);
        CheckExit(result, password);
        var listing = ParseListing(result.Output);
        if (candidate != null)
            GroupValidation.Validate(candidate, listing, result.Output);
        if (listing.Format == ArchiveFormat.Zip)
            ZipMetadata.Validate(Path.GetFullPath(archivePath), cancellationToken);
        return listing;
    }

    // Full integrity validation is used on temporary aliases before a durable source rename.
    public async Task TestAsync(string archivePath, string? password, CancellationToken cancellationToken = default)
    {
        var listing = await ListAsync(archivePath, password, cancellationToken);
        var result = await RunAsync(["t", "-sccUTF-8", "-bd", PasswordArgument(password), "--", Path.GetFullPath(archivePath)], cancellationToken);
        CheckExit(result, password, listing.IsEncrypted);
    }

    public async Task ExtractAsync(string archivePath, string outputDirectory, string? password,
        IProgress<ExtractionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var listing = await ListAsync(archivePath, password, cancellationToken);
        outputDirectory = Path.GetFullPath(outputDirectory);
        ArchiveSafety.CheckParents(outputDirectory);
        if (File.Exists(outputDirectory) || (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any()))
            throw new ArchiveException(EngineFailure.UnsafePath, "输出暂存目录必须为空；不会覆盖已有文件。");
        long bytes = 0;
        foreach (var entry in listing.Entries)
        {
            ArchiveSafety.ValidateEntry(entry);
            try
            {
                bytes = checked(bytes + entry.Size);
            }
            catch (OverflowException) { throw new ArchiveException(EngineFailure.DiskFull, "归档声明的展开大小超出可处理范围。"); }
        }
        ArchiveSafety.RequireSpace(outputDirectory, bytes);
        Directory.CreateDirectory(outputDirectory);
        ArchiveSafety.CheckParents(outputDirectory);
        progress?.Report(new("正在解压到独立暂存目录…"));
        var result = await RunAsync(["x", "-y", "-aos", "-sccUTF-8", "-bb1", "-bsp1", "-o" + outputDirectory, PasswordArgument(password), "--", Path.GetFullPath(archivePath)], cancellationToken, progress, password);
        CheckExit(result, password, listing.IsEncrypted);
        // Check the real output, including attributes, before a coordinator may publish it.
        foreach (var entry in listing.Entries)
        {
            var path = Path.Combine(outputDirectory, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            ArchiveSafety.CheckParents(path);
            if (entry.IsDirectory ? !Directory.Exists(path) : !File.Exists(path) || new FileInfo(path).Length != entry.Size)
                throw new ArchiveException(EngineFailure.CorruptArchive, "解压结果不完整，已保留为未完成目录。");
        }
        progress?.Report(new("本层解压与结果核验完成", 100));
    }

    // Passing an explicit nonempty random attempt prevents 7-Zip from ever prompting on stdin.
    static string PasswordArgument(string? password) => "-p" + (password ?? Guid.NewGuid().ToString("N"));
    sealed record ProcessResult(int ExitCode, string Output, string Error);
    async Task<ProcessResult> RunAsync(IEnumerable<string> arguments, CancellationToken token, IProgress<ExtractionProgress>? progress = null, string? password = null)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!
        };
        foreach (var arg in arguments)
            start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
                throw new ArchiveException(EngineFailure.Unsupported, "无法启动解压引擎。");
        }
        catch (System.ComponentModel.Win32Exception) { throw new ArchiveException(EngineFailure.Unsupported, "解压引擎缺失或无法启动，请检查便携包是否完整。"); }
        process.StandardInput.Close();
        using var registration = token.Register(() => Kill(process));
        var stdout = ReadBounded(process.StandardOutput, process, token, progress, password);
        var stderr = ReadBounded(process.StandardError, process, token);
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            var outputs = await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return new(process.ExitCode, outputs[0], outputs[1]);
        }
        catch
        {
            Kill(process);
            try
            {
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            }
            catch { }
            throw;
        }
    }
    static void Kill(Process p)
    {
        try
        {
            if (!p.HasExited)
                p.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
    static async Task<string> ReadBounded(StreamReader reader, Process process, CancellationToken token, IProgress<ExtractionProgress>? progress = null, string? password = null)
    {
        var buffer = new char[8192];
        var text = new StringBuilder();
        int count;
        var live = new StringBuilder();
        int? lastPercent = null;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            if (text.Length + count > MaxOutputCharacters)
            {
                Kill(process);
                throw new ArchiveException(EngineFailure.Unsupported, "归档条目或引擎输出过大，已安全停止，请手动处理。");
            }
            text.Append(buffer, 0, count);
            if (progress != null)
            {
                for (int i = 0; i < count; i++)
                {
                    char ch = buffer[i];
                    if (ch is '\r' or '\n' or '\b')
                    {
                        var line = live.ToString().Trim();
                        if (line.StartsWith("- ", StringComparison.Ordinal))
                        {
                            var file = line[2..];
                            if (!string.IsNullOrEmpty(password))
                                file = file.Replace(password, "***", StringComparison.Ordinal);
                            progress.Report(new("正在解压：" + file, lastPercent));
                        }
                        live.Clear();
                    }
                    else
                    {
                        if (live.Length < 4096)
                            live.Append(ch);
                        if (ch == '%')
                        {
                            var match = Regex.Match(live.ToString(), @"(\d{1,3})%$");
                            if (match.Success && int.TryParse(match.Groups[1].Value, out var percent) && percent <= 100 && lastPercent != percent)
                            {
                                lastPercent = percent;
                                progress.Report(new("正在解压…", percent));
                            }
                        }
                    }
                }
            }
        }
        return text.ToString();
    }
    static void CheckExit(ProcessResult result, string? password, bool encrypted = false)
    {
        if (result.ExitCode == 0)
            return;
        // Never expose raw engine output: file names and error text can include supplied secrets.
        var diagnostics = DiagnosticRecords(result.Output).Concat(DiagnosticRecords(result.Error)).ToArray();
        bool Has(string grammar) => diagnostics.Any(line => Regex.IsMatch(line, "^(?:" + grammar + @")(?:[.?]?(?:$| : ))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        if (Has(@"Missing volume|Cannot find volume"))
            throw new ArchiveException(EngineFailure.MissingVolumes, "引擎检测到缺失分卷，请补齐所有分卷后重试。");
        if (Has(@"(?:Can ?not open encrypted archive\. |Data Error in encrypted file\. )?Wrong password|The password is incorrect|Password is incorrect"))
            throw new ArchiveException(password == null ? EngineFailure.NeedsPassword : EngineFailure.PasswordOrCorruption, "需要密码，或密码错误／加密数据损坏。请核对后重试。");
        if (Has(@"Not enough space|There is not enough space on the disk|The disk is full|Disk is full"))
            throw new ArchiveException(EngineFailure.DiskFull, "磁盘空间不足，未完成数据已保留。");
        if (Has("Access is denied"))
            throw new ArchiveException(EngineFailure.AccessDenied, "没有读写权限，请检查文件占用及目录权限。");
        if (encrypted && Has(@"CRC Failed|Data Error"))
            throw new ArchiveException(EngineFailure.PasswordOrCorruption, "密码错误或加密数据损坏；仅凭 CRC 错误无法区分。");
        throw new ArchiveException(EngineFailure.CorruptArchive, "归档无法完整读取或校验失败，请检查文件完整性及分卷是否齐全。");
    }
    static IEnumerable<string> DiagnosticRecords(string text)
    {
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("ERROR: ", StringComparison.Ordinal))
            {
                line = line[7..];
                // Listing errors can be "ERROR: <absolute archive path> : <diagnostic>".
                // A filename cannot contain the colon delimiter on Windows. Never search
                // the path itself, nor a path appended after an extraction diagnostic.
                if (Path.IsPathRooted(line))
                {
                    int delimiter = line.IndexOf(" : ", StringComparison.Ordinal);
                    if (delimiter < 0)
                        continue;
                    line = line[(delimiter + 3)..];
                }
                yield return line;
            }
            else if (line.StartsWith("Open ERROR: ", StringComparison.Ordinal))
                yield return line[12..];
            else if (!Path.IsPathRooted(line) && !line.Contains(" = ", StringComparison.Ordinal))
                yield return line;
        }
    }

    static ArchiveListing ParseListing(string output)
    {
        var normalized = output.Replace("\r\n", "\n");
        normalized = Regex.Replace(normalized, @"\n\nWarnings: [0-9]+\s*\z", "\n");
        int boundary = normalized.IndexOf("\n----------\n", StringComparison.Ordinal);
        if (boundary < 0)
            throw new ArchiveException(EngineFailure.CorruptArchive, "归档目录信息不完整。");
        var header = normalized[..boundary];
        var types = Regex.Matches(header, @"(?m)^Type = (.+)$").Select(m => m.Groups[1].Value.Trim()).ToArray();
        var supported = types.LastOrDefault();
        var format = supported switch
        {
            "zip" => ArchiveFormat.Zip,
            "7z" => ArchiveFormat.SevenZip,
            "Rar" or "Rar5" => ArchiveFormat.Rar,
            _ => ArchiveFormat.Unknown
        };
        if (format == ArchiveFormat.Unknown || types.Any(t => t is not ("Split" or "zip" or "7z" or "Rar" or "Rar5")))
            throw new ArchiveException(EngineFailure.Unsupported, "仅支持真实 ZIP、7z、RAR 归档及其分卷；普通程序不会执行或解包。");
        var entries = new List<ArchiveEntry>();
        var seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool encrypted = false;
        foreach (var block in normalized[(boundary + "\n----------\n".Length)..].Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var raw in block.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var at = raw.IndexOf(" = ", StringComparison.Ordinal);
                if (at <= 0 || !fields.TryAdd(raw[..at], raw[(at + 3)..]))
                    throw new ArchiveException(EngineFailure.UnsafePath, "归档元数据包含换行、重复字段或无法安全解析的内容。");
            }
            if (fields.Count == 0)
                continue;
            if (!fields.TryGetValue("Path", out var path) || !fields.TryGetValue("Size", out var sizeText) || !long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var size))
                throw new ArchiveException(EngineFailure.UnsafePath, "归档缺少明确的路径或展开大小，无法安全预检。");
            var directory = fields.GetValueOrDefault("Folder") == "+" || fields.GetValueOrDefault("Attributes", "").StartsWith('D');
            var attr = fields.GetValueOrDefault("Attributes", "");
            bool link = fields.Keys.Any(k => k.Contains("Link", StringComparison.OrdinalIgnoreCase) && fields[k].Length > 0)
                || Regex.IsMatch(attr, @"(^|\s)l[rwx-]{9}") || fields.ContainsKey("Reparse");
            var entry = new ArchiveEntry(path, size, directory, link);
            ArchiveSafety.ValidateEntry(entry);
            var canonical = path.Replace('/', '\\').TrimEnd('\\');
            if (!seen.TryAdd(canonical, directory))
                throw new ArchiveException(EngineFailure.UnsafePath, "归档含重名或大小写冲突路径。");
            entries.Add(entry);
            if (entries.Count > MaxEntries)
                throw new ArchiveException(EngineFailure.Unsupported, "归档条目超过安全处理上限，请手动处理。");
            encrypted |= fields.GetValueOrDefault("Encrypted") == "+";
        }
        foreach (var name in seen.Keys)
        {
            int at = name.LastIndexOf('\\');
            while (at > 0)
            {
                var parent = name[..at];
                if (seen.TryGetValue(parent, out var dir) && !dir)
                    throw new ArchiveException(EngineFailure.UnsafePath, "归档文件与目录路径冲突。");
                at = parent.LastIndexOf('\\');
            }
        }
        return new(format, entries, encrypted);
    }
}

internal static class ArchiveSafety
{
    public static void ValidateEntry(ArchiveEntry entry)
    {
        if (entry.IsLink)
            throw new ArchiveException(EngineFailure.UnsafePath, "归档包含符号链接、硬链接或重解析点，已停止。");
        var path = entry.Path;
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.StartsWith('\\') || Path.IsPathRooted(path) || path.Any(c => c < 32) || path.IndexOfAny([':', '"', '<', '>', '|', '?', '*']) >= 0 || entry.Size < 0)
            throw new ArchiveException(EngineFailure.UnsafePath, "归档含绝对路径、非法字符或不确定大小。");
        foreach (var part in path.Replace('\\', '/').TrimEnd('/').Split('/'))
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|CLOCK\$|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase))
                throw new ArchiveException(EngineFailure.UnsafePath, "归档含路径穿越或 Windows 保留名称，已停止。");
        }
    }
    public static void CheckParents(string path)
    {
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArchiveException(EngineFailure.UnsafePath, "输入或输出路径经过链接／重解析点，已停止以保护其他文件。");
    }
    public static void RequireSpace(string destination, long bytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(destination))!;
            var drive = new DriveInfo(root);
            if (!drive.IsReady || bytes > drive.AvailableFreeSpace - 32L * 1024 * 1024)
                throw new ArchiveException(EngineFailure.DiskFull, "磁盘可用空间不足以完成预检／解压（含安全余量）。");
        }
        catch (ArgumentException) { throw new ArchiveException(EngineFailure.DiskFull, "无法确认目标磁盘的可用空间，请选择本地磁盘。"); }
        catch (IOException) { throw new ArchiveException(EngineFailure.DiskFull, "无法确认目标磁盘的可用空间，请检查磁盘连接。"); }
    }
}
