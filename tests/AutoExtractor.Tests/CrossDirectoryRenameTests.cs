using AutoExtractor.Core;
using System.Security.Cryptography;
using System.Text.Json;

namespace AutoExtractor.Tests;

public static class CrossDirectoryRenameTests
{
    public static void Register()
    {
        Add("cross-directory consolidation and restore preserve all original bytes", async root =>
        {
            var candidate = MakeCandidate(root);
            var original = candidate.Members.ToDictionary(m => m.SourcePath, m => File.ReadAllBytes(m.SourcePath));
            var service = new RenameService();
            var result = await service.ApplyAsync(candidate);
            var destination = Path.Combine(root, "1");
            TestRunner.Check(result.Candidate.Members.All(m => Path.GetDirectoryName(m.SourcePath) == destination));
            TestRunner.Check(result.Candidate.Members.Select(m => File.ReadAllBytes(m.SourcePath)).Zip(original.Values).All(p => p.First.SequenceEqual(p.Second)));
            TestRunner.Check(candidate.Members.All(m => !File.Exists(m.SourcePath)));
            TestRunner.Check(RenameService.GetJournalDirectory(candidate) == Path.Combine(root, ".autoextractor-journals"));
            TestRunner.Check(Path.GetDirectoryName(result.JournalPath) == RenameService.GetJournalDirectory(candidate));
            var journal = Read(result.JournalPath!);
            TestRunner.Check(journal.ScopeRoot == root && journal.DestinationDirectory == destination && journal.CandidateId == candidate.Id);
            TestRunner.Check(journal.Entries.All(e => Path.GetDirectoryName(e.StagingPath) == Path.GetDirectoryName(e.SourcePath)));
            await service.RestoreAsync(result.JournalPath!);
            TestRunner.Check(original.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)));
            TestRunner.Check(result.Candidate.Members.All(m => !File.Exists(m.SourcePath)));
        });
        Add("cross-directory ZIP terminal entry controls consolidation destination", async root =>
        {
            var c = MakeCandidate(root) with
            {
                VolumeKind = VolumeKind.ZipSplit,
                EntryMemberIndex = 1
            };
            c = c with
            {
                Members = [c.Members[0] with { RestoredName = "game.z01" }, c.Members[1] with { RestoredName = "game.zip" }]
            };
            var service = new RenameService();
            var result = await service.ApplyAsync(c);
            TestRunner.Check(result.Candidate.Members.All(m => Path.GetDirectoryName(m.SourcePath) == Path.Combine(root, "2")));
            await service.RestoreAsync(result.JournalPath!);
            TestRunner.Check(c.Members.All(m => File.Exists(m.SourcePath)));
        });
        Add("cross-directory destination collision causes no partial moves", async root =>
        {
            var c = MakeCandidate(root);
            var target = Path.Combine(root, "1", c.Members[1].RestoredName);
            File.WriteAllText(target, "unrelated target");
            var before = Snapshot(root);
            try
            {
                await new RenameService().ApplyAsync(c);
                throw new Exception("Expected destination conflict");
            }
            catch (IOException ex) { TestRunner.Check(ex.Message.Contains("目标路径已存在"), ex.Message); }
            CheckSnapshot(root, before);
        });
        Add("cross-directory pre-cancel leaves inputs untouched", async root =>
        {
            var c = MakeCandidate(root);
            var before = Snapshot(root);
            using var token = new CancellationTokenSource();
            token.Cancel();
            await TestRunner.ThrowsAsync<OperationCanceledException>(() => new RenameService().ApplyAsync(c, token.Token));
            CheckSnapshot(root, before);
        });
        Add("cross-directory retry resolves committed relocation with same task hashes", async root =>
        {
            var c = MakeCandidate(root);
            var service = new RenameService();
            var applied = await service.ApplyAsync(c);
            var resolved = await service.ResolveAppliedCandidateAsync(c);
            TestRunner.Check(resolved.Members.Select(m => m.SourcePath).SequenceEqual(applied.Candidate.Members.Select(m => m.SourcePath)));
            var target = resolved.Members[1].SourcePath;
            File.WriteAllText(target, "modified data");
            var before = Snapshot(root);
            await TestRunner.ThrowsAsync<IOException>(() => service.ResolveAppliedCandidateAsync(c));
            CheckSnapshot(root, before);
        });
        Add("cross-directory interrupted restore recovers source-local staging", async root =>
        {
            var c = MakeCandidate(root);
            var originals = Snapshot(root);
            var service = new RenameService();
            var applied = await service.ApplyAsync(c);
            var journal = Read(applied.JournalPath!);
            File.Move(journal.Entries[1].TargetPath, journal.Entries[1].StagingPath);
            File.WriteAllText(applied.JournalPath!, JsonSerializer.Serialize(journal with
            {
                State = "Restoring"
            }));
            await service.RestoreAsync(applied.JournalPath!);
            TestRunner.Check(originals.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)));
            TestRunner.Check(!journal.Entries.Any(e => File.Exists(e.StagingPath)));
        });
        foreach (var kind in new[] { "scope", "destination", "outside-source", "wrong-stage-parent", "legacy-downgrade" })
            Add("cross-directory rejects tampered " + kind + " journal before mutations", async root =>
            {
                var c = MakeCandidate(root);
                var service = new RenameService();
                var applied = await service.ApplyAsync(c);
                var journal = Read(applied.JournalPath!);
                if (kind == "scope")
                    journal = journal with
                    {
                        ScopeRoot = Path.GetDirectoryName(root)
                    };
                else if (kind == "destination")
                    journal = journal with
                    {
                        DestinationDirectory = Path.Combine(root, "2")
                    };
                else if (kind == "outside-source")
                    journal.Entries[1] = journal.Entries[1] with
                    {
                        SourcePath = Path.Combine(Path.GetDirectoryName(root)!, "outside-original")
                    };
                else if (kind == "wrong-stage-parent")
                    journal.Entries[1] = journal.Entries[1] with
                    {
                        StagingPath = Path.Combine(root, "1", ".autoextractor-wrong.stage")
                    };
                else
                    journal = journal with
                    {
                        ScopeRoot = null,
                        DestinationDirectory = null
                    };
                File.WriteAllText(applied.JournalPath!, JsonSerializer.Serialize(journal));
                var before = Snapshot(root);
                await TestRunner.ThrowsAsync<IOException>(() => service.RestoreAsync(applied.JournalPath!));
                CheckSnapshot(root, before);
            });
        Add("cross-directory rejects drive-wide and cross-volume scope without file access", async root =>
        {
            var service = new RenameService();
            var c = MakeCandidate(root);
            string volume = Path.GetPathRoot(root)!;
            var broad = c with
            {
                Members = [new(Path.Combine(volume, "scope-one", "first.tmp"), "group.zip.001", 0), new(Path.Combine(volume, "scope-two", "second.tmp"), "group.zip.002", 1)]
            };
            await TestRunner.ThrowsAsync<IOException>(() => Task.FromResult(RenameService.GetJournalDirectory(broad)));
            string anotherVolume = volume.StartsWith("C:", StringComparison.OrdinalIgnoreCase) ? "D:\\" : "C:\\";
            var cross = c with
            {
                Members = [c.Members[0], new(Path.Combine(anotherVolume, "other-scope", "second.tmp"), "group.zip.002", 1)]
            };
            await TestRunner.ThrowsAsync<IOException>(() => Task.FromResult(RenameService.GetJournalDirectory(cross)));
        });
    }
    static ArchiveCandidate MakeCandidate(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "1"));
        Directory.CreateDirectory(Path.Combine(root, "2"));
        var a = Path.Combine(root, "1", "first.tmp");
        var b = Path.Combine(root, "2", "second.tmp");
        File.WriteAllBytes(a, RandomNumberGenerator.GetBytes(311));
        File.WriteAllBytes(b, RandomNumberGenerator.GetBytes(173));
        return new()
        {
            DisplayName = "sibling group",
            Format = ArchiveFormat.Zip,
            VolumeKind = VolumeKind.ByteSplit,
            Status = CandidateStatus.Ready,
            Members = [new(a, "group.zip.001", 0), new(b, "group.zip.002", 1)]
        };
    }
    static RenameJournal Read(string path) => JsonSerializer.Deserialize<RenameJournal>(File.ReadAllText(path))!;
    static Dictionary<string, byte[]> Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
    static void CheckSnapshot(string root, Dictionary<string, byte[]> before)
    {
        var after = Snapshot(root);
        TestRunner.Check(after.Count == before.Count && before.All(p => after.TryGetValue(p.Key, out var bytes) && bytes.SequenceEqual(p.Value)), "Rejected operation changed files");
    }
    static void Add(string name, Func<string, Task> run) => TestRunner.Cases.Add((name, async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "AutoExtractor-cross-rename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await run(root);
        }
        finally { Directory.Delete(root, true); }
    }
    ));
}
