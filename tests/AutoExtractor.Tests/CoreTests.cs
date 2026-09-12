using AutoExtractor.Core;
using System.Text.Json;

namespace AutoExtractor.Tests;

public static partial class CoreTests
{
    public static void Register()
    {
        RegisterRetryIdentityTests();
        Add("ordinary numbered PE is not an archive candidate", async d =>
        {
            byte[] pe = new byte[512];
            pe[0] = 77;
            pe[1] = 90;
            pe[60] = 64;
            pe[64] = 80;
            pe[65] = 69;
            var p = Put(d, "app2026.exe", pe);
            TestRunner.Check(SignatureDetector.Detect(p).IsExecutable);
            TestRunner.Check((await new ArchiveScanner().ScanAsync([d])).Candidates.Count == 0);
        });
        Add("RAR part names cannot combine independent ZIP archives", async d =>
        {
            Put(d, "game.part1.rar.jpg", CompleteZip());
            Put(d, "game.part2.rar.jpg", CompleteZip());
            var c = (await new ArchiveScanner().ScanAsync([d])).Candidates.Single();
            TestRunner.Check(c.Status != CandidateStatus.Ready);
        });
        Add("legacy RAR names require native volume metadata", async d =>
        {
            byte[] standalone = [82, 97, 114, 33, 26, 7, 0, 0, 0, 0x73, 0, 0, 13, 0, .. new byte[6]];
            Put(d, "game.rar", standalone);
            Put(d, "game.r00", standalone);
            var c = (await new ArchiveScanner().ScanAsync([d])).Candidates.Single();
            TestRunner.Check(c.Status != CandidateStatus.Ready);
        });
        Add("unrecognized first RAR cannot borrow later signature", async d =>
        {
            Put(d, "game.part1.rar", new byte[40]);
            Put(d, "game.part2.rar", Rar5(1));
            var c = (await new ArchiveScanner().ScanAsync([d])).Candidates.Single();
            TestRunner.Check(c.Status == CandidateStatus.NeedsConfirmation);
        });
        foreach (var overlap in new[] { "target", "source", "duplicate" })
            Add("tampered journal rejects " + overlap + " overlap before any mutation", async d =>
            {
                var a = Put(d, "a.jpg", [1]);
                var b = Put(d, "b.jpg", [2]);
                var c = Put(d, ".autoextractor-original.stage", [3]);
                var service = new RenameService();
                var result = await service.ApplyAsync(Candidate(a, "a.zip") with
                {
                    Members = [new(a, "a.zip", 0), new(b, "b.zip", 1), new(c, ".autoextractor-shared.stage", 2)]
                });
                var journal = JsonSerializer.Deserialize<RenameJournal>(File.ReadAllText(result.JournalPath!))!;
                journal.Entries[1] = overlap switch
                {
                    "target" => journal.Entries[1] with { StagingPath = journal.Entries[2].TargetPath },
                    "source" => journal.Entries[1] with { StagingPath = journal.Entries[2].SourcePath },
                    _ => journal.Entries[1] with { TargetPath = journal.Entries[2].TargetPath }
                };
                File.WriteAllText(result.JournalPath!, JsonSerializer.Serialize(journal));
                var before = Directory.GetFiles(d, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
                await TestRunner.ThrowsAsync<IOException>(() => service.RestoreAsync(result.JournalPath!));
                var after = Directory.GetFiles(d, "*", SearchOption.AllDirectories);
                TestRunner.Check(before.Count == after.Length && after.All(p => before.TryGetValue(p, out var bytes) && bytes.SequenceEqual(File.ReadAllBytes(p))), "Restore changed files before rejecting invalid transaction");
            });
        Add("byte split ZIP final central directory is not independent archive", async d =>
        {
            Put(d, "game.001.jpg", Zip());
            Put(d, "game.002.mp4", [.. new byte[50], 80, 75, 5, 6, .. new byte[18]]);
            var c = (await new ArchiveScanner().ScanAsync([d])).Candidates.Single();
            TestRunner.Check(c.VolumeKind == VolumeKind.ByteSplit && c.Status == CandidateStatus.Ready);
        });
        Add("native numbered archive remains single", async d =>
        {
            Put(d, "holiday2024.zip", Zip());
            var c = (await new ArchiveScanner().ScanAsync([d])).Candidates.Single();
            TestRunner.Check(c.VolumeKind == VolumeKind.Single && c.Status == CandidateStatus.Ready);
        });
        Add("RAR5 metadata validates numbered disguised volumes", async d =>
        {
            var p = Put(d, "game01.jpg", Rar5(0));
            Put(d, "game02.mp4", Rar5(1));
            TestRunner.Check(SignatureDetector.Detect(p).IsVolume && SignatureDetector.Detect(p).VolumeNumber == 0);
            var c = (await new ArchiveScanner().ScanAsync([d])).Candidates.Single();
            TestRunner.Check(c.VolumeKind == VolumeKind.RarParts && c.Status == CandidateStatus.Ready);
            File.WriteAllBytes(Path.Combine(d, "game02.mp4"), Rar5(3));
            c = (await new ArchiveScanner().ScanAsync([d])).Candidates.Single();
            TestRunner.Check(c.Status != CandidateStatus.Ready);
        });
        Add("fake SFX RAR marker ignored", async d =>
        {
            byte[] bytes = new byte[512];
            bytes[0] = 77;
            bytes[1] = 90;
            bytes[60] = 64;
            bytes[64] = 80;
            bytes[65] = 69;
            new byte[] { 82, 97, 114, 33, 26, 7, 1, 0 }.CopyTo(bytes, 300);
            var p = Put(d, "normal.exe", bytes);
            TestRunner.Check(SignatureDetector.Detect(p).Format == ArchiveFormat.Unknown);
            TestRunner.Check((await new ArchiveScanner().ScanAsync([d])).Candidates.Count == 0);
        });
        Add("pre-cancel leaves files untouched", async d =>
        {
            var p = Put(d, "a.jpg", Zip());
            using var c = new CancellationTokenSource();
            c.Cancel();
            await TestRunner.ThrowsAsync<OperationCanceledException>(() => new RenameService().ApplyAsync(Candidate(p, "a.zip"), c.Token));
            await TestRunner.ThrowsAsync<OperationCanceledException>(() => new ArchiveScanner().ScanAsync([d], c.Token));
            TestRunner.Check(File.Exists(p));
        });
        Add("restore collision leaves all target files", async d =>
        {
            var p = Put(d, "a.jpg", Zip());
            var s = new RenameService();
            var r = await s.ApplyAsync(Candidate(p, "a.zip"));
            Put(d, "a.jpg", [7]);
            await TestRunner.ThrowsAsync<IOException>(() => s.RestoreAsync(r.JournalPath!));
            TestRunner.Check(File.ReadAllBytes(p)[0] == 7 && File.Exists(Path.Combine(d, "a.zip")));
        });
        Add("disguised signatures and genuine media", async d =>
        {
            var p = Put(d, "包.jpg", [0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c, 0, 4, .. new byte[24]]);
            TestRunner.Check(SignatureDetector.Detect(p).Format == ArchiveFormat.SevenZip);
            Put(d, "photo.jpg", [0xff, 0xd8, 0xff, 0xe0, 0, 0]);
            var result = await new ArchiveScanner().ScanAsync([d]);
            TestRunner.Check(result.Candidates.Count == 1);
        });
        Add("numbered disguised byte split and sibling scope", async d =>
        {
            var p = Put(d, "game01.jpg", Zip());
            Put(d, "game02.mp4", new byte[100]);
            Put(d, "other.tmp", Zip());
            var result = await new ArchiveScanner().ScanAsync([p]);
            TestRunner.Check(result.Candidates.Count == 1);
            var c = result.Candidates[0];
            TestRunner.Check(c.Members.Count == 2 && c.VolumeKind == VolumeKind.ByteSplit && c.Status == CandidateStatus.Ready);
            TestRunner.Check(c.Members[0].RestoredName.EndsWith(".zip.001") && c.Members[1].RestoredName.EndsWith(".zip.002"));
        });
        Add("gaps and duplicate sequence stay unready", async d =>
        {
            Put(d, "game.001.jpg", Zip());
            Put(d, "game.003.jpg", new byte[20]);
            var r = await new ArchiveScanner().ScanAsync([d]);
            TestRunner.Check(r.Candidates.Single().Status == CandidateStatus.MissingVolumes);
            Put(d, "game.001.mp4", new byte[20]);
            r = await new ArchiveScanner().ScanAsync([d]);
            TestRunner.Check(r.Candidates.Single().Status != CandidateStatus.Ready);
        });
        Add("unknown continuation requires confirmation", async d =>
        {
            Put(d, "game.002.jpg", new byte[20]);
            Put(d, "game.003.jpg", new byte[30]);
            var r = await new ArchiveScanner().ScanAsync([d]);
            TestRunner.Check(r.Candidates.Single().Status == CandidateStatus.NeedsConfirmation);
        });
        Add("zip split uses final zip entry", async d =>
        {
            Put(d, "game.z01", [0x50, 0x4b, 7, 8, .. Zip()]);
            Put(d, "game.zip", [0x50, 0x4b, 5, 6, .. new byte[18]]);
            var c = (await new ArchiveScanner().ScanAsync([d])).Candidates.Single();
            TestRunner.Check(c.VolumeKind == VolumeKind.ZipSplit && c.EntryPath.EndsWith("game.zip"));
        });
        Add("native payload and output ignored", async d =>
        {
            Put(d, "app.jar", Zip());
            Directory.CreateDirectory(Path.Combine(d, "AutoExtractor_Output"));
            Put(Path.Combine(d, "AutoExtractor_Output"), "a.tmp", Zip());
            TestRunner.Check((await new ArchiveScanner().ScanAsync([d])).Candidates.Count == 0);
        });
        Add("rename roundtrip preserves bytes", async d =>
        {
            var p = Put(d, "包.jpg", Zip());
            var before = File.ReadAllBytes(p);
            var service = new RenameService();
            var r = await service.ApplyAsync(Candidate(p, "包.zip"));
            TestRunner.Check(!File.Exists(p) && File.Exists(r.Candidate.EntryPath) && r.JournalPath != null);
            TestRunner.Check(service.FindJournals(d).Count == 1);
            await service.RestoreAsync(r.JournalPath!);
            TestRunner.Check(File.ReadAllBytes(p).SequenceEqual(before) && !File.Exists(Path.Combine(d, "包.zip")));
        });
        Add("rename conflict leaves source untouched", async d =>
        {
            var p = Put(d, "a.jpg", Zip());
            Put(d, "a.zip", [1, 2]);
            await TestRunner.ThrowsAsync<IOException>(() => new RenameService().ApplyAsync(Candidate(p, "a.zip")));
            TestRunner.Check(File.Exists(p) && File.ReadAllBytes(Path.Combine(d, "a.zip")).SequenceEqual(new byte[] { 1, 2 }));
        });
        Add("modified target prevents whole restore", async d =>
        {
            var p = Put(d, "a.jpg", Zip());
            var q = Put(d, "b.jpg", [3, 4]);
            var s = new RenameService();
            var c = Candidate(p, "a.zip") with
            {
                Members = [new(p, "a.zip", 0), new(q, "b.zip", 1)]
            };
            var r = await s.ApplyAsync(c);
            File.WriteAllBytes(Path.Combine(d, "b.zip"), [8]);
            await TestRunner.ThrowsAsync<IOException>(() => s.RestoreAsync(r.JournalPath!));
            TestRunner.Check(!File.Exists(p) && File.Exists(Path.Combine(d, "a.zip")));
        });
        Add("two phase swaps and interrupted recovery", async d =>
        {
            var a = Put(d, "a", [1]);
            var b = Put(d, "b", [2]);
            var s = new RenameService();
            var r = await s.ApplyAsync(Candidate(a, "b") with
            {
                Members = [new(a, "b", 0), new(b, "a", 1)]
            });
            TestRunner.Check(File.ReadAllBytes(a)[0] == 2);
            await s.RestoreAsync(r.JournalPath!);
            TestRunner.Check(File.ReadAllBytes(a)[0] == 1);
            var r2 = await s.ApplyAsync(Candidate(a, "c"));
            var journal = JsonSerializer.Deserialize<RenameJournal>(File.ReadAllText(r2.JournalPath!))!;
            File.Move(journal.Entries[0].TargetPath, journal.Entries[0].StagingPath);
            await s.RestoreAsync(r2.JournalPath!);
            TestRunner.Check(File.ReadAllBytes(a)[0] == 1);
        });
    }
    static void Add(string name, Func<string, Task> run) => TestRunner.Cases.Add((name, async () => { var d = Path.Combine(Path.GetTempPath(), "AutoExtractor-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); try { await run(d); } finally { Directory.Delete(d, true); } }));
    static string Put(string d, string n, byte[] bytes)
    {
        var p = Path.Combine(d, n);
        File.WriteAllBytes(p, bytes);
        return p;
    }
    static byte[] Zip() => [0x50, 0x4b, 3, 4, 20, 0, .. new byte[24]];
    static byte[] CompleteZip()
    {
        using var m = new MemoryStream();
        using (var z = new System.IO.Compression.ZipArchive(m, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            using var w = new StreamWriter(z.CreateEntry("test.txt").Open());
            w.Write("independent archive");
        }
        return m.ToArray();
    }
    static byte[] Rar5(byte volume) => [82, 97, 114, 33, 26, 7, 1, 0, 0, 0, 0, 0, 4, 1, 0, 3, volume];
    static ArchiveCandidate Candidate(string p, string target) => new() { DisplayName = "test", Format = ArchiveFormat.Zip, Status = CandidateStatus.Ready, Members = [new(p, target, 0)] };
}


public static partial class CoreTests
{
    static void RegisterRetryIdentityTests()
    {
        Add("retry missing input never substitutes unrelated restored filename", async d => { var original = Put(d, "a.tmp", CompleteZip()); var target = Put(d, "a.zip", [1, 2, 3]); var candidate = Candidate(original, "a.zip"); File.Delete(original); await TestRunner.ThrowsAsync<IOException>(() => new RenameService().ResolveAppliedCandidateAsync(candidate)); TestRunner.Check(File.ReadAllBytes(target).SequenceEqual(new byte[] { 1, 2, 3 })); });
        Add("retry resolves same candidate committed journal and preserves identity", async d => { var original = Put(d, "a.tmp", CompleteZip()); var second = Put(d, "b.tmp", [4, 5, 6]); var candidate = Candidate(original, "a.zip") with { Members = [new(original, "a.zip", 0), new(second, "b.zip", 1)] }; var service = new RenameService(); var renamed = await service.ApplyAsync(candidate); var journal = JsonSerializer.Deserialize<RenameJournal>(File.ReadAllText(renamed.JournalPath!))!; TestRunner.Check(journal.State == "Applied" && journal.CandidateId == candidate.Id, "Journal must bind to exact task ID"); var resolved = await service.ResolveAppliedCandidateAsync(candidate); TestRunner.Check(resolved.Id == candidate.Id && resolved.Members.Select(m => m.SourcePath).SequenceEqual(renamed.Candidate.Members.Select(m => m.SourcePath))); TestRunner.Check(!File.Exists(original) && !File.Exists(second)); });
        Add("retry refuses modified committed target before remapping", async d => { var original = Put(d, "a.tmp", CompleteZip()); var candidate = Candidate(original, "a.zip"); var service = new RenameService(); var renamed = await service.ApplyAsync(candidate); File.WriteAllBytes(renamed.Candidate.EntryPath, [1, 2, 3, 4]); await TestRunner.ThrowsAsync<IOException>(() => service.ResolveAppliedCandidateAsync(candidate)); TestRunner.Check(File.ReadAllBytes(renamed.Candidate.EntryPath).SequenceEqual(new byte[] { 1, 2, 3, 4 })); });
        Add("retry cannot borrow journal from another task", async d => { var original = Put(d, "a.tmp", CompleteZip()); var candidate = Candidate(original, "a.zip"); var service = new RenameService(); await service.ApplyAsync(candidate); await TestRunner.ThrowsAsync<IOException>(() => service.ResolveAppliedCandidateAsync(candidate with { Id = Guid.NewGuid().ToString("N") })); });
        foreach (var kind in new[] { "mapping", "state", "legacy" })
            Add("retry rejects " + kind + " journal provenance", async d => { var original = Put(d, "a.tmp", CompleteZip()); var candidate = Candidate(original, "a.zip"); var service = new RenameService(); var renamed = await service.ApplyAsync(candidate); var journal = JsonSerializer.Deserialize<RenameJournal>(File.ReadAllText(renamed.JournalPath!))!; if (kind == "mapping") journal.Entries[0] = journal.Entries[0] with { SourcePath = Path.Combine(d, "other.tmp") }; else if (kind == "state") journal = journal with { State = "Staged" }; else journal = new RenameJournal(journal.State, journal.Entries); File.WriteAllText(renamed.JournalPath!, JsonSerializer.Serialize(journal)); await TestRunner.ThrowsAsync<IOException>(() => service.ResolveAppliedCandidateAsync(candidate)); TestRunner.Check(File.Exists(renamed.Candidate.EntryPath)); });
        Add("legacy journal without candidate ID still restores original names", async d => { var original = Put(d, "a.tmp", CompleteZip()); var service = new RenameService(); var result = await service.ApplyAsync(Candidate(original, "a.zip")); var journal = JsonSerializer.Deserialize<RenameJournal>(File.ReadAllText(result.JournalPath!))!; File.WriteAllText(result.JournalPath!, JsonSerializer.Serialize(new RenameJournal(journal.State, journal.Entries))); await service.RestoreAsync(result.JournalPath!); TestRunner.Check(File.Exists(original)); });
    }
}
