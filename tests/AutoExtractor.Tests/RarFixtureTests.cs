using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using AutoExtractor.Core;
using AutoExtractor.Engine;

namespace AutoExtractor.Tests;

public static class RarFixtureTests
{
    const string Prefix = "test_read_format_rar5_multiarchive.part";
    static string Root
    {
        get
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "tests", "fixtures", "libarchive", "manifest.json")))
                    return d.FullName;
            throw new IOException("Pinned RAR fixture directory not found.");
        }
    }
    static string Fixtures => Path.Combine(Root, "tests", "fixtures", "libarchive");
    static SevenZipEngine Engine => new(Path.Combine(Root, "vendor", "7zip", "7z.exe"));

    public static void Register()
    {
        foreach (var format in new[] { "rar4", "rar5" })
            foreach (var encryption in new[] { "solid_encrypted", "encrypted_filenames" })
                Add($"real {format} {encryption} known-password retry", async temp =>
                {
                    // Password and exact plaintext provenance: pinned upstream test_read_format_rar_encryption.c:28-35.
                    const string password = "password";
                    var source = Copy($"test_read_format_{format}_{encryption}.rar", temp, "加密样例.jpg");
                    string before = Hash(source);
                    try
                    {
                        await Engine.ExtractAsync(source, Path.Combine(temp, "wrong-password-output"), "incorrect-fixture-password");
                        throw new Exception("Incorrect RAR password accepted.");
                    }
                    catch (ArchiveException e) { TestRunner.Check(e.Failure is EngineFailure.NeedsPassword or EngineFailure.PasswordOrCorruption, $"Unexpected password error: {e.Failure}"); }
                    var scanner = new ArchiveScanner();
                    var renamer = new RenameService();
                    var candidate = (await scanner.ScanAsync([source])).Candidates.Single();
                    TestRunner.Check(candidate.Format == ArchiveFormat.Rar && candidate.Status == CandidateStatus.Ready);
                    int requests = 0;
                    var result = await new ExtractionCoordinator(scanner, renamer, Engine).RunAsync(candidate, new(Path.Combine(temp, "output")), (request, _) =>
                    {
                        requests++;
                        if (requests > 2)
                            throw new Exception("Correct documented RAR password did not unlock the fixture.");
                        if (requests == 2)
                            TestRunner.Check(request.PreviousFailed);
                        return Task.FromResult<string?>(requests == 1 ? "incorrect-fixture-password" : password);
                    });
                    TestRunner.Check(requests == 2);
                    var files = Directory.GetFiles(result.OutputDirectory, "*", SearchOption.AllDirectories);
                    TestRunner.Check(files.Length == 4);
                    foreach (char letter in "abcd")
                    {
                        var path = files.Single(p => Path.GetFileName(p) == $"{letter}.txt");
                        TestRunner.Check(File.ReadAllText(path) == $"This is from {letter}.txt");
                    }
                    TestRunner.Check(result.Journals.Count == 1);
                    TestRunner.Check(!File.ReadAllText(result.Journals[0]).Contains(password, StringComparison.Ordinal));
                    await renamer.RestoreAsync(result.Journals[0]);
                    TestRunner.Check(Hash(source) == before);
                });
        TestRunner.Cases.Add(("RAR fixture provenance and hashes", () =>
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "manifest.json")));
            TestRunner.Check(manifest.RootElement.GetProperty("Revision").GetString() == "c719b9b1f56621d92063a85361cc8d114f5575a9");
            foreach (var file in manifest.RootElement.GetProperty("Files").EnumerateArray())
            {
                CheckHash(file.GetProperty("SourceFile").GetString()!, file.GetProperty("SourceSha256").GetString()!);
                if (file.TryGetProperty("DecodedFile", out var decoded))
                    CheckHash(decoded.GetString()!, file.GetProperty("DecodedSha256").GetString()!);
            }
            return Task.CompletedTask;
        }
        ));
        Add("real RAR5 compressed disguised archive", async temp =>
        {
            var source = Copy("test_read_format_rar5_compressed.rar", temp, "真实压缩.jpg");
            var candidate = (await new ArchiveScanner().ScanAsync([source])).Candidates.Single();
            TestRunner.Check(candidate.Format == ArchiveFormat.Rar && candidate.Status == CandidateStatus.Ready);
            var listing = await Engine.ListAsync(source, null);
            TestRunner.Check(listing.Entries.Count == 1 && listing.Entries[0].Path == "test.bin" && listing.Entries[0].Size == 1200 && !listing.Entries[0].IsLink);
            var output = Path.Combine(temp, "extracted");
            await Engine.ExtractAsync(source, output, null);
            var content = File.ReadAllBytes(Path.Combine(output, "test.bin"));
            TestRunner.Check(content.Length == 1200 && Crc(content) == 0x7CCA70CD);
            // Independently regenerate the upstream fixture's documented integer sequence.
            for (int i = 0; i < 300; i++)
            {
                int k = i + 1;
                TestRunner.Check(BinaryPrimitives.ReadInt32LittleEndian(content.AsSpan(i * 4, 4)) == Math.Max(0, k * k - 3 * k + 1));
            }
        });
        Add("real RAR5 disguised eight-volume extraction and restore", async temp =>
        {
            var sources = CopyParts(temp);
            var originalHashes = sources.Select(Hash).ToArray();
            var scanner = new ArchiveScanner();
            var renamer = new RenameService();
            var candidate = (await scanner.ScanAsync([sources[0]])).Candidates.Single();
            TestRunner.Check(candidate.Format == ArchiveFormat.Rar && candidate.VolumeKind == VolumeKind.RarParts && candidate.Members.Count == 8 && candidate.Status == CandidateStatus.Ready, candidate.Explanation);
            var result = await new ExtractionCoordinator(scanner, renamer, Engine).RunAsync(candidate, new(Path.Combine(temp, "output")), (_, _) => Task.FromResult<string?>(null));
            var files = Directory.GetFiles(result.OutputDirectory, "*", SearchOption.AllDirectories);
            TestRunner.Check(files.Length == 2);
            var first = File.ReadAllBytes(files.Single(p => Path.GetFileName(p) == "bsdcat_test"));
            var second = File.ReadAllBytes(files.Single(p => Path.GetFileName(p) == "bsdtar_test"));
            TestRunner.Check(first.Length == 144608 && Crc(first) == 0x35277473);
            TestRunner.Check(second.Length == 365672 && Crc(second) == 0xE59665F8);
            // These are upstream Unix test programs; tests never execute extracted content.
            TestRunner.Check(result.Journals.Count == 1);
            await renamer.RestoreAsync(result.Journals[0]);
            for (int i = 0; i < sources.Length; i++)
                TestRunner.Check(Hash(sources[i]) == originalHashes[i]);
        });
        Add("real RAR5 middle volume gap preserves input", async temp =>
        {
            var sources = CopyParts(temp, omit: 4);
            var scanner = new ArchiveScanner();
            var candidate = (await scanner.ScanAsync([sources[0]])).Candidates.Single();
            TestRunner.Check(candidate.Status == CandidateStatus.MissingVolumes);
            var hashes = sources.Select(Hash).ToArray();
            await Missing(() => new ExtractionCoordinator(scanner, new RenameService(), Engine).RunAsync(candidate, new(Path.Combine(temp, "output")), (_, _) => Task.FromResult<string?>(null)));
            for (int i = 0; i < sources.Length; i++)
                TestRunner.Check(Hash(sources[i]) == hashes[i]);
            TestRunner.Check(new RenameService().FindJournals(temp).Count == 0);
        });
        Add("real RAR5 missing last volume fails engine validation", async temp =>
        {
            string first = "";
            for (int i = 1; i <= 7; i++)
            {
                var path = Copy($"{Prefix}{i:00}.rar", temp, $"{Prefix}{i:00}.rar");
                if (i == 1)
                    first = path;
            }
            await Missing(() => Engine.ListAsync(first, null));
        });
    }

    static void CheckHash(string name, string expected) => TestRunner.Check(Hash(Path.Combine(Fixtures, name)) == expected, $"Fixture digest changed: {name}");
    static string Hash(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f));
    }
    static string Copy(string name, string directory, string target)
    {
        var path = Path.Combine(directory, target);
        File.Copy(Path.Combine(Fixtures, name), path);
        return path;
    }
    static string[] CopyParts(string directory, int omit = 0) => Enumerable.Range(1, 8).Where(i => i != omit).Select(i => Copy($"{Prefix}{i:00}.rar", directory, $"真实分卷{i:00}.{(i % 2 == 0 ? "mp4" : "jpg")}")).ToArray();
    static async Task Missing(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ArchiveException e) { TestRunner.Check(e.Failure is EngineFailure.MissingVolumes or EngineFailure.CorruptArchive, $"Unexpected error: {e.Failure}"); return; }
        throw new Exception("Incomplete RAR volume set was accepted.");
    }
    static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint value = 0xffffffff;
        foreach (byte b in bytes)
        {
            value ^= b;
            for (int i = 0; i < 8; i++)
                value = (value >> 1) ^ ((value & 1) != 0 ? 0xedb88320u : 0);
        }
        return ~value;
    }
    static void Add(string name, Func<string, Task> test) => TestRunner.Cases.Add((name, async () =>
    {
        var temp = Path.Combine(Path.GetTempPath(), "AutoExtractor-RAR-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            await test(temp);
        }
        finally { Directory.Delete(temp, true); }
    }
    ));
}
