using System.Diagnostics;
using System.IO.Compression;
using AutoExtractor.Core;
using AutoExtractor.Engine;
namespace AutoExtractor.Tests;

public static partial class EngineTests
{
    static string Exe => Path.Combine(FindRoot(), "vendor", "7zip", "7z.exe");
    static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "vendor", "7zip", "7z.exe")))
            d = d.Parent;
        return d?.FullName ?? throw new Exception("Engine not found");
    }
    static SevenZipEngine Engine => new(Exe);
    sealed class Temp : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AutoExtractor-engine-tests-" + Guid.NewGuid().ToString("N")); public Temp() => Directory.CreateDirectory(Path); public string File(string p) => System.IO.Path.Combine(Path, p); public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (IOException) { }
        }
    }
    static string Zip(Temp t, string name, params (string Name, string Text)[] entries)
    {
        var p = t.File(name);
        using var z = ZipFile.Open(p, ZipArchiveMode.Create);
        foreach (var e in entries)
        {
            using var w = new StreamWriter(z.CreateEntry(e.Name).Open());
            w.Write(e.Text);
        }
        return p;
    }
    static async Task Make7z(string path, string source, string? password = null, bool split = false)
    {
        var si = new ProcessStartInfo(Exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "a", "-t7z", path, source, "-y" })
            si.ArgumentList.Add(a);
        if (password != null)
        {
            si.ArgumentList.Add("-p" + password);
            si.ArgumentList.Add("-mhe=on");
        }
        if (split)
            si.ArgumentList.Add("-v100b");
        using var p = Process.Start(si)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        await o;
        var err = await e;
        TestRunner.Check(p.ExitCode == 0, "fixture failed " + err);
    }
    static ArchiveCandidate Candidate(string p) => new() { DisplayName = Path.GetFileName(p), Format = ArchiveFormat.Zip, Members = [new(p, Path.GetFileNameWithoutExtension(p) + ".zip", 0)], Status = CandidateStatus.Ready };
    static async Task Failure(Func<Task> action, params EngineFailure[] reasons)
    {
        try
        {
            await action();
        }
        catch (ArchiveException e) { TestRunner.Check(reasons.Contains(e.Failure), "Unexpected failure " + e.Failure); return; }
        throw new Exception("Expected archive failure");
    }
    sealed class EmptyScanner : IArchiveScanner
    {
        public Task<ScanResult> ScanAsync(IEnumerable<string> inputs, CancellationToken cancellationToken = default) => Task.FromResult(new ScanResult([], []));
    }
    sealed class Renamer : IRenameService
    {
        public bool Called; public Task<RenameResult> ApplyAsync(ArchiveCandidate c, CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult(new RenameResult(c, null));
        }
        public Task RestoreAsync(string p, CancellationToken cancellationToken = default) => Task.CompletedTask; public IReadOnlyList<string> FindJournals(string d) => [];
    }
    public static void Register()
    {
        RegisterLayerTests();
        TestRunner.Cases.Add(("engine Unicode ZIP listing and extraction", async () => { using var t = new Temp(); var p = Zip(t, "伪装.jpg", ("目录/中文.txt", "内容")); var l = await Engine.ListAsync(p, null); TestRunner.Check(l.Format == ArchiveFormat.Zip && l.Entries.Single().Size == 6); var o = t.File("output"); await Engine.ExtractAsync(p, o, null); TestRunner.Check(System.IO.File.ReadAllText(Path.Combine(o, "目录", "中文.txt")) == "内容"); }
        ));
        TestRunner.Cases.Add(("engine rejects traversal and ADS before writing", async () => { foreach (var n in new[] { "../escape.txt", "C:/escape.txt", "file.txt:ads", "CON.txt", "dir/../escape.txt" }) { using var t = new Temp(); var p = Zip(t, "bad.zip", (n, "bad")); await Failure(() => Engine.ExtractAsync(p, t.File("out"), null), EngineFailure.UnsafePath); TestRunner.Check(!System.IO.File.Exists(t.File("escape.txt"))); } }
        ));
        TestRunner.Cases.Add(("engine refuses overwrite", async () => { using var t = new Temp(); var p = Zip(t, "a.zip", ("existing.txt", "new")); Directory.CreateDirectory(t.File("out")); System.IO.File.WriteAllText(t.File("out/existing.txt"), "old"); await Failure(() => Engine.ExtractAsync(p, t.File("out"), null), EngineFailure.UnsafePath); TestRunner.Check(System.IO.File.ReadAllText(t.File("out/existing.txt")) == "old"); }
        ));
        TestRunner.Cases.Add(("engine rejects ordinary executable", async () => { await Failure(() => Engine.ListAsync(Exe, null), EngineFailure.Unsupported); }
        ));
        TestRunner.Cases.Add(("engine encrypted headers retries without logging secret", async () => { using var t = new Temp(); System.IO.File.WriteAllText(t.File("secret.txt"), "secret"); await Make7z(t.File("header.7z"), t.File("secret.txt"), "correct密碼"); await Failure(() => Engine.ListAsync(t.File("header.7z"), null), EngineFailure.NeedsPassword, EngineFailure.PasswordOrCorruption); await Failure(() => Engine.ListAsync(t.File("header.7z"), "incorrect"), EngineFailure.NeedsPassword, EngineFailure.PasswordOrCorruption); var l = await Engine.ListAsync(t.File("header.7z"), "correct密碼"); TestRunner.Check(l.Entries.Count == 1); await Engine.ExtractAsync(t.File("header.7z"), t.File("out"), "correct密碼"); }
        ));
        TestRunner.Cases.Add(("engine corrupt archive classified", async () => { using var t = new Temp(); System.IO.File.WriteAllBytes(t.File("bad.7z"), [0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c, 0, 0]); await Failure(() => Engine.ListAsync(t.File("bad.7z"), null), EngineFailure.CorruptArchive); }
        ));
        TestRunner.Cases.Add(("engine cancellation", async () => { using var t = new Temp(); var p = Zip(t, "a.zip", ("a.txt", "hello")); using var c = new CancellationTokenSource(); c.Cancel(); await TestRunner.ThrowsAsync<OperationCanceledException>(() => Engine.ListAsync(p, null, c.Token)); }
        ));
        TestRunner.Cases.Add(("coordinator validates before rename", async () => { using var t = new Temp(); var p = t.File("bad.jpg"); System.IO.File.WriteAllText(p, "not an archive"); var r = new Renamer(); var c = new ExtractionCoordinator(new EmptyScanner(), r, Engine); await Failure(() => c.RunAsync(Candidate(p), new(t.File("out")), (_, _) => Task.FromResult<string?>(null)), EngineFailure.CorruptArchive, EngineFailure.Unsupported); TestRunner.Check(!r.Called); TestRunner.Check(System.IO.File.Exists(p)); }
        ));
        TestRunner.Cases.Add(("coordinator publishes successful isolated result", async () => { using var t = new Temp(); var p = Zip(t, "a.jpg", ("a.txt", "hello")); var c = new ExtractionCoordinator(new EmptyScanner(), new Renamer(), Engine); var r = await c.RunAsync(Candidate(p), new(t.File("out")), (_, _) => Task.FromResult<string?>(null)); TestRunner.Check(Directory.GetFiles(r.OutputDirectory, "a.txt", SearchOption.AllDirectories).Length == 1); TestRunner.Check(Directory.GetDirectories(r.OutputDirectory, "*incomplete*", SearchOption.AllDirectories).Length == 0); }
        ));
        TestRunner.Cases.Add(("coordinator password cancellation preserves input", async () => { using var t = new Temp(); System.IO.File.WriteAllText(t.File("a.txt"), "a"); await Make7z(t.File("enc.7z"), t.File("a.txt"), "pw"); var p = t.File("enc.7z"); var c = new ExtractionCoordinator(new EmptyScanner(), new Renamer(), Engine); await TestRunner.ThrowsAsync<OperationCanceledException>(() => c.RunAsync(Candidate(p) with { Format = ArchiveFormat.SevenZip }, new(t.File("out")), (_, _) => Task.FromResult<string?>(null))); TestRunner.Check(System.IO.File.Exists(p)); }
        ));
    }
}


public static partial class EngineTests
{
    static async Task<ArchiveCandidate> ScanOne(string p) => (await new ArchiveScanner().ScanAsync([p])).Candidates.Single();
    static async Task<string> OuterZip(Temp t, string name, params (string Name, string Source)[] files)
    {
        var p = t.File(name);
        using (var z = ZipFile.Open(p, ZipArchiveMode.Create))
        {
            foreach (var f in files)
                z.CreateEntryFromFile(f.Source, f.Name);
        }
        await Task.CompletedTask;
        return p;
    }
    static void RegisterLayerTests()
    {
        RegisterProgressTests();
        TestRunner.Cases.Add(("engine rejects newline and symbolic link metadata", async () => { using var t = new Temp(); var p = Zip(t, "newline.zip", ("abc\ndef.txt", "bad")); await Failure(() => Engine.ListAsync(p, null), EngineFailure.UnsafePath); var q = t.File("link.zip"); using (var z = ZipFile.Open(q, ZipArchiveMode.Create)) { var e = z.CreateEntry("link"); e.ExternalAttributes = unchecked((int)(0xA1FF0000)); using var w = new StreamWriter(e.Open()); w.Write("../target"); } await Failure(() => Engine.ListAsync(q, null), EngineFailure.UnsafePath); }
        ));
        TestRunner.Cases.Add(("coordinator three layers SFX volumes tmp with different passwords", async () =>
        {
            using var t = new Temp();
            Directory.CreateDirectory(t.File("source"));
            System.IO.File.WriteAllText(t.File("source/最终文本.txt"), "最终结果");
            await Make7z(t.File("payload.7z"), t.File("source/最终文本.txt"), "inner密碼");
            System.IO.File.Move(t.File("payload.7z"), t.File("payload.tmp"));
            await Make7z(t.File("middle.7z"), t.File("payload.tmp"), "middle密碼", true);
            var parts = Directory.GetFiles(t.Path, "middle.7z.*").Order().Select((p, i) => ("下载." + (i + 1).ToString("000") + ".jpg", p)).ToArray();
            var outer = await OuterZip(t, "outer.zip", parts);
            var sfx = t.File("下载.exe");
            var stub = System.IO.File.ReadAllBytes(Exe);
            await using (var output = System.IO.File.Create(sfx))
            {
                await output.WriteAsync(stub);
                await using var input = System.IO.File.OpenRead(outer);
                await input.CopyToAsync(output);
            }
            var c = await ScanOne(sfx);
            TestRunner.Check(c.IsSelfExtracting, "Expected SFX");
            var prompted = new List<int>();
            var coordinator = new ExtractionCoordinator(new ArchiveScanner(), new RenameService(), Engine);
            var r = await coordinator.RunAsync(c, new(t.File("out")), (p, _) => { prompted.Add(p.Layer); return Task.FromResult<string?>(p.Layer == 2 ? "middle密碼" : "inner密碼"); });
            TestRunner.Check(r.RemainingCandidates.Count == 0, string.Join(";", r.Messages));
            var final = Directory.GetFiles(r.OutputDirectory, "最终文本.txt", SearchOption.AllDirectories);
            TestRunner.Check(final.Length == 1 && System.IO.File.ReadAllText(final[0]) == "最终结果");
            TestRunner.Check(prompted.Contains(2) && prompted.Contains(3));
            TestRunner.Check(r.Journals.Count >= 3);
            TestRunner.Check(!Directory.GetDirectories(t.Path, ".AutoExtractor_validate_*", SearchOption.AllDirectories).Any());
        }
        ));
        TestRunner.Cases.Add(("coordinator smart stop preserves program resources", async () => { using var t = new Temp(); var nested = Zip(t, "nested.zip", ("final.txt", "hello")); var p = await OuterZip(t, "outer.zip", ("app.exe", Exe), ("resources/data.tmp", nested)); var c = new ExtractionCoordinator(new ArchiveScanner(), new RenameService(), Engine); var r = await c.RunAsync(await ScanOne(p), new(t.File("out")), (_, _) => Task.FromResult<string?>(null)); TestRunner.Check(r.RemainingCandidates.Count == 1); TestRunner.Check(!Directory.GetFiles(r.OutputDirectory, "final.txt", SearchOption.AllDirectories).Any()); }
        ));
        TestRunner.Cases.Add(("coordinator depth and duplicate content guard", async () => { using var t = new Temp(); var nested = Zip(t, "nested.zip", ("final.txt", "hello")); var p = await OuterZip(t, "outer.zip", ("one.tmp", nested), ("two.tmp", nested)); var c = new ExtractionCoordinator(new ArchiveScanner(), new RenameService(), Engine); var r = await c.RunAsync(await ScanOne(p), new(t.File("out")), (_, _) => Task.FromResult<string?>(null)); TestRunner.Check(r.RemainingCandidates.Count == 1, string.Join(";", r.Messages)); TestRunner.Check(Directory.GetFiles(r.OutputDirectory, "final.txt", SearchOption.AllDirectories).Length == 1); var r2 = await c.RunAsync(await ScanOne(p), new(t.File("depth"), 1), (_, _) => Task.FromResult<string?>(null)); TestRunner.Check(r2.RemainingCandidates.Count == 2); TestRunner.Check(!Directory.GetFiles(r2.OutputDirectory, "final.txt", SearchOption.AllDirectories).Any()); }
        ));
        TestRunner.Cases.Add(("coordinator corrupt child remains explicitly incomplete", async () => { using var t = new Temp(); var nested = Zip(t, "nested.zip", ("final.txt", "hello")); var bytes = System.IO.File.ReadAllBytes(nested); System.IO.File.WriteAllBytes(nested, bytes[..40]); var p = await OuterZip(t, "outer.zip", ("broken.tmp", nested)); var c = new ExtractionCoordinator(new ArchiveScanner(), new RenameService(), Engine); var r = await c.RunAsync(await ScanOne(p), new(t.File("out")), (_, _) => throw new Exception("Corruption must not prompt for passwords")); TestRunner.Check(r.RemainingCandidates.Count == 1, string.Join(";", r.Messages)); TestRunner.Check(r.Messages.Any(m => m.Contains("未完成"))); }
        ));
        TestRunner.Cases.Add(("coordinator broken split validates before any rename", async () => { using var t = new Temp(); System.IO.File.WriteAllBytes(t.File("text.txt"), System.Security.Cryptography.RandomNumberGenerator.GetBytes(1000)); await Make7z(t.File("a.7z"), t.File("text.txt"), null, true); var parts = Directory.GetFiles(t.Path, "a.7z.*").Order().ToArray(); for (int i = 0; i < parts.Length - 1; i++) System.IO.File.Move(parts[i], t.File($"split.{i + 1:000}.jpg")); System.IO.File.Delete(parts[^1]); var input = await ScanOne(t.File("split.001.jpg")); var renamer = new Renamer(); var c = new ExtractionCoordinator(new ArchiveScanner(), renamer, Engine); await Failure(() => c.RunAsync(input, new(t.File("out")), (_, _) => throw new Exception("Missing/corrupt is not password")), EngineFailure.CorruptArchive, EngineFailure.MissingVolumes); TestRunner.Check(!renamer.Called); TestRunner.Check(System.IO.File.Exists(t.File("split.001.jpg"))); }
        ));
    }
}




public static partial class EngineTests
{
    sealed class ImmediateProgress(Action<ExtractionProgress> report) : IProgress<ExtractionProgress>
    {
        public void Report(ExtractionProgress p) => report(p);
    }
    static void RegisterProgressTests()
    {
        RegisterZipVolumes();
        TestRunner.Cases.Add(("engine streams percent and current filename", async () => { using var t = new Temp(); var p = Zip(t, "progress.zip", ("streaming-file.txt", new string('x', 10_000_000))); var values = new List<ExtractionProgress>(); await Engine.ExtractAsync(p, t.File("out"), null, new ImmediateProgress(v => values.Add(v))); TestRunner.Check(values.Any(v => v.Percent is >= 0 and < 100), "No live percentage"); TestRunner.Check(values.Any(v => v.Message.Contains("streaming-file.txt")), "No current file"); }
        ));
        TestRunner.Cases.Add(("engine cancels a running extraction child", async () => { using var t = new Temp(); var p = Zip(t, "cancel.zip", ("large.txt", new string('x', 50_000_000))); using var cancel = new CancellationTokenSource(); bool streaming = false; await TestRunner.ThrowsAsync<OperationCanceledException>(() => Engine.ExtractAsync(p, t.File("out"), null, new ImmediateProgress(v => { if (v.Percent is >= 0 and < 100) { streaming = true; cancel.Cancel(); } }), cancel.Token)); TestRunner.Check(streaming, "Cancellation did not come from live child progress"); }
        ));
    }
}


public static partial class EngineTests
{
    static void RegisterZipVolumes()
    {
        RegisterReviewRegressions();
        TestRunner.Cases.Add(("engine byte split ZIP reads original metadata across members", async () => { using var t = new Temp(); var p = Zip(t, "zip.zip", ("中文.txt", new string('a', 5000))); var bytes = System.IO.File.ReadAllBytes(p); int part = 1; for (int pos = 0; pos < bytes.Length; pos += 80) System.IO.File.WriteAllBytes(t.File($"parts.zip.{part++:000}"), bytes[pos..Math.Min(pos + 80, bytes.Length)]); var l = await Engine.ListAsync(t.File("parts.zip.001"), null); TestRunner.Check(l.Format == ArchiveFormat.Zip && l.Entries.Count == 1); await Engine.ExtractAsync(t.File("parts.zip.001"), t.File("out"), null); TestRunner.Check(System.IO.File.ReadAllText(t.File("out/中文.txt")).Length == 5000); }
        ));
        TestRunner.Cases.Add(("coordinator native z01 ZIP aliases validate then rename", async () => { using var t = new Temp(); var p = Zip(t, "fixture.zip", ("final.txt", "native split")); var bytes = System.IO.File.ReadAllBytes(p); int end = bytes.Length - 22; int cd = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 16)); var first = new byte[cd + 4]; new byte[] { 0x50, 0x4b, 0x07, 0x08 }.CopyTo(first, 0); bytes.AsSpan(0, cd).CopyTo(first.AsSpan(4)); var last = bytes[cd..]; int e = last.Length - 22; System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(last.AsSpan(42), 4); System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(last.AsSpan(e + 4), 1); System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(last.AsSpan(e + 6), 1); System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(last.AsSpan(e + 16), 0); System.IO.File.WriteAllBytes(t.File("native.z01.jpg"), first); System.IO.File.WriteAllBytes(t.File("native.zip"), last); var candidate = (await new ArchiveScanner().ScanAsync([t.File("native.zip")])).Candidates.Single(c => c.DisplayName == "native"); TestRunner.Check(candidate.Status == CandidateStatus.Ready && candidate.VolumeKind == VolumeKind.ZipSplit); var coordinator = new ExtractionCoordinator(new ArchiveScanner(), new RenameService(), Engine); var r = await coordinator.RunAsync(candidate, new(t.File("out")), (_, _) => Task.FromResult<string?>(null)); TestRunner.Check(Directory.GetFiles(r.OutputDirectory, "final.txt", SearchOption.AllDirectories).Length == 1); TestRunner.Check(System.IO.File.Exists(t.File("native.z01"))); }
        ));
    }
}


public static partial class EngineTests
{
    static void RegisterReviewRegressions()
    {
        RegisterConsumedMemberEdgeTests();
        TestRunner.Cases.Add(("coordinator rejects unused independently readable group members before rename", async () =>
        {
            foreach (var kind in new[] { VolumeKind.RarParts, VolumeKind.Single, VolumeKind.ByteSplit })
            {
                using var t = new Temp();
                var p = Zip(t, "download.jpg", ("final.txt", "a"));
                System.IO.File.WriteAllText(t.File("extra.jpg"), "unrelated original");
                var candidate = new ArchiveCandidate { DisplayName = "manual", Format = kind == VolumeKind.RarParts ? ArchiveFormat.Rar : ArchiveFormat.Zip, VolumeKind = kind, Status = CandidateStatus.Ready, Members = [new(p, kind == VolumeKind.RarParts ? "game.part001.rar" : kind == VolumeKind.ByteSplit ? "game.zip.001" : "game.zip", 0), new(t.File("extra.jpg"), kind == VolumeKind.RarParts ? "game.part002.rar" : kind == VolumeKind.ByteSplit ? "game.zip.002" : "unused.zip", 1)] };
                var renamer = new Renamer();
                var c = new ExtractionCoordinator(new EmptyScanner(), renamer, Engine);
                await Failure(() => c.RunAsync(candidate, new(t.File("out")), (_, _) => throw new Exception("No encryption")), EngineFailure.Unsupported, EngineFailure.CorruptArchive, EngineFailure.UnsafePath);
                TestRunner.Check(!renamer.Called, "Unconsumed originals must not be renamed");
                TestRunner.Check(System.IO.File.ReadAllText(t.File("extra.jpg")) == "unrelated original");
            }
        }
        ));
        TestRunner.Cases.Add(("coordinator rejects trailing unused byte split data before rename", async () => { using var t = new Temp(); System.IO.File.WriteAllText(t.File("final.txt"), "real archive"); await Make7z(t.File("first.jpg"), t.File("final.txt")); System.IO.File.WriteAllText(t.File("second.jpg"), "unrelated tail"); var candidate = new ArchiveCandidate { DisplayName = "manual", Format = ArchiveFormat.SevenZip, VolumeKind = VolumeKind.ByteSplit, Status = CandidateStatus.Ready, Members = [new(t.File("first.jpg"), "archive.7z.001", 0), new(t.File("second.jpg"), "archive.7z.002", 1)] }; var renamer = new Renamer(); var c = new ExtractionCoordinator(new EmptyScanner(), renamer, Engine); await Failure(() => c.RunAsync(candidate, new(t.File("out")), (_, _) => throw new Exception("No encryption")), EngineFailure.Unsupported, EngineFailure.CorruptArchive); TestRunner.Check(!renamer.Called); }
        ));
        TestRunner.Cases.Add(("engine diagnostics cannot be spoofed by corrupt archive names", async () => { foreach (var name in new[] { "Wrong password.zip", "Missing volume.zip", "disk is full.zip", "Access is denied.zip" }) { using var t = new Temp(); var p = t.File(name); System.IO.File.WriteAllBytes(p, [80, 75, 3, 4, 0, 0, 0]); await Failure(() => Engine.ListAsync(p, null), EngineFailure.CorruptArchive); int prompts = 0; var renamer = new Renamer(); var c = new ExtractionCoordinator(new EmptyScanner(), renamer, Engine); await Failure(() => c.RunAsync(Candidate(p), new(t.File("out")), (_, _) => { prompts++; return Task.FromResult<string?>("anything"); }), EngineFailure.CorruptArchive); TestRunner.Check(prompts == 0 && !renamer.Called); } }
        ));
        TestRunner.Cases.Add(("coordinator missing child retains result and processes later sibling", async () => { using var t = new Temp(); var nested = Zip(t, "nested.zip", ("final.txt", "sibling succeeds")); var p = await OuterZip(t, "outer.jpg", ("sibling.tmp", nested)); var scanner = new DisappearingChildScanner(); var c = new ExtractionCoordinator(scanner, new RenameService(), Engine); var r = await c.RunAsync(Candidate(p), new(t.File("out")), (_, _) => throw new Exception("No encryption")); TestRunner.Check(r.RemainingCandidates.Count == 1 && r.RemainingCandidates[0].DisplayName == "missing-child"); TestRunner.Check(r.Journals.Count == 2, "Prior and sibling journals retained"); TestRunner.Check(Directory.GetFiles(r.OutputDirectory, "final.txt", SearchOption.AllDirectories).Length == 1); TestRunner.Check(r.Messages.Any(m => m.Contains("missing-child") && m.Contains("未完成"))); }
        ));
    }
    sealed class DisappearingChildScanner : IArchiveScanner
    {
        bool first = true;
        public async Task<ScanResult> ScanAsync(IEnumerable<string> inputs, CancellationToken cancellationToken = default)
        {
            var roots = inputs.ToArray();
            var scan = await new ArchiveScanner().ScanAsync(roots, cancellationToken);
            if (!first)
                return scan;
            first = false;
            var missing = Candidate(System.IO.Path.Combine(roots[0], "missing.tmp")) with
            {
                DisplayName = "missing-child"
            };
            return new(new[] { missing }.Concat(scan.Candidates).ToArray(), scan.Messages);
        }
    }
}


public static partial class EngineTests
{
    static void RegisterConsumedMemberEdgeTests()
    {
        TestRunner.Cases.Add(("coordinator accepts legitimate ZIP local header on later byte split boundary", async () => { using var t = new Temp(); var p = Zip(t, "fixture.zip", ("one.txt", "first"), ("two.txt", "second")); var bytes = System.IO.File.ReadAllBytes(p); int boundary = -1; for (int i = 4; i < bytes.Length - 4; i++) if (bytes.AsSpan(i, 4).SequenceEqual(new byte[] { 80, 75, 3, 4 })) { boundary = i; break; } TestRunner.Check(boundary > 0); System.IO.File.WriteAllBytes(t.File("first.jpg"), bytes[..boundary]); System.IO.File.WriteAllBytes(t.File("second.jpg"), bytes[boundary..]); var candidate = new ArchiveCandidate { DisplayName = "confirmed byte split", Format = ArchiveFormat.Zip, VolumeKind = VolumeKind.ByteSplit, Status = CandidateStatus.Ready, Members = [new(t.File("first.jpg"), "parts.zip.001", 0), new(t.File("second.jpg"), "parts.zip.002", 1)] }; var c = new ExtractionCoordinator(new EmptyScanner(), new RenameService(), Engine); var result = await c.RunAsync(candidate, new(t.File("out")), (_, _) => throw new Exception("Not encrypted")); TestRunner.Check(Directory.GetFiles(result.OutputDirectory, "*.txt", SearchOption.AllDirectories).Length == 2); TestRunner.Check(result.Journals.Count == 1); }
        ));
        TestRunner.Cases.Add(("coordinator rejects unused ninth member beside real eight-volume RAR", async () => { using var t = new Temp(); var fixtureDirectory = Path.Combine(FindRoot(), "tests", "fixtures", "libarchive"); var members = new List<ArchiveMember>(); for (int i = 1; i <= 8; i++) { var source = Path.Combine(fixtureDirectory, $"test_read_format_rar5_multiarchive.part{i:D2}.rar"); var copy = t.File($"download-{i}.jpg"); System.IO.File.Copy(source, copy); members.Add(new(copy, $"group.part{i:D3}.rar", i - 1)); } var extra = t.File("download-9.jpg"); System.IO.File.WriteAllText(extra, "unrelated data"); members.Add(new(extra, "group.part009.rar", 8)); var candidate = new ArchiveCandidate { DisplayName = "manual rar", Format = ArchiveFormat.Rar, VolumeKind = VolumeKind.RarParts, Status = CandidateStatus.Ready, Members = members }; var rename = new Renamer(); var c = new ExtractionCoordinator(new EmptyScanner(), rename, Engine); await Failure(() => c.RunAsync(candidate, new(t.File("out")), (_, _) => throw new Exception("Not encrypted")), EngineFailure.Unsupported); TestRunner.Check(!rename.Called); TestRunner.Check(System.IO.File.ReadAllText(extra) == "unrelated data"); }
        ));
    }
}
