using System.IO.Compression;
using AutoExtractor.Core;
using AutoExtractor.Engine;

namespace AutoExtractor.Tests;

public static class FinalContentTests
{
    public static void Register()
    {
        Add("final navigation preserves ordinary documents beside a software folder", async root =>
        {
            var result = await Extract(root, Zip(("software/app.exe", File.ReadAllBytes(EnginePath())), ("software/data.bin", "resource"u8.ToArray()), ("documents/manual.txt", "instructions"u8.ToArray())));
            var browse = Browse(result) + Path.DirectorySeparatorChar;
            TestRunner.Check(Directory.GetFiles(result.OutputDirectory, "manual.txt", SearchOption.AllDirectories).Single().StartsWith(browse, StringComparison.OrdinalIgnoreCase), "A sibling document must remain in the opened result");
            TestRunner.Check(Directory.GetFiles(result.OutputDirectory, "app.exe", SearchOption.AllDirectories).Single().StartsWith(browse, StringComparison.OrdinalIgnoreCase));
        });
        Add("final navigation preserves ordinary payload from earlier archive layers", async root =>
        {
            var payload = Zip(("software/app.exe", File.ReadAllBytes(EnginePath())), ("software/data.bin", "resource"u8.ToArray()));
            var result = await Extract(root, Zip(("inner.tmp", payload), ("readme.txt", "keep these instructions"u8.ToArray())));
            TestRunner.Check(Browse(result) == result.OutputDirectory, "Earlier layer instructions must not disappear from the result view");
        });
        Add("result opens final content past archive layers and single wrapping folders", async root =>
        {
            var payload = Zip(("download/1/1/software/start.exe", File.ReadAllBytes(EnginePath())), ("download/1/1/software/game/data.rpa", "fixture resource"u8.ToArray()));
            var result = await Extract(root, Zip(("inner.tmp", payload)));
            var executable = Directory.GetFiles(result.OutputDirectory, "start.exe", SearchOption.AllDirectories).Single();
            TestRunner.Check(Browse(result) == Path.GetDirectoryName(executable), "Open result should lead to the actual software folder");
            TestRunner.Check(Directory.GetFiles(result.OutputDirectory, "inner.zip", SearchOption.AllDirectories).Length == 1, "Intermediate archive must remain available");
        });
        Add("result retains all outputs when multiple software folders exist", async root =>
        {
            var exe = File.ReadAllBytes(EnginePath());
            var result = await Extract(root, Zip(("one/app.exe", exe), ("one/readme.txt", "one"u8.ToArray()), ("two/app.exe", exe), ("two/readme.txt", "two"u8.ToArray())));
            TestRunner.Check(Browse(result) == result.OutputDirectory, "Multiple software results must not pick one arbitrarily");
        });
        Add("result opens software folder while resource archives remain intentionally packed", async root =>
        {
            var resource = Zip(("nested.txt", "keep packed"u8.ToArray()));
            var result = await Extract(root, Zip(("wrapper/software/app.exe", File.ReadAllBytes(EnginePath())), ("wrapper/software/resources/data.tmp", resource)));
            TestRunner.Check(result.RemainingCandidates.Count == 1);
            var app = Directory.GetFiles(result.OutputDirectory, "app.exe", SearchOption.AllDirectories).Single();
            TestRunner.Check(Browse(result) == Path.GetDirectoryName(app));
        });
        Add("failed nested archive keeps the full output location visible", async root =>
        {
            var broken = new byte[32];
            new byte[] { 55, 122, 188, 175, 39, 28 }.CopyTo(broken, 0);
            var result = await Extract(root, Zip(("software/app.exe", File.ReadAllBytes(EnginePath())), ("software/readme.txt", "ready"u8.ToArray()), ("broken.tmp", broken)));
            TestRunner.Check(result.RemainingCandidates.Count == 1 && result.Messages.Any(m => m.Contains("未完成")));
            TestRunner.Check(Browse(result) == result.OutputDirectory, "A failed sibling must remain visible with the successful output");
        });
    }
    static string Browse(ExtractionResult result)
    {
        var property = typeof(ExtractionResult).GetProperty("BrowseDirectory");
        TestRunner.Check(property is not null, "Extraction result must expose a destination for browsing final content");
        return (string)property!.GetValue(result)!;
    }
    static async Task<ExtractionResult> Extract(string root, byte[] zip)
    {
        var source = Path.Combine(root, "outer.zip");
        File.WriteAllBytes(source, zip);
        var scanner = new ArchiveScanner();
        var candidate = (await scanner.ScanAsync([source])).Candidates.Single();
        return await new ExtractionCoordinator(scanner, new RenameService(), new SevenZipEngine(EnginePath())).RunAsync(candidate, new(Path.Combine(root, "output")), (_, _) => throw new Exception("No password needed"));
    }
    static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        foreach (var entry in entries)
        {
            using var output = zip.CreateEntry(entry.Name).Open();
            output.Write(entry.Data);
        }
        return bytes.ToArray();
    }
    static string EnginePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "vendor", "7zip", "7z.exe");
            if (File.Exists(path))
                return path;
        }
        throw new IOException("Bundled engine missing");
    }
    static void Add(string name, Func<string, Task> test) => TestRunner.Cases.Add((name, async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "AutoExtractor-final-content-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await test(root);
        }
        finally { Directory.Delete(root, true); }
    }
    ));
}
