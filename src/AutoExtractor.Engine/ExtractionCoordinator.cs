using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AutoExtractor.Core;

namespace AutoExtractor.Engine;

public sealed class ExtractionCoordinator(IArchiveScanner scanner, IRenameService renameService, IArchiveEngine engine)
{
    public async Task<ExtractionResult> RunAsync(ArchiveCandidate candidate, ExtractionOptions options,
        Func<PasswordRequest, CancellationToken, Task<string?>> requestPassword,
        IProgress<ExtractionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (candidate.Members.Count == 0)
            throw new ArchiveException(EngineFailure.Unsupported, "没有可处理的归档成员。");
        if (candidate.Status != CandidateStatus.Ready)
            throw new ArchiveException(candidate.Status == CandidateStatus.MissingVolumes ? EngineFailure.MissingVolumes : EngineFailure.Unsupported, "分卷存在缺失或歧义，请先确认完整分组与顺序。");
        var baseDirectory = Path.GetFullPath(options.OutputDirectory ?? Path.Combine(Path.GetDirectoryName(candidate.EntryPath)!, "AutoExtractor_Output"));
        ArchiveSafety.CheckParents(baseDirectory);
        var taskDirectory = Path.Combine(baseDirectory, SafeName(candidate.DisplayName) + "_" + Guid.NewGuid().ToString("N")[..10]);
        var journals = new List<string>();
        var remaining = new List<ArchiveCandidate>();
        var messages = new List<string>();
        var passwords = new List<string>();
        var seenHashes = new HashSet<string>(StringComparer.Ordinal);
        int sequence = 0;
        var maxDepth = Math.Clamp(options.MaxDepth, 1, 10);
        try
        {
            await ProcessCandidate(candidate, 1, true);
            return new(taskDirectory, journals, remaining, messages);
        }
        finally { passwords.Clear(); }

        async Task ProcessCandidate(ArchiveCandidate current, int layer, bool topLevel)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (layer > maxDepth || current.Status != CandidateStatus.Ready)
            {
                remaining.Add(current);
                messages.Add(layer > maxDepth ? $"{current.DisplayName}：达到 {maxDepth} 层上限，保留待手动继续。" : $"{current.DisplayName}：分组需要人工确认，保留待处理。");
                return;
            }
            string? password = null;
            try
            {
                var fingerprint = await Fingerprint(current, cancellationToken);
                if (!seenHashes.Add(fingerprint))
                {
                    remaining.Add(current);
                    messages.Add($"{current.DisplayName}：内容重复，已停止重复展开，保留待检查。");
                    return;
                }
                progress?.Report(new($"第 {layer} 层：验证 {current.DisplayName}", null, layer));
                using (var aliases = await AliasSet.CreateAsync(current, cancellationToken))
                {
                    // List and test normalized aliases before modifying even one downloaded filename.
                    password = await WithPassword(aliases.EntryPath, current, layer, true);
                }
                var renamed = await renameService.ApplyAsync(current, cancellationToken);
                current = renamed.Candidate;
                if (renamed.JournalPath != null)
                    journals.Add(renamed.JournalPath);
                cancellationToken.ThrowIfCancellationRequested();
                ArchiveSafety.CheckParents(taskDirectory);
                Directory.CreateDirectory(taskDirectory);
                var layerName = $"{++sequence:D3}_第{layer:D2}层_{SafeName(current.DisplayName)}";
                var staging = Path.Combine(taskDirectory, layerName + "_incomplete_" + Guid.NewGuid().ToString("N")[..8]);
                var completed = Path.Combine(taskDirectory, layerName);
                Directory.CreateDirectory(staging);
                var layerProgress = progress == null ? null : new LayerProgress(progress, layer);
                try
                {
                    await engine.ExtractAsync(current.EntryPath, staging, password, layerProgress, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    ArchiveSafety.CheckParents(staging);
                    Directory.Move(staging, completed);
                }
                catch { messages.Add($"第 {layer} 层未完成，数据保留于：{staging}"); throw; }
                if (password != null && !passwords.Contains(password, StringComparer.Ordinal))
                    passwords.Add(password);
                messages.Add($"第 {layer} 层完成：{completed}");
                // Only discover descendants from this newly published directory.
                var scan = await scanner.ScanAsync([completed], cancellationToken);
                messages.AddRange(scan.Messages);
                var stops = options.SmartStop ? FindApplicationDirectories(completed, scan.Candidates) : [];
                foreach (var child in scan.Candidates.Where(c => c.Status != CandidateStatus.Ignored))
                {
                    if (stops.Any(dir => IsWithin(child.EntryPath, dir)))
                    {
                        remaining.Add(child);
                        messages.Add($"{child.DisplayName}：检测到程序及配套文件，已智能停止该子目录；需要时可手动继续。");
                    }
                    else
                        await ProcessCandidate(child, layer + 1, false);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (!topLevel && error is ArchiveException or IOException or UnauthorizedAccessException)
            {
                remaining.Add(current);
                var detail = error is ArchiveException ? error.Message : "文件读写失败，请检查权限、磁盘空间或文件占用。";
                messages.Add($"第 {layer} 层未完成：{current.DisplayName}。{detail}");
            }
        }

        async Task<string?> WithPassword(string path, ArchiveCandidate candidateToValidate, int layer, bool fullValidation)
        {
            var attempts = new Queue<string?>(new string?[] { null }.Concat(passwords.AsEnumerable().Reverse()));
            bool previousFailed = false;
            int prompted = 0;
            string message = "归档需要密码。密码仅保留在本任务内存中。";
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? attempt;
                if (attempts.Count > 0)
                    attempt = attempts.Dequeue();
                else
                {
                    if (++prompted > 20)
                        throw new ArchiveException(EngineFailure.PasswordOrCorruption, "密码重试次数达到上限，请确认密码与归档完整性后重试任务。");
                    attempt = await requestPassword(new(candidateToValidate.DisplayName, layer, previousFailed, message), cancellationToken);
                    if (attempt == null)
                        throw new OperationCanceledException("已取消此归档的密码输入。", cancellationToken);
                }
                try
                {
                    var listing = engine is SevenZipEngine groupEngine
                        ? await groupEngine.ValidateGroupAsync(path, candidateToValidate, attempt, cancellationToken)
                        : await engine.ListAsync(path, attempt, cancellationToken);
                    if (listing.Format != candidateToValidate.Format || (engine is not SevenZipEngine && (candidateToValidate.Members.Count != 1 || candidateToValidate.VolumeKind != VolumeKind.Single)))
                        throw new ArchiveException(EngineFailure.Unsupported, "引擎无法确认声明格式或全部分卷成员，尚未还原文件名。");
                    foreach (var entry in listing.Entries)
                        ArchiveSafety.ValidateEntry(entry);
                    if (listing.Format == ArchiveFormat.Unknown)
                        throw new ArchiveException(EngineFailure.Unsupported, "不支持的归档格式。");
                    if (fullValidation && engine is SevenZipEngine sevenZip)
                        await sevenZip.TestAsync(path, attempt, cancellationToken);
                    return attempt;
                }
                catch (ArchiveException error) when (error.Failure is EngineFailure.NeedsPassword or EngineFailure.PasswordOrCorruption)
                {
                    previousFailed = attempt != null;
                    message = error.Message;
                }
            }
        }
    }

    sealed class LayerProgress(IProgress<ExtractionProgress> parent, int layer) : IProgress<ExtractionProgress>
    {
        public void Report(ExtractionProgress value) => parent.Report(value with { Layer = layer });
    }
    static string SafeName(string name)
    {
        var result = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').Take(60).ToArray());
        return string.IsNullOrWhiteSpace(result) ? "archive" : result;
    }
    static bool IsWithin(string path, string directory) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    static List<string> FindApplicationDirectories(string root, IReadOnlyList<ArchiveCandidate> candidates)
    {
        var archivePaths = candidates.SelectMany(c => c.Members).Select(m => Path.GetFullPath(m.SourcePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var path in Directory.EnumerateFiles(root, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            if (archivePaths.Contains(Path.GetFullPath(path)))
                continue;
            using var input = File.OpenRead(path);
            if (input.ReadByte() != 'M' || input.ReadByte() != 'Z')
                continue;
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.EnumerateFileSystemEntries(directory).Take(2).Count() > 1)
                result.Add(directory);
        }
        return result;
    }
    static async Task<string> Fingerprint(ArchiveCandidate candidate, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        foreach (var member in candidate.Members.OrderBy(m => m.Order))
        {
            ArchiveSafety.CheckParents(member.SourcePath);
            await using var input = new FileStream(member.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
            int count;
            while ((count = await input.ReadAsync(buffer, token)) != 0)
                hash.AppendData(buffer, 0, count);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    sealed class AliasSet : IDisposable
    {
        readonly string directory;
        public string EntryPath
        {
            get;
        }
        AliasSet(string directory, string entryPath)
        {
            this.directory = directory;
            EntryPath = entryPath;
        }
        public static async Task<AliasSet> CreateAsync(ArchiveCandidate candidate, CancellationToken token)
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(candidate.EntryPath))!;
            ArchiveSafety.CheckParents(parent);
            var directory = Path.Combine(parent, ".AutoExtractor_validate_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var owned = new AliasSet(directory, Path.Combine(directory, candidate.Members[candidate.EntryMemberIndex].RestoredName));
            try
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var member in candidate.Members)
                {
                    token.ThrowIfCancellationRequested();
                    ArchiveSafety.ValidateEntry(new(member.RestoredName, 0, false));
                    if (Path.GetFileName(member.RestoredName) != member.RestoredName || !names.Add(member.RestoredName))
                        throw new ArchiveException(EngineFailure.UnsafePath, "分卷还原名称无效或冲突。");
                    ArchiveSafety.CheckParents(member.SourcePath);
                    var alias = Path.Combine(directory, member.RestoredName);
                    if (!OperatingSystem.IsWindows() || !CreateHardLink(alias, Path.GetFullPath(member.SourcePath), IntPtr.Zero))
                    {
                        ArchiveSafety.RequireSpace(directory, new FileInfo(member.SourcePath).Length);
                        await using var source = new FileStream(member.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
                        await using var destination = new FileStream(alias, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
                        await source.CopyToAsync(destination, token);
                    }
                }
                return owned;
            }
            catch { owned.Dispose(); throw; }
        }
        public void Dispose()
        {
            // This unique directory contains only aliases created by this invocation, never downloads.
            try
            {
                ArchiveSafety.CheckParents(directory);
                foreach (var file in Directory.EnumerateFiles(directory))
                    File.Delete(file);
                Directory.Delete(directory, recursive: false);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
    }
}
