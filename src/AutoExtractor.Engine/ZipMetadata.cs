using System.Buffers.Binary;
using System.Text;
using AutoExtractor.Core;

namespace AutoExtractor.Engine;

// 7-Zip sanitizes control characters in text listings. Read central-directory names as
// bytes as well, so that the preflight checks the original names, not a sanitized display.
internal static class ZipMetadata
{
    public static void Validate(string path, CancellationToken cancellationToken)
    {
        var files = new List<string> { path };
        if (path.EndsWith(".001", StringComparison.OrdinalIgnoreCase))
        {
            files.Clear();
            var stem = path[..^3];
            for (int n = 1; n <= 100000; n++)
            {
                var part = stem + n.ToString("D3");
                if (!File.Exists(part))
                    break;
                files.Add(part);
            }
        }
        else if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var stem = path[..^4];
            for (int n = 1; n <= 100000; n++)
            {
                var part = stem + ".z" + n.ToString("D2");
                if (!File.Exists(part))
                    break;
                files.Insert(files.Count - 1, part);
            }
        }
        using var stream = new Segments(files);
        var tail = new byte[(int)Math.Min(stream.Length, 65557)];
        stream.Position = stream.Length - tail.Length;
        stream.ReadExactly(tail);
        int end = -1;
        for (int i = tail.Length - 22; i >= 0; i--)
            if (U32(tail, i) == 0x06054b50 && i + 22 + U16(tail, i + 20) == tail.Length)
            {
                end = i;
                break;
            }
        if (end < 0)
            throw new ArchiveException(EngineFailure.CorruptArchive, "ZIP 中央目录结束记录缺失。");
        long endPosition = stream.Length - tail.Length + end;
        long count = U16(tail, end + 10), size = U32(tail, end + 12);
        var locator = new byte[20];
        if (endPosition >= locator.Length)
        {
            stream.Position = endPosition - locator.Length;
            stream.ReadExactly(locator);
        }
        if (count == ushort.MaxValue || size == uint.MaxValue || U32(tail, end + 16) == uint.MaxValue || U32(locator, 0) == 0x07064b50)
        {
            // ZIP64 end records are immediately before the locator and classic end record.
            var area = new byte[(int)Math.Min(endPosition, 1024 * 1024)];
            stream.Position = endPosition - area.Length;
            stream.ReadExactly(area);
            int zip64 = -1;
            for (int i = area.Length - 76; i >= 0; i--)
                if (U32(area, i) == 0x06064b50 && BinaryPrimitives.ReadUInt64LittleEndian(area.AsSpan(i + 4)) >= 44 && BinaryPrimitives.ReadUInt64LittleEndian(area.AsSpan(i + 4)) == (ulong)(area.Length - i - 32))
                {
                    zip64 = i;
                    break;
                }
            if (zip64 < 0)
                throw new ArchiveException(EngineFailure.Unsupported, "无法安全确认 ZIP64 中央目录位置。");
            count = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(area.AsSpan(zip64 + 32)));
            size = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(area.AsSpan(zip64 + 40)));
            endPosition = endPosition - area.Length + zip64;
        }
        if (count > 200000 || size < 0 || size > endPosition)
            throw new ArchiveException(EngineFailure.Unsupported, "ZIP 目录超出安全处理范围。");
        stream.Position = endPosition - size;
        var header = new byte[46];
        for (long i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.ReadExactly(header);
            if (U32(header, 0) != 0x02014b50)
                throw new ArchiveException(EngineFailure.CorruptArchive, "ZIP 中央目录结构不完整。");
            int nameLength = U16(header, 28), extraLength = U16(header, 30), commentLength = U16(header, 32);
            var nameBytes = new byte[nameLength];
            stream.ReadExactly(nameBytes);
            var name = ((U16(header, 8) & 0x800) != 0 ? Encoding.UTF8 : Encoding.Latin1).GetString(nameBytes);
            var attributes = U32(header, 38);
            var unixMode = attributes >> 16;
            bool link = (unixMode & 0xF000) == 0xA000 || (attributes & 0x400) != 0;
            ArchiveSafety.ValidateEntry(new(name, 0, name.EndsWith('/'), link));
            var extra = new byte[extraLength];
            stream.ReadExactly(extra);
            for (int pos = 0; pos + 4 <= extra.Length;)
            {
                int id = U16(extra, pos), length = U16(extra, pos + 2);
                pos += 4;
                if (length > extra.Length - pos)
                    throw new ArchiveException(EngineFailure.CorruptArchive, "ZIP 扩展元数据截断。");
                if (id == 0x7075 && length >= 5)
                    ArchiveSafety.ValidateEntry(new(Encoding.UTF8.GetString(extra, pos + 5, length - 5), 0, false));
                pos += length;
            }
            stream.Position += commentLength;
            if (stream.Position > endPosition)
                throw new ArchiveException(EngineFailure.CorruptArchive, "ZIP 中央目录越界。");
        }
    }
    static ushort U16(byte[] bytes, int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at));
    static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    sealed class Segments : Stream
    {
        readonly List<(string Path, long Start, long Length)> parts = [];
        public Segments(IEnumerable<string> files)
        {
            long start = 0;
            foreach (var file in files)
            {
                var length = new FileInfo(file).Length;
                parts.Add((file, start, length));
                start += length;
            }
            Length = start;
        }
        public override long Length
        {
            get;
        }
        public override long Position
        {
            get; set;
        }
        public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => false;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (Position < 0 || Position > Length)
                throw new IOException("Invalid archive offset.");
            int read = 0;
            foreach (var part in parts)
            {
                if (Position < part.Start || Position >= part.Start + part.Length)
                    continue;
                using var file = File.OpenRead(part.Path);
                file.Position = Position - part.Start;
                int n = file.Read(buffer[read..]);
                read += n;
                Position += n;
                if (read == buffer.Length)
                    break;
            }
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => Position = (origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? Position : Length) + offset;
        public override void Flush()
        {
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
