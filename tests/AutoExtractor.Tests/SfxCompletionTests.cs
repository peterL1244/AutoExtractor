using System.Diagnostics;
using System.IO.Compression;
using AutoExtractor.Core;
using AutoExtractor.Engine;
namespace AutoExtractor.Tests;

public static class SfxCompletionTests
{
    const string Password = "synthetic-sfx-password";
    public static void Register()
    {
        Add("numbered encrypted SFX is a standalone archive", async root =>
        {
            var sfx = await MakeSfx(root);
            var input = Input(root);
            var path = Path.Combine(input, "A3397.exe");
            File.WriteAllBytes(path, sfx);
            var signature = SignatureDetector.Detect(path);
            TestRunner.Check(signature.IsSelfExtracting && !signature.IsVolume);
            var c = (await new ArchiveScanner().ScanAsync([input])).Candidates.Single();
            TestRunner.Check(c.VolumeKind == VolumeKind.Single && c.Status == CandidateStatus.Ready && c.DisplayName == "A3397" && c.Members.Single().RestoredName == "A3397.7z", "Numeric product IDs must not imply missing volumes");
        });
        Add("independent numbered SFX files stay independent", async root =>
        {
            var sfx = await MakeSfx(root);
            var input = Input(root);
            File.WriteAllBytes(Path.Combine(input, "A3397.exe"), sfx);
            File.WriteAllBytes(Path.Combine(input, "A3398.exe"), sfx);
            var cs = (await new ArchiveScanner().ScanAsync([input])).Candidates;
            TestRunner.Check(cs.Count == 2 && cs.All(c => c.VolumeKind == VolumeKind.Single && c.Status == CandidateStatus.Ready));
        });
        Add("SFX explicit and loose genuine byte split companions remain grouped", async root =>
        {
            var sfx = await MakeSfx(root);
            foreach (var explicitNumber in new[] { true, false })
            {
                var input = Path.Combine(root, explicitNumber ? "explicit" : "loose");
                Directory.CreateDirectory(input);
                File.WriteAllBytes(Path.Combine(input, explicitNumber ? "setup.001.exe" : "setup01.exe"), sfx[..^32]);
                File.WriteAllBytes(Path.Combine(input, explicitNumber ? "setup.002.tmp" : "setup02.tmp"), sfx[^32..]);
                var c = (await new ArchiveScanner().ScanAsync([input])).Candidates.Single();
                TestRunner.Check(c.VolumeKind == VolumeKind.ByteSplit && c.Members.Count == 2 && c.Status == CandidateStatus.Ready);
            }
        });
        Add("single explicit SFX volume still requires confirmation", async root =>
        {
            var sfx = await MakeSfx(root);
            var input = Input(root);
            File.WriteAllBytes(Path.Combine(input, "setup.001.exe"), sfx);
            var c = (await new ArchiveScanner().ScanAsync([input])).Candidates.Single();
            TestRunner.Check(c.VolumeKind == VolumeKind.ByteSplit && c.Status == CandidateStatus.NeedsConfirmation);
        });
        Add("numbered Godot resources and Linux launcher are not split archives", async root =>
        {
            var input = Input(root);
            WriteGodot(input);
            TestRunner.Check(SignatureDetector.Detect(Path.Combine(input, "WhatIf2.x86_64")).IsExecutable);
            TestRunner.Check((await new ArchiveScanner().ScanAsync([input])).Candidates.Count == 0, "Final software files must not create a false WhatIf volume group");
        });
        Add("encrypted numbered SFX recursively reaches RenPy style software and keeps resource archive", async root =>
        {
            var payload = Path.Combine(root, "payload");
            Directory.CreateDirectory(Path.Combine(payload, "wrapper", "software", "game"));
            Directory.CreateDirectory(Path.Combine(payload, "wrapper", "software", "lib"));
            Directory.CreateDirectory(Path.Combine(payload, "wrapper", "software", "renpy"));
            File.Copy(EnginePath(), Path.Combine(payload, "wrapper", "software", "launch.exe"));
            File.WriteAllText(Path.Combine(payload, "wrapper", "software", "lib", "library.txt"), "fixture");
            File.WriteAllText(Path.Combine(payload, "wrapper", "software", "renpy", "runtime.txt"), "fixture");
            File.WriteAllBytes(Path.Combine(payload, "wrapper", "software", "game", "resource.tmp"), Zip("keep.txt", "keep packed"u8.ToArray()));
            var result = await ExtractOuter(root, await MakeSfx(root, payload));
            TestRunner.Check(Directory.GetFiles(result.OutputDirectory, "launch.exe", SearchOption.AllDirectories).Length == 1);
            TestRunner.Check(result.RemainingCandidates.Count == 1 && !Directory.GetFiles(result.OutputDirectory, "keep.txt", SearchOption.AllDirectories).Any(), "Software resource should remain packed after smart stop");
        });
        Add("encrypted numbered SFX reaches final Godot payload without false remaining volumes", async root =>
        {
            var payload = Path.Combine(root, "payload");
            var software = Path.Combine(payload, "pack", "1", "1", "software");
            Directory.CreateDirectory(software);
            WriteGodot(software);
            var result = await ExtractOuter(root, await MakeSfx(root, payload));
            TestRunner.Check(result.RemainingCandidates.Count == 0, "All package layers should finish without treating runtime resources as volumes");
            foreach (var name in new[] { "WhatIf2.exe", "WhatIf2.pck", "WhatIf2.x86_64" })
                TestRunner.Check(Directory.GetFiles(result.OutputDirectory, name, SearchOption.AllDirectories).Length == 1);
        });
    }
    static async Task<ExtractionResult> ExtractOuter(string root, byte[] sfx)
    {
        var input = Input(root);
        var outer = Path.Combine(input, "outer.zip");
        File.WriteAllBytes(outer, Zip("A3397.exe", sfx));
        var scanner = new ArchiveScanner();
        var candidate = (await scanner.ScanAsync([input])).Candidates.Single();
        var layers = new List<int>();
        var result = await new ExtractionCoordinator(scanner, new RenameService(), new SevenZipEngine(EnginePath())).RunAsync(candidate, new(Path.Combine(root, "output")), (request, _) => { layers.Add(request.Layer); return Task.FromResult<string?>(Password); });
        TestRunner.Check(layers.SequenceEqual(new[] { 2 }), "Encrypted SFX should prompt exactly at the second package layer");
        return result;
    }
    static void WriteGodot(string directory)
    {
        File.Copy(EnginePath(), Path.Combine(directory, "WhatIf2.exe"));
        File.WriteAllBytes(Path.Combine(directory, "WhatIf2.pck"), "GDPC synthetic resource"u8.ToArray());
        var elf = new byte[64];
        new byte[] { 127, 69, 76, 70, 2, 1, 1 }.CopyTo(elf, 0);
        elf[16] = 2;
        elf[18] = 62;
        elf[20] = 1;
        elf[52] = 64;
        File.WriteAllBytes(Path.Combine(directory, "WhatIf2.x86_64"), elf);
    }
    static string Input(string root)
    {
        var path = Path.Combine(root, "input");
        Directory.CreateDirectory(path);
        return path;
    }
    static async Task<byte[]> MakeSfx(string root, string? source = null)
    {
        if (source is null)
        {
            source = Path.Combine(root, "payload");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "content.txt"), "fixture");
        }
        var archive = Path.Combine(root, "payload.7z");
        var info = new ProcessStartInfo(EnginePath()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = source };
        foreach (var arg in new[] { "a", "-t7z", "-mhe=on", "-p" + Password, "-y", archive, "." })
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await stdout;
        var error = await stderr;
        TestRunner.Check(process.ExitCode == 0, "Fixture creation failed: " + error);
        return File.ReadAllBytes(EnginePath()).Concat(File.ReadAllBytes(archive)).ToArray();
    }
    static byte[] Zip(string name, byte[] data)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            using var output = zip.CreateEntry(name).Open();
            output.Write(data);
        }
        return bytes.ToArray();
    }
    static string EnginePath()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var path = Path.Combine(d.FullName, "vendor", "7zip", "7z.exe");
            if (File.Exists(path))
                return path;
        }
        throw new IOException("Engine missing");
    }
    static void Add(string name, Func<string, Task> test) => TestRunner.Cases.Add((name, async () => { var root = Path.Combine(Path.GetTempPath(), "AutoExtractor-sfx-completion-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); try { await test(root); } finally { Directory.Delete(root, true); } }));
}
