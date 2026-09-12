using System.Security.Cryptography;
using System.Text.Json;

namespace AutoExtractor.Core;

public sealed record RenameJournal(string State, List<RenameJournalEntry> Entries, string? CandidateId = null, string? ScopeRoot = null, string? DestinationDirectory = null);
public sealed record RenameJournalEntry(string SourcePath, string TargetPath, string StagingPath, long Length, string Sha256);

public sealed class RenameService : IRenameService
{
    static readonly SemaphoreSlim Gate = new(1, 1);
    static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    sealed record PlannedMember(string Source, string Target);
    sealed record TransactionPlan(string ScopeRoot, string DestinationDirectory, PlannedMember[] Members);

    public static string GetJournalDirectory(ArchiveCandidate candidate) => Path.Combine(CreatePlan(candidate).ScopeRoot, ".autoextractor-journals");

    public async Task<RenameResult> ApplyAsync(ArchiveCandidate candidate, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var plan = CreatePlan(candidate);
            var entries = new List<RenameJournalEntry>();
            string id = Guid.NewGuid().ToString("N");
            foreach (var member in plan.Members)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string stage = Path.Combine(Path.GetDirectoryName(member.Source)!, ".autoextractor-" + id + "-" + entries.Count + ".stage");
                var identity = await Identity(member.Source, cancellationToken);
                entries.Add(new(member.Source, member.Target, stage, identity.Length, identity.Hash));
            }
            string journalDirectory = Path.Combine(plan.ScopeRoot, ".autoextractor-journals");
            string journalPath = Path.Combine(journalDirectory, id + ".json");
            var journal = ValidateJournal(journalPath, new("Prepared", entries, candidate.Id, plan.ScopeRoot, plan.DestinationDirectory));
            var sources = entries.Select(x => x.SourcePath).ToHashSet(Paths);
            foreach (var entry in entries)
            {
                if (Exists(entry.StagingPath) || Exists(entry.TargetPath) && !sources.Contains(entry.TargetPath))
                    throw new IOException("目标路径已存在: " + entry.TargetPath);
                await Verify(entry.SourcePath, entry, cancellationToken);
            }
            if (entries.All(x => string.Equals(x.SourcePath, x.TargetPath, StringComparison.Ordinal)))
                return new(candidate, null);
            CheckPath(journalDirectory);
            Directory.CreateDirectory(journalDirectory);
            Persist(journalPath, journal);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Stages stay beside each original. Every final move is within one filesystem,
                // and RestoreCore can resolve an interrupted transaction across these directories.
                foreach (var entry in entries)
                    File.Move(entry.SourcePath, entry.StagingPath);
                Persist(journalPath, journal with
                {
                    State = "Staged"
                });
                foreach (var entry in entries)
                    File.Move(entry.StagingPath, entry.TargetPath);
                Persist(journalPath, journal with
                {
                    State = "Applied"
                });
            }
            catch (Exception original)
            {
                try
                {
                    await RestoreCore(journalPath, CancellationToken.None);
                }
                catch (Exception rollback) { throw new IOException("重命名中断，自动恢复未完成。请保留恢复日志: " + journalPath, new AggregateException(original, rollback)); }
                throw;
            }
            var updated = candidate with
            {
                Members = candidate.Members.Select((member, i) => member with { SourcePath = entries[i].TargetPath }).ToArray()
            };
            return new(updated, journalPath);
        }
        finally { Gate.Release(); }
    }

    /// <summary>Resolves an original task candidate only through its own committed rename transaction.</summary>
    public async Task<ArchiveCandidate> ResolveAppliedCandidateAsync(ArchiveCandidate candidate, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (string.IsNullOrEmpty(candidate.Id))
                throw new IOException("任务缺少原始文件身份，请重新选择文件。");
            var plan = CreatePlan(candidate);
            RenameJournal? committed = null;
            foreach (var path in FindJournals(plan.ScopeRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                CheckPath(path);
                if (new FileInfo(path).Length > 32L * 1024 * 1024)
                    throw new IOException("改名记录超出安全读取范围，请重新选择文件。");
                RenameJournal? journal;
                try
                {
                    journal = JsonSerializer.Deserialize<RenameJournal>(await File.ReadAllTextAsync(path, cancellationToken));
                }
                catch (JsonException) { continue; }
                // Legacy records without a task ID remain restorable but cannot authorize retry.
                if (journal is null || journal.CandidateId != candidate.Id || journal.State != "Applied")
                    continue;
                if (committed != null)
                    throw new IOException("本任务存在多份已提交改名记录，无法确定身份，请恢复原名或重新选择文件。");
                journal = ValidateJournal(path, journal);
                if (journal.Entries.Count != plan.Members.Length)
                    throw new IOException("改名记录与本任务的成员数量不一致，拒绝自动切换路径。");
                for (int i = 0; i < journal.Entries.Count; i++)
                {
                    var entry = journal.Entries[i];
                    if (!Paths.Equals(entry.SourcePath, plan.Members[i].Source) || !Paths.Equals(entry.TargetPath, plan.Members[i].Target))
                        throw new IOException("改名记录的原始／目标路径与本任务不一致，拒绝自动切换路径。");
                    if (Exists(entry.StagingPath))
                        throw new IOException("改名事务仍有暂存文件，请先恢复原名。");
                }
                committed = journal;
            }
            if (committed is null)
            {
                if (plan.Members.Any(member => !File.Exists(member.Source)))
                    throw new IOException("原始输入已丢失，且没有属于本任务的已提交改名记录。不会改用同名压缩包；请重新选择文件。");
                return candidate;
            }
            var targets = committed.Entries.Select(entry => entry.TargetPath).ToHashSet(Paths);
            if (committed.Entries.Any(entry => Exists(entry.SourcePath) && !targets.Contains(entry.SourcePath)))
                throw new IOException("原文件名已被其他文件重新占用，拒绝自动切换路径，请重新选择文件。");
            foreach (var entry in committed.Entries)
                await Verify(entry.TargetPath, entry, cancellationToken);
            return candidate with
            {
                Members = candidate.Members.Select((member, i) => member with { SourcePath = committed.Entries[i].TargetPath }).ToArray()
            };
        }
        finally { Gate.Release(); }
    }

    public async Task RestoreAsync(string journalPath, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            await RestoreCore(Path.GetFullPath(journalPath), cancellationToken);
        }
        finally { Gate.Release(); }
    }

    static async Task RestoreCore(string path, CancellationToken ct)
    {
        CheckPath(path);
        var journal = ValidateJournal(path, JsonSerializer.Deserialize<RenameJournal>(await File.ReadAllTextAsync(path, ct)) ?? throw new IOException("恢复日志无效。"));
        var entries = journal.Entries;
        foreach (var entry in entries)
            if (!Directory.Exists(Path.GetDirectoryName(entry.SourcePath)))
                throw new IOException("原始目录已不存在，请恢复目录后再试: " + Path.GetDirectoryName(entry.SourcePath));
        // Resolve the identity of every member before the first move.
        var locations = new List<string>();
        var used = new HashSet<string>(Paths);
        foreach (var entry in entries)
        {
            string[] options = journal.State == "Restored" ? [entry.SourcePath, entry.StagingPath, entry.TargetPath] : [entry.StagingPath, entry.TargetPath, entry.SourcePath];
            string? found = null;
            foreach (string option in options.Distinct(Paths))
            {
                if (!File.Exists(option) || used.Contains(option))
                    continue;
                var identity = await Identity(option, ct);
                if (identity.Length == entry.Length && identity.Hash == entry.Sha256)
                {
                    found = option;
                    break;
                }
            }
            if (found == null)
                throw new IOException("文件丢失或内容已改变，未执行恢复: " + entry.SourcePath);
            locations.Add(found);
            used.Add(found);
        }
        foreach (var entry in entries)
        {
            if (Exists(entry.SourcePath) && !used.Contains(entry.SourcePath))
                throw new IOException("原文件名已被其他文件占用: " + entry.SourcePath);
            if (Exists(entry.StagingPath) && !used.Contains(entry.StagingPath))
                throw new IOException("暂存路径冲突: " + entry.StagingPath);
        }
        for (int i = 0; i < entries.Count; i++)
            await Verify(locations[i], entries[i], ct);
        ct.ThrowIfCancellationRequested();
        Persist(path, journal with
        {
            State = "Restoring"
        });
        for (int i = 0; i < entries.Count; i++)
            if (!Paths.Equals(locations[i], entries[i].StagingPath))
                File.Move(locations[i], entries[i].StagingPath);
        foreach (var entry in entries)
            File.Move(entry.StagingPath, entry.SourcePath);
        Persist(path, journal with
        {
            State = "Restored"
        });
    }

    static TransactionPlan CreatePlan(ArchiveCandidate candidate)
    {
        if (candidate.Members.Count == 0 || candidate.EntryMemberIndex < 0 || candidate.EntryMemberIndex >= candidate.Members.Count)
            throw new IOException("任务没有有效的归档入口。");
        var sources = candidate.Members.Select(member => Path.GetFullPath(member.SourcePath)).ToArray();
        ValidateUnique(sources);
        string[] directories = sources.Select(source => Path.GetDirectoryName(source) ?? throw new IOException("源文件路径无效。")).ToArray();
        string scope = CommonScope(directories);
        string destination = directories[candidate.EntryMemberIndex];
        var planned = candidate.Members.Select((member, index) =>
        {
            ValidateName(member.RestoredName);
            var target = Path.Combine(destination, member.RestoredName);
            CheckPath(sources[index]);
            CheckPath(target);
            return new PlannedMember(sources[index], target);
        }).ToArray();
        ValidateUnique(planned.Select(member => member.Target));
        return new(scope, destination, planned);
    }

    // The common source ancestor is the complete transaction boundary. A grouping that would
    // require an entire drive/share as its scope, or a cross-filesystem copy/delete, is refused.
    static string CommonScope(IEnumerable<string> directories)
    {
        var values = directories.Select(Path.GetFullPath).Distinct(Paths).ToArray();
        if (values.Length == 0)
            throw new IOException("事务缺少原始目录。");
        var roots = values.Select(Path.GetPathRoot).Distinct(Paths).ToArray();
        if (roots.Length != 1)
            throw new IOException("跨磁盘分卷暂不支持自动整理，请先复制到同一磁盘下的独立目录。");
        string scope = values[0];
        while (!values.All(directory => WithinOrEqual(directory, scope)))
            scope = Path.GetDirectoryName(scope) ?? throw new IOException("无法确定共同原始目录。");
        if (values.Length > 1 && Paths.Equals(Path.TrimEndingDirectorySeparator(scope), Path.TrimEndingDirectorySeparator(roots[0]!)))
            throw new IOException("分卷范围覆盖整个磁盘或共享根目录，请先放入同一个独立父目录。");
        return scope;
    }

    static RenameJournal ValidateJournal(string path, RenameJournal journal)
    {
        CheckPath(path);
        if (!Paths.Equals(Path.GetFileName(Path.GetDirectoryName(path)), ".autoextractor-journals"))
            throw new IOException("恢复日志位置无效。");
        string anchor = Path.GetFullPath(Path.GetDirectoryName(Path.GetDirectoryName(path))!);
        if (journal.Entries is null || journal.Entries.Count == 0)
            throw new IOException("恢复日志为空。");
        if (journal.Entries.Any(entry => !Path.IsPathFullyQualified(entry.SourcePath) || !Path.IsPathFullyQualified(entry.TargetPath) || !Path.IsPathFullyQualified(entry.StagingPath)))
            throw new IOException("恢复日志包含非绝对路径。");
        var entries = journal.Entries.Select(entry => entry with { SourcePath = Path.GetFullPath(entry.SourcePath), TargetPath = Path.GetFullPath(entry.TargetPath), StagingPath = Path.GetFullPath(entry.StagingPath) }).ToList();
        ValidateUnique(entries.Select(entry => entry.SourcePath));
        ValidateUnique(entries.Select(entry => entry.TargetPath));
        ValidateStages(entries);
        bool legacy = journal.ScopeRoot is null && journal.DestinationDirectory is null;
        string scope = anchor, destination = anchor;
        if (!legacy)
        {
            if (journal.ScopeRoot is null || journal.DestinationDirectory is null || !Path.IsPathFullyQualified(journal.ScopeRoot) || !Path.IsPathFullyQualified(journal.DestinationDirectory))
                throw new IOException("恢复日志缺少完整的整理范围或目标目录。");
            scope = Path.GetFullPath(journal.ScopeRoot);
            destination = Path.GetFullPath(journal.DestinationDirectory);
            var sourceDirectories = entries.Select(entry => Path.GetDirectoryName(entry.SourcePath)!).ToArray();
            if (!Paths.Equals(scope, anchor) || !Paths.Equals(scope, CommonScope(sourceDirectories)) || !sourceDirectories.Contains(destination, Paths))
                throw new IOException("恢复日志的整理范围或目标目录与原始路径不一致。");
        }
        foreach (var entry in entries)
        {
            string sourceDirectory = Path.GetDirectoryName(entry.SourcePath)!;
            if (legacy && !Paths.Equals(sourceDirectory, anchor) || !WithinOrEqual(sourceDirectory, scope) || !Paths.Equals(Path.GetDirectoryName(entry.TargetPath), destination) || !Paths.Equals(Path.GetDirectoryName(entry.StagingPath), sourceDirectory))
                throw new IOException("恢复日志路径超出原始范围或暂存目录不属于对应源文件。");
            foreach (var endpoint in new[] { entry.SourcePath, entry.TargetPath, entry.StagingPath })
                CheckPath(endpoint);
            if (!Path.GetFileName(entry.StagingPath).StartsWith(".autoextractor-", StringComparison.Ordinal) || !entry.StagingPath.EndsWith(".stage", StringComparison.Ordinal))
                throw new IOException("暂存路径无效。");
            if (entry.Length < 0 || entry.Sha256 is null || entry.Sha256.Length != 64 || entry.Sha256.Any(character => !Uri.IsHexDigit(character)))
                throw new IOException("恢复日志中的文件身份无效。");
        }
        return journal with
        {
            Entries = entries,
            ScopeRoot = legacy ? null : scope,
            DestinationDirectory = legacy ? null : destination
        };
    }

    static bool WithinOrEqual(string directory, string scope) => Paths.Equals(directory, scope) || directory.StartsWith(Path.TrimEndingDirectorySeparator(scope) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    static void ValidateName(string name)
    {
        if (name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' '))
            throw new IOException("目标文件名无效。");
    }
    public IReadOnlyList<string> FindJournals(string directory)
    {
        string path = Path.Combine(Path.GetFullPath(directory), ".autoextractor-journals");
        CheckPath(path);
        return Directory.Exists(path) ? Directory.EnumerateFiles(path, "*.json").Order(StringComparer.OrdinalIgnoreCase).ToArray() : [];
    }
    static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    static void ValidateUnique(IEnumerable<string> paths)
    {
        var values = paths.ToArray();
        if (values.Distinct(Paths).Count() != values.Length)
            throw new IOException("重复的源路径或目标路径。");
    }
    static void ValidateStages(IReadOnlyList<RenameJournalEntry> entries)
    {
        ValidateUnique(entries.Select(entry => entry.StagingPath));
        var endpoints = entries.SelectMany(entry => new[] { entry.SourcePath, entry.TargetPath }).ToHashSet(Paths);
        if (entries.Any(entry => endpoints.Contains(entry.StagingPath)))
            throw new IOException("暂存路径与事务的源路径或目标路径重叠。");
    }
    static async Task<(long Length, string Hash)> Identity(string path, CancellationToken ct)
    {
        CheckPath(path);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        long length = file.Length;
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
        return (length, hash);
    }
    static async Task Verify(string path, RenameJournalEntry entry, CancellationToken ct)
    {
        var identity = await Identity(path, ct);
        if (identity.Length != entry.Length || identity.Hash != entry.Sha256)
            throw new IOException("文件在验证期间发生变化: " + path);
    }
    static void CheckPath(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("拒绝链接或重解析路径: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    static void Persist(string path, RenameJournal journal)
    {
        CheckPath(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(journal, new JsonSerializerOptions { WriteIndented = true });
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(data);
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }
}
