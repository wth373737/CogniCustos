using System.IO.Compression;

namespace CogniCustos.Documents;

/// <summary>
/// Minimal PNG writer for one BGRA32 frame.
///
/// The vision path needs an encoded image to post to Ollama, and the reader must not drag
/// SkiaSharp into the app (Avalonia pins 3.119; a second 4.x would collide). PNG needs only
/// a Deflate stream and a CRC table, so it is cheaper to own these 70 lines than to take a
/// dependency for them.
/// </summary>
public static class PngEncoder
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Encode BGRA32 (bottom of the reader's pipeline) to a PNG byte array.</summary>
    public static byte[] FromBgra(int width, int height, byte[] bgra)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (bgra.Length < width * height * 4) throw new ArgumentException("pixel buffer is too small", nameof(bgra));

        // raw scanlines: one filter byte (0 = none) + RGB per pixel
        var stride = width * 3;
        var raw = new byte[(stride + 1) * height];
        var o = 0;
        for (var y = 0; y < height; y++)
        {
            raw[o++] = 0;
            var row = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                raw[o++] = bgra[i + 2]; // R
                raw[o++] = bgra[i + 1]; // G
                raw[o++] = bgra[i];     // B
            }
        }

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            z.Write(raw, 0, raw.Length);

        using var png = new MemoryStream();
        png.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        WriteBigEndian(ihdr[..4], width);
        WriteBigEndian(ihdr[4..8], height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // colour type: truecolour
        ihdr[10] = 0; // deflate
        ihdr[11] = 0; // adaptive filtering
        ihdr[12] = 0; // no interlace
        WriteChunk(png, "IHDR", ihdr);

        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", ReadOnlySpan<byte>.Empty);

        return png.ToArray();
    }

    private static void WriteChunk(Stream target, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        WriteBigEndian(header[..4], data.Length);
        header[4] = (byte)type[0];
        header[5] = (byte)type[1];
        header[6] = (byte)type[2];
        header[7] = (byte)type[3];
        target.Write(header);
        target.Write(data);

        var crc = Crc32(0xFFFFFFFF, header[4..]);
        crc = Crc32(crc, data);
        Span<byte> tail = stackalloc byte[4];
        WriteBigEndian(tail, (int)(crc ^ 0xFFFFFFFF));
        target.Write(tail);
    }

    private static void WriteBigEndian(Span<byte> target, int value)
    {
        target[0] = (byte)(value >> 24);
        target[1] = (byte)(value >> 16);
        target[2] = (byte)(value >> 8);
        target[3] = (byte)value;
    }

    private static uint[]? _crcTable;

    private static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        var table = _crcTable ??= BuildCrcTable();
        foreach (var b in data) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }
}
