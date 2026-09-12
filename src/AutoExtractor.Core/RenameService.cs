using System.Security.Cryptography;
using System.Text.Json;

namespace AutoExtractor.Core;

public sealed record RenameJournal(string State, List<RenameJournalEntry> Entries, string? CandidateId = null);
public sealed record RenameJournalEntry(string SourcePath, string TargetPath, string StagingPath, long Length, string Sha256);
public sealed class RenameService : IRenameService
{
    static readonly SemaphoreSlim Gate = new(1, 1);
    static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    public async Task<RenameResult> ApplyAsync(ArchiveCandidate candidate, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (candidate.Members.Count == 0)
                throw new IOException("没有文件。");
            var entries = new List<RenameJournalEntry>();
            string id = Guid.NewGuid().ToString("N");
            foreach (var m in candidate.Members)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string source = Path.GetFullPath(m.SourcePath);
                CheckPath(source);
                if (m.RestoredName != Path.GetFileName(m.RestoredName) || m.RestoredName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || m.RestoredName is "." or ".." || m.RestoredName.EndsWith('.') || m.RestoredName.EndsWith(' '))
                    throw new IOException("目标文件名无效。");
                string target = Path.Combine(Path.GetDirectoryName(source)!, m.RestoredName);
                CheckPath(target);
                string stage = Path.Combine(Path.GetDirectoryName(source)!, ".autoextractor-" + id + "-" + entries.Count + ".stage");
                var identity = await Identity(source, cancellationToken);
                entries.Add(new(source, target, stage, identity.Length, identity.Hash));
            }
            ValidateUnique(entries.Select(x => x.SourcePath));
            ValidateUnique(entries.Select(x => x.TargetPath));
            ValidateStages(entries);
            if (entries.Select(x => Path.GetDirectoryName(x.SourcePath)).Distinct(Paths).Count() != 1)
                throw new IOException("一个重命名事务的所有文件必须位于同一目录。");
            var sources = entries.Select(x => x.SourcePath).ToHashSet(Paths);
            foreach (var e in entries)
            {
                if (Exists(e.StagingPath) || Exists(e.TargetPath) && !sources.Contains(e.TargetPath))
                    throw new IOException("目标路径已存在: " + e.TargetPath);
                await Verify(e.SourcePath, e, cancellationToken);
            }
            if (entries.All(x => string.Equals(x.SourcePath, x.TargetPath, StringComparison.Ordinal)))
                return new(candidate, null);
            string directory = Path.Combine(Path.GetDirectoryName(entries[0].SourcePath)!, ".autoextractor-journals");
            CheckPath(directory);
            Directory.CreateDirectory(directory);
            string journalPath = Path.Combine(directory, id + ".json");
            var journal = new RenameJournal("Prepared", entries, candidate.Id);
            Persist(journalPath, journal);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var e in entries)
                    File.Move(e.SourcePath, e.StagingPath);
                Persist(journalPath, journal with
                {
                    State = "Staged"
                });
                foreach (var e in entries)
                    File.Move(e.StagingPath, e.TargetPath);
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
                Members = candidate.Members.Select((m, i) => m with { SourcePath = entries[i].TargetPath }).ToArray()
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
            if (candidate.Members.Count == 0 || string.IsNullOrEmpty(candidate.Id))
                throw new IOException("任务缺少原始文件身份，请重新选择文件。");
            var expected = candidate.Members.Select(m =>
            {
                string source = Path.GetFullPath(m.SourcePath);
                CheckPath(source);
                if (m.RestoredName != Path.GetFileName(m.RestoredName) || m.RestoredName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || m.RestoredName is "." or ".." || m.RestoredName.EndsWith('.') || m.RestoredName.EndsWith(' '))
                    throw new IOException("任务中的还原名称无效。");
                string target = Path.Combine(Path.GetDirectoryName(source)!, m.RestoredName);
                CheckPath(target);
                return (Source: source, Target: target);
            }).ToArray();
            ValidateUnique(expected.Select(e => e.Source));
            ValidateUnique(expected.Select(e => e.Target));
            string directory = Path.GetDirectoryName(expected[0].Source)!;
            if (expected.Any(e => !Paths.Equals(Path.GetDirectoryName(e.Source), directory)))
                throw new IOException("一个任务的原始文件必须位于同一目录。");
            RenameJournal? committed = null;
            foreach (var path in FindJournals(directory))
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
                // Old records remain restorable, but lack evidence binding them to this queued task.
                if (journal is null || journal.CandidateId != candidate.Id || journal.State != "Applied")
                    continue;
                if (committed != null)
                    throw new IOException("本任务存在多份已提交改名记录，无法确定身份，请恢复原名或重新选择文件。");
                if (journal.Entries is null || journal.Entries.Count != expected.Length)
                    throw new IOException("改名记录与本任务的成员数量不一致，拒绝自动切换路径。");
                if (journal.Entries.Any(e => !Path.IsPathFullyQualified(e.SourcePath) || !Path.IsPathFullyQualified(e.TargetPath) || !Path.IsPathFullyQualified(e.StagingPath)))
                    throw new IOException("改名记录包含非绝对路径，拒绝自动切换路径。");
                var entries = journal.Entries.Select(e => e with { SourcePath = Path.GetFullPath(e.SourcePath), TargetPath = Path.GetFullPath(e.TargetPath), StagingPath = Path.GetFullPath(e.StagingPath) }).ToList();
                ValidateUnique(entries.Select(e => e.SourcePath));
                ValidateUnique(entries.Select(e => e.TargetPath));
                ValidateStages(entries);
                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    if (!Paths.Equals(entry.SourcePath, expected[i].Source) || !Paths.Equals(entry.TargetPath, expected[i].Target))
                        throw new IOException("改名记录的原始／目标路径与本任务不一致，拒绝自动切换路径。");
                    foreach (var endpoint in new[] { entry.SourcePath, entry.TargetPath, entry.StagingPath })
                    {
                        if (!Paths.Equals(Path.GetDirectoryName(endpoint), directory))
                            throw new IOException("改名记录路径超出原始目录。");
                        CheckPath(endpoint);
                    }
                    if (!Path.GetFileName(entry.StagingPath).StartsWith(".autoextractor-", StringComparison.Ordinal) || !entry.StagingPath.EndsWith(".stage", StringComparison.Ordinal) || Exists(entry.StagingPath))
                        throw new IOException("改名事务仍有暂存文件或记录无效，请先恢复原名。");
                }
                committed = journal with
                {
                    Entries = entries
                };
            }
            if (committed is null)
            {
                if (expected.Any(e => !File.Exists(e.Source)))
                    throw new IOException("原始输入已丢失，且没有属于本任务的已提交改名记录。不会改用同名压缩包；请重新选择文件。");
                return candidate;
            }
            var targets = committed.Entries.Select(e => e.TargetPath).ToHashSet(Paths);
            if (committed.Entries.Any(e => Exists(e.SourcePath) && !targets.Contains(e.SourcePath)))
                throw new IOException("原文件名已被其他文件重新占用，拒绝自动切换路径，请重新选择文件。");
            // Hash the full target set before returning any remapped member. No paths are guessed,
            // and no file or journal is modified by resolution.
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
        var journal = JsonSerializer.Deserialize<RenameJournal>(await File.ReadAllTextAsync(path, ct)) ?? throw new IOException("恢复日志无效。");
        if (journal.Entries.Count == 0)
            throw new IOException("恢复日志为空。");
        if (journal.Entries.Any(e => !Path.IsPathFullyQualified(e.SourcePath) || !Path.IsPathFullyQualified(e.TargetPath) || !Path.IsPathFullyQualified(e.StagingPath)))
            throw new IOException("恢复日志包含非绝对路径。");
        var entries = journal.Entries.Select(e => e with { SourcePath = Path.GetFullPath(e.SourcePath), TargetPath = Path.GetFullPath(e.TargetPath), StagingPath = Path.GetFullPath(e.StagingPath) }).ToList();
        journal = journal with
        {
            Entries = entries
        };
        string directory = Path.GetDirectoryName(Path.GetDirectoryName(path))!;
        if (Path.GetFileName(Path.GetDirectoryName(path)) != ".autoextractor-journals")
            throw new IOException("恢复日志位置无效。");
        ValidateUnique(entries.Select(x => x.SourcePath));
        ValidateUnique(entries.Select(x => x.TargetPath));
        ValidateStages(entries);
        foreach (var e in entries)
        {
            foreach (var p in new[] { e.SourcePath, e.TargetPath, e.StagingPath })
            {
                if (!Path.IsPathFullyQualified(p) || !Paths.Equals(Path.GetDirectoryName(p), directory))
                    throw new IOException("恢复日志路径超出原始目录。");
                CheckPath(p);
            }
            if (!Path.GetFileName(e.StagingPath).StartsWith(".autoextractor-", StringComparison.Ordinal) || !e.StagingPath.EndsWith(".stage", StringComparison.Ordinal))
                throw new IOException("暂存路径无效。");
        }
        // Resolve all identities before the first move; a modified/missing member aborts the entire restore.
        var locations = new List<string>();
        var used = new HashSet<string>(Paths);
        foreach (var e in entries)
        {
            string[] options = journal.State == "Restored" ? [e.SourcePath, e.StagingPath, e.TargetPath] : [e.StagingPath, e.TargetPath, e.SourcePath];
            string? found = null;
            foreach (string p in options.Distinct(Paths))
            if (File.Exists(p) && !used.Contains(p))
            {
                var identity = await Identity(p, ct);
                if (identity.Length == e.Length && identity.Hash == e.Sha256)
                {
                    found = p;
                    break;
                }
            }
            if (found == null)
                throw new IOException("文件丢失或内容已改变，未执行恢复: " + e.SourcePath);
            locations.Add(found);
            used.Add(found);
        }
        foreach (var e in entries)
        {
            if (Exists(e.SourcePath) && !used.Contains(e.SourcePath))
                throw new IOException("原文件名已被其他文件占用: " + e.SourcePath);
            if (Exists(e.StagingPath) && !used.Contains(e.StagingPath))
                throw new IOException("暂存路径冲突: " + e.StagingPath);
        }
        // Recheck the entire transaction after hashing, then ignore cancellation during mutations.
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
        foreach (var e in entries)
            File.Move(e.StagingPath, e.SourcePath);
        Persist(path, journal with
        {
            State = "Restored"
        });
    }
    public IReadOnlyList<string> FindJournals(string directory)
    {
        string p = Path.Combine(Path.GetFullPath(directory), ".autoextractor-journals");
        CheckPath(p);
        if (!Directory.Exists(p))
            return [];
        return Directory.EnumerateFiles(p, "*.json").Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);
    static void ValidateUnique(IEnumerable<string> paths)
    {
        var a = paths.ToArray();
        if (a.Distinct(Paths).Count() != a.Length)
            throw new IOException("重复的源路径或目标路径。");
    }
    static void ValidateStages(IReadOnlyList<RenameJournalEntry> entries)
    {
        ValidateUnique(entries.Select(x => x.StagingPath));
        var endpoints = entries.SelectMany(x => new[] { x.SourcePath, x.TargetPath }).ToHashSet(Paths);
        if (entries.Any(x => endpoints.Contains(x.StagingPath)))
            throw new IOException("暂存路径与事务的源路径或目标路径重叠。");
    }
    static async Task<(long Length, string Hash)> Identity(string p, CancellationToken ct)
    {
        CheckPath(p);
        await using var f = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        long length = f.Length;
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(f, ct));
        return (length, hash);
    }
    static async Task Verify(string p, RenameJournalEntry e, CancellationToken ct)
    {
        var id = await Identity(p, ct);
        if (id.Length != e.Length || id.Hash != e.Sha256)
            throw new IOException("文件在验证期间发生变化: " + p);
    }
    static void CheckPath(string path)
    {
        for (string? p = Path.GetFullPath(path); p != null; p = Path.GetDirectoryName(p))
        {
            try
            {
                if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("拒绝链接或重解析路径: " + p);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    static void Persist(string path, RenameJournal journal)
    {
        CheckPath(path);
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(journal, new JsonSerializerOptions { WriteIndented = true });
        using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(data);
            stream.Flush(true);
        }
        File.Move(tmp, path, true);
    }
}
