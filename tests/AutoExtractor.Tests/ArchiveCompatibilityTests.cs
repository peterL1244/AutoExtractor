using System.Buffers.Binary;
using System.IO.Compression;
using AutoExtractor.Engine;

namespace AutoExtractor.Tests;

public static class ArchiveCompatibilityTests
{
    public static void Register()
    {
        TestRunner.Cases.Add(("ZIP64 offset-only end record reads and extracts", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "AutoExtractor-zip64-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "zip64.tmp");
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    using var w = new StreamWriter(zip.CreateEntry("small.txt").Open());
                    w.Write("zip64 offset-only fixture");
                }
                var original = File.ReadAllBytes(path);
                int end = original.Length - 22;
                var eocd = original[end..];
                ulong count = BinaryPrimitives.ReadUInt16LittleEndian(eocd.AsSpan(10));
                ulong size = BinaryPrimitives.ReadUInt32LittleEndian(eocd.AsSpan(12));
                ulong offset = BinaryPrimitives.ReadUInt32LittleEndian(eocd.AsSpan(16));
                // Simulate the ZIP64 layout used when only central directory offset overflows.
                // The small true offset is in ZIP64 metadata, so no multi-gigabyte file is needed.
                var zip64 = new byte[76];
                BinaryPrimitives.WriteUInt32LittleEndian(zip64, 0x06064b50);
                BinaryPrimitives.WriteUInt64LittleEndian(zip64.AsSpan(4), 44);
                BinaryPrimitives.WriteUInt16LittleEndian(zip64.AsSpan(12), 45);
                BinaryPrimitives.WriteUInt16LittleEndian(zip64.AsSpan(14), 45);
                BinaryPrimitives.WriteUInt64LittleEndian(zip64.AsSpan(24), count);
                BinaryPrimitives.WriteUInt64LittleEndian(zip64.AsSpan(32), count);
                BinaryPrimitives.WriteUInt64LittleEndian(zip64.AsSpan(40), size);
                BinaryPrimitives.WriteUInt64LittleEndian(zip64.AsSpan(48), offset);
                BinaryPrimitives.WriteUInt32LittleEndian(zip64.AsSpan(56), 0x07064b50);
                BinaryPrimitives.WriteUInt64LittleEndian(zip64.AsSpan(64), (ulong)end);
                BinaryPrimitives.WriteUInt32LittleEndian(zip64.AsSpan(72), 1);
                BinaryPrimitives.WriteUInt32LittleEndian(eocd.AsSpan(16), uint.MaxValue);
                using (var output = File.Create(path))
                {
                    output.Write(original.AsSpan(0, end));
                    output.Write(zip64);
                    output.Write(eocd);
                }
                var root = new DirectoryInfo(AppContext.BaseDirectory);
                while (root is not null && !File.Exists(Path.Combine(root.FullName, "vendor", "7zip", "7z.exe")))
                    root = root.Parent;
                var engine = new SevenZipEngine(Path.Combine(root!.FullName, "vendor", "7zip", "7z.exe"));
                var listing = await engine.ListAsync(path, null);
                TestRunner.Check(listing.Entries.Single().Path == "small.txt");
                var destination = Path.Combine(directory, "out");
                await engine.ExtractAsync(path, destination, null);
                TestRunner.Check(File.ReadAllText(Path.Combine(destination, "small.txt")) == "zip64 offset-only fixture");
            }
            finally { Directory.Delete(directory, true); }
        }
        ));
    }
}
