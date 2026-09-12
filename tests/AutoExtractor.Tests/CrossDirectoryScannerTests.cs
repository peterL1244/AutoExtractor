using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using AutoExtractor.Core;

namespace AutoExtractor.Tests;

public static class CrossDirectoryScannerTests
{
    public static void Register()
    {
        Add("native ZIP terminal local header preserves EOCD disk metadata", async root =>
        {
            var terminal = Zip();
            BinaryPrimitives.WriteUInt16LittleEndian(terminal.AsSpan(terminal.Length - 18), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(terminal.AsSpan(terminal.Length - 16), 1);
            Put(Path.Combine(root, "1"), "game.z01", [80, 75, 7, 8, .. Zip()[..30]]);
            string last = Put(Path.Combine(root, "2"), "game.zip", terminal);
            TestRunner.Check(SignatureDetector.Detect(last).IsVolume, "A terminal volume can start with a local file header.");
            var c = (await new ArchiveScanner().ScanAsync([root])).Candidates.Single();
            TestRunner.Check(c.Status == CandidateStatus.Ready && c.VolumeKind == VolumeKind.ZipSplit && c.EntryPath == last && c.Members.Count == 2);
        });
        Add("ZIP volume EOCD is read beyond bounded header window", async root =>
        {
            string last = Put(root, "terminal.zip", Zip()[..30]);
            byte[] end = new byte[22];
            BinaryPrimitives.WriteUInt32LittleEndian(end, 0x06054b50);
            BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(4), 1);
            using (var file = new FileStream(last, FileMode.Open, FileAccess.Write))
            {
                file.SetLength(8 * 1024 * 1024);
                file.Position = file.Length - end.Length;
                file.Write(end);
            }
            TestRunner.Check(SignatureDetector.Detect(last).IsVolume);
            await Task.CompletedTask;
        });
        Add("standalone ZIP cannot become another directory's split terminal", async root =>
        {
            string archive = Put(Path.Combine(root, "1"), "game.zip", Zip());
            Put(Path.Combine(root, "2"), "game.z01", [80, 75, 7, 8, .. Zip()[..30]]);
            var candidates = (await new ArchiveScanner().ScanAsync([root])).Candidates;
            TestRunner.Check(candidates.Count == 2);
            TestRunner.Check(candidates.Single(c => c.EntryPath == archive).VolumeKind == VolumeKind.Single && candidates.Single(c => c.EntryPath == archive).Status == CandidateStatus.Ready);
            TestRunner.Check(candidates.All(c => c.Members.Count == 1));
        });
        Add("standalone RAR cannot become another directory's legacy head", async root =>
        {
            string archive = Put(Path.Combine(root, "1"), "game.rar", File.ReadAllBytes(Path.Combine(FixtureDirectory(), "test_read_format_rar5_compressed.rar")));
            Put(Path.Combine(root, "2"), "game.r00", Fixture(2));
            var candidates = (await new ArchiveScanner().ScanAsync([root])).Candidates;
            TestRunner.Check(candidates.Count == 2);
            TestRunner.Check(candidates.Single(c => c.EntryPath == archive).VolumeKind == VolumeKind.Single && candidates.Single(c => c.EntryPath == archive).Status == CandidateStatus.Ready);
            TestRunner.Check(candidates.All(c => c.Members.Count == 1));
        });
        Add("same-directory RAR SFX part1 exe shares native RAR family", async root =>
        {
            var paths = MakeRarSet(root, _ => root);
            var signatures = paths.Select(SignatureDetector.Detect).ToArray();
            TestRunner.Check(signatures[0].IsSelfExtracting && signatures.All(s => s.Format == ArchiveFormat.Rar && s.IsVolume));
            var result = await new ArchiveScanner().ScanAsync([root]);
            CheckRarGroup(result, paths);
        });
        Add("imported parent merges complementary nested native RAR volumes", async root =>
        {
            var paths = MakeRarSet(root, i => Path.Combine(root, i % 2 == 0 ? "2" : "1"));
            var hashes = paths.Select(Hash).ToArray();
            CheckRarGroup(await new ArchiveScanner().ScanAsync([root]), paths);
            for (int i = 0; i < paths.Length; i++)
                TestRunner.Check(File.Exists(paths[i]) && Hash(paths[i]) == hashes[i]);
        });
        Add("multiple selected directories merge only imported RAR family", async root =>
        {
            var paths = MakeRarSet(root, i => Path.Combine(root, i % 2 == 0 ? "2" : "1"));
            var outside = Path.Combine(root, "not-selected");
            Directory.CreateDirectory(outside);
            File.Copy(paths[1], Path.Combine(outside, Path.GetFileName(paths[1])));
            CheckRarGroup(await new ArchiveScanner().ScanAsync([Path.Combine(root, "1"), Path.Combine(root, "2")]), paths);
        });
        Add("multiple selected files merge scoped RAR parts without upward discovery", async root =>
        {
            var paths = MakeRarSet(root, i => Path.Combine(root, i.ToString()));
            CheckRarGroup(await new ArchiveScanner().ScanAsync(paths), paths);
            var alone = await new ArchiveScanner().ScanAsync([paths[0]]);
            TestRunner.Check(alone.Candidates.Count == 1 && alone.Candidates[0].Members.Count == 1 && alone.Candidates[0].Status != CandidateStatus.Ready);
        });
        Add("nested byte split ZIP joins across imported child directories", async root =>
        {
            byte[] zip = Zip();
            int split = 35;
            string first = Put(Path.Combine(root, "a"), "game.001.jpg", zip[..split]);
            string second = Put(Path.Combine(root, "b"), "game.002.mp4", zip[split..]);
            var c = (await new ArchiveScanner().ScanAsync([root])).Candidates.Single();
            TestRunner.Check(c.Status == CandidateStatus.Ready && c.VolumeKind == VolumeKind.ByteSplit && c.Members.Select(m => m.SourcePath).SequenceEqual(new[] { first, second }));
            TestRunner.Check(c.Members.Select(m => m.RestoredName).SequenceEqual(new[] { "game.zip.001", "game.zip.002" }));
        });
        Add("duplicate complete RAR sets remain separate directory-local candidates", async root =>
        {
            MakeRarSet(root, _ => Path.Combine(root, "copy1"));
            MakeRarSet(root, _ => Path.Combine(root, "copy2"));
            var candidates = (await new ArchiveScanner().ScanAsync([root])).Candidates;
            TestRunner.Check(candidates.Count == 2 && candidates.All(c => c.Members.Count == 8 && c.Members.Select(m => Path.GetDirectoryName(m.SourcePath)).Distinct().Count() == 1));
        });
        Add("duplicate partial RAR heads cannot borrow one shared continuation", async root =>
        {
            var first = Fixture(1);
            var second = Fixture(2);
            Put(Path.Combine(root, "1"), "game.part1.rar", first);
            Put(Path.Combine(root, "2"), "game.part1.rar", first);
            Put(Path.Combine(root, "3"), "game.part2.rar", second);
            var candidates = (await new ArchiveScanner().ScanAsync([root])).Candidates;
            TestRunner.Check(candidates.Count == 3 && candidates.All(c => c.Status != CandidateStatus.Ready && c.Members.Count == 1));
        });
        Add("native ZIP first and terminal volumes join across selected directories", async root =>
        {
            byte[] eocd = new byte[22];
            BinaryPrimitives.WriteUInt32LittleEndian(eocd, 0x06054b50);
            BinaryPrimitives.WriteUInt16LittleEndian(eocd.AsSpan(4), 1);
            string first = Put(Path.Combine(root, "1"), "game.z01", [80, 75, 7, 8, .. Zip()[..30]]);
            string last = Put(Path.Combine(root, "2"), "game.zip", eocd);
            TestRunner.Check(SignatureDetector.Detect(last).IsVolume, "The EOCD disk number identifies a native ZIP volume even at offset zero.");
            var c = (await new ArchiveScanner().ScanAsync([root])).Candidates.Single();
            TestRunner.Check(c.Status == CandidateStatus.Ready && c.VolumeKind == VolumeKind.ZipSplit && c.EntryPath == last && c.Members[0].SourcePath == first);
        });
        Add("native legacy RAR anchor and continuation join across directories", async root =>
        {
            string first = Put(Path.Combine(root, "1"), "game.rar", Fixture(1));
            string second = Put(Path.Combine(root, "2"), "game.r00", Fixture(2));
            var c = (await new ArchiveScanner().ScanAsync([root])).Candidates.Single();
            TestRunner.Check(c.Status == CandidateStatus.Ready && c.VolumeKind == VolumeKind.RarLegacy && c.Members.Select(m => m.SourcePath).SequenceEqual(new[] { first, second }));
        });
        Add("independent native RARs do not become cross-directory numbered volumes", async root =>
        {
            var single = Path.Combine(FixtureDirectory(), "test_read_format_rar5_compressed.rar");
            Put(Path.Combine(root, "1"), "game.part1.exe", File.ReadAllBytes(single));
            Put(Path.Combine(root, "2"), "game.part2.rar", File.ReadAllBytes(single));
            TestRunner.Check((await new ArchiveScanner().ScanAsync([root])).Candidates.All(c => c.Status != CandidateStatus.Ready));
        });
    }

    static void CheckRarGroup(ScanResult result, string[] paths)
    {
        TestRunner.Check(result.Candidates.Count == 1, $"Expected one RAR family, got {result.Candidates.Count}");
        var c = result.Candidates[0];
        TestRunner.Check(c.Format == ArchiveFormat.Rar && c.VolumeKind == VolumeKind.RarParts && c.Status == CandidateStatus.Ready, c.Explanation);
        TestRunner.Check(c.Members.Select(m => m.SourcePath).SequenceEqual(paths));
        TestRunner.Check(c.IsSelfExtracting && c.EntryPath == paths[0]);
        TestRunner.Check(c.Members.Select(m => m.RestoredName).SequenceEqual(Enumerable.Range(1, 8).Select(i => $"example.sfx.sfx.part{i:000}.rar")));
    }
    static string[] MakeRarSet(string root, Func<int, string> directory)
    {
        _ = root;
        return Enumerable.Range(1, 8).Select(i => Put(directory(i), $"example.sfx.sfx.part{i}.{(i == 1 ? "exe" : "rar")}", i == 1 ? Sfx(Fixture(i)) : Fixture(i))).ToArray();
    }
    static byte[] Sfx(byte[] rar)
    {
        byte[] stub = new byte[512];
        stub[0] = 77;
        stub[1] = 90;
        stub[60] = 64;
        stub[64] = 80;
        stub[65] = 69;
        return [.. stub, .. rar];
    }
    static byte[] Fixture(int i) => File.ReadAllBytes(Path.Combine(FixtureDirectory(), $"test_read_format_rar5_multiarchive.part{i:00}.rar"));
    static string FixtureDirectory()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string path = Path.Combine(d.FullName, "tests", "fixtures", "libarchive");
            if (File.Exists(Path.Combine(path, "manifest.json")))
                return path;
        }
        throw new IOException("Fixture directory not found");
    }
    static string Put(string directory, string name, byte[] content)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, content);
        return path;
    }
    static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }
    static byte[] Zip()
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("content.txt").Open());
            writer.Write(new string('x', 100));
        }
        return bytes.ToArray();
    }
    static void Add(string name, Func<string, Task> test) => TestRunner.Cases.Add((name, async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "AutoExtractor-crossdir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await test(root);
        }
        finally { Directory.Delete(root, true); }
    }
    ));
}
