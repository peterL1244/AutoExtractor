using System.IO.Compression;
using AutoExtractor.Core;
using AutoExtractor.Engine;

namespace AutoExtractor.Tests;

public static class CrossDirectoryEngineTests
{
    public static void Register()
    {
        Add("cross-directory discovered ZIP volumes consolidate extract and restore", async root =>
        {
            var bytes = ZipBytes(("verified.txt", "scoped discovery and extraction"u8.ToArray()));
            Directory.CreateDirectory(Path.Combine(root, "1"));
            Directory.CreateDirectory(Path.Combine(root, "2"));
            int cut = bytes.Length / 2;
            var first = Path.Combine(root, "1", "group.001.tmp");
            var second = Path.Combine(root, "2", "group.002.tmp");
            File.WriteAllBytes(first, bytes[..cut]);
            File.WriteAllBytes(second, bytes[cut..]);
            var scanner = new ArchiveScanner();
            var scanned = await scanner.ScanAsync([root]);
            TestRunner.Check(scanned.Candidates.Count == 1 && scanned.Candidates[0].Status == CandidateStatus.Ready);
            var candidate = scanned.Candidates[0];
            var renamer = new RenameService();
            var coordinator = new ExtractionCoordinator(scanner, renamer, new SevenZipEngine(EnginePath()));
            var result = await coordinator.RunAsync(candidate, new(Path.Combine(root, "output")), (_, _) => throw new Exception("Fixture is not encrypted"));
            TestRunner.Check(result.RemainingCandidates.Count == 0 && result.Journals.Count == 1, string.Join(";", result.Messages));
            TestRunner.Check(File.ReadAllText(Directory.GetFiles(result.OutputDirectory, "verified.txt", SearchOption.AllDirectories).Single()) == "scoped discovery and extraction");
            TestRunner.Check(!File.Exists(first) && !File.Exists(second));
            TestRunner.Check(candidate.Members.All(member => File.Exists(Path.Combine(root, "1", member.RestoredName))));
            await renamer.RestoreAsync(result.Journals[0]);
            TestRunner.Check(File.ReadAllBytes(first).SequenceEqual(bytes[..cut]) && File.ReadAllBytes(second).SequenceEqual(bytes[cut..]));
        });
        Add("multi-volume RAR SFX accounts for validated first-volume prefix", async root =>
        {
            var members = new List<ArchiveMember>();
            for (int i = 1; i <= 8; i++)
            {
                var bytes = File.ReadAllBytes(Path.Combine(RepositoryRoot(), "tests", "fixtures", "libarchive", $"test_read_format_rar5_multiarchive.part{i:D2}.rar"));
                var source = Path.Combine(root, i == 1 ? "first.exe" : $"part{i}.tmp");
                if (i == 1)
                    bytes = [.. File.ReadAllBytes(EnginePath()), .. bytes];
                File.WriteAllBytes(source, bytes);
                members.Add(new(source, $"group.part{i:D3}.rar", i - 1));
            }
            var candidate = new ArchiveCandidate { DisplayName = "generated RAR SFX", Format = ArchiveFormat.Rar, VolumeKind = VolumeKind.RarParts, IsSelfExtracting = true, Status = CandidateStatus.Ready, Members = members };
            var coordinator = new ExtractionCoordinator(new EmptyScanner(), new RenameService(), new SevenZipEngine(EnginePath()));
            var result = await coordinator.RunAsync(candidate, new(Path.Combine(root, "output")), (_, _) => throw new Exception("Fixture is not encrypted"));
            TestRunner.Check(result.RemainingCandidates.Count == 0);
            TestRunner.Check(Directory.GetFiles(result.OutputDirectory, "*", SearchOption.AllDirectories).Length == 2);
            TestRunner.Check(result.Journals.Count == 1);
        });
        Add("cross-directory smart stop protects any member inside application subtree", async root =>
        {
            var inner = ZipBytes(("final.txt", "must remain packed"u8.ToArray()));
            int cut = inner.Length / 2;
            var first = inner[..cut];
            var second = inner[cut..];
            var outer = Path.Combine(root, "outer.tmp");
            File.WriteAllBytes(outer, ZipBytes(("volumes/first.tmp", first), ("program/second.tmp", second), ("program/app.exe", File.ReadAllBytes(EnginePath()))));
            var candidate = new ArchiveCandidate { DisplayName = "outer", Format = ArchiveFormat.Zip, Status = CandidateStatus.Ready, Members = [new(outer, "outer.zip", 0)] };
            var coordinator = new ExtractionCoordinator(new CrossGroupScanner(), new RenameService(), new SevenZipEngine(EnginePath()));
            var result = await coordinator.RunAsync(candidate, new(Path.Combine(root, "output")), (_, _) => throw new Exception("Fixture is not encrypted"));
            TestRunner.Check(result.RemainingCandidates.Count == 1);
            TestRunner.Check(result.Messages.Any(m => m.Contains("智能停止")) && !result.Messages.Any(m => m.Contains("未完成")), string.Join(";", result.Messages));
            TestRunner.Check(!Directory.GetFiles(result.OutputDirectory, "final.txt", SearchOption.AllDirectories).Any());
            var remaining = result.RemainingCandidates[0];
            TestRunner.Check(File.ReadAllBytes(remaining.Members[0].SourcePath).SequenceEqual(first));
            TestRunner.Check(File.ReadAllBytes(remaining.Members[1].SourcePath).SequenceEqual(second));
        });
    }
    sealed class EmptyScanner : IArchiveScanner
    {
        public Task<ScanResult> ScanAsync(IEnumerable<string> inputs, CancellationToken cancellationToken = default) => Task.FromResult(new ScanResult([], []));
    }
    sealed class CrossGroupScanner : IArchiveScanner
    {
        bool first = true;
        public Task<ScanResult> ScanAsync(IEnumerable<string> inputs, CancellationToken cancellationToken = default)
        {
            if (!first)
                return Task.FromResult(new ScanResult([], []));
            first = false;
            var root = inputs.Single();
            var candidate = new ArchiveCandidate { DisplayName = "protected split", Format = ArchiveFormat.Zip, VolumeKind = VolumeKind.ByteSplit, Status = CandidateStatus.Ready, Members = [new(Path.Combine(root, "volumes", "first.tmp"), "group.zip.001", 0), new(Path.Combine(root, "program", "second.tmp"), "group.zip.002", 1)] };
            return Task.FromResult(new ScanResult([candidate], []));
        }
    }
    static byte[] ZipBytes(params (string Name, byte[] Bytes)[] entries)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var entry in entries)
            {
                using var stream = zip.CreateEntry(entry.Name).Open();
                stream.Write(entry.Bytes);
            }
        return output.ToArray();
    }
    static string EnginePath() => Path.Combine(RepositoryRoot(), "vendor", "7zip", "7z.exe");
    static string RepositoryRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "vendor", "7zip", "7z.exe")))
                return d.FullName;
        throw new IOException("Bundled engine not found");
    }
    static void Add(string name, Func<string, Task> run) => TestRunner.Cases.Add((name, async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "AutoExtractor-cross-engine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await run(root);
        }
        finally { Directory.Delete(root, true); }
    }
    ));
}
