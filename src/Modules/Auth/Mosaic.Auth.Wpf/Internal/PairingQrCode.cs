using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mosaic.Auth.Wpf.Internal;

/// <summary>ISO/IEC 18004 byte-mode QR, version 10-L. Pairing links are ASCII URIs.</summary>
internal static class PairingQrCode
{
    private const int Size = 57;
    private const int DataBytes = 274;
    private const int CorrectionBytes = 18;

    public static BitmapSource Render(string uri)
    {
        var bytes = Encoding.ASCII.GetBytes(uri);
        if (bytes.Length > 271 || uri.Any(character => !char.IsAscii(character)))
            throw new ArgumentException("配对链接超出二维码容量。", nameof(uri));

        var bits = new List<bool>(DataBytes * 8);
        Append(bits, 4, 4);
        Append(bits, bytes.Length, 16);
        foreach (var value in bytes) Append(bits, value, 8);
        Append(bits, 0, 4);
        for (var index = 0; bits.Count < DataBytes * 8; index++) Append(bits, index % 2 == 0 ? 0xEC : 0x11, 8);
        var data = new byte[DataBytes];
        for (var i = 0; i < bits.Count; i++)
            if (bits[i]) data[i / 8] |= (byte)(1 << (7 - i % 8));
        var words = Interleave(data);

        var modules = new bool?[Size, Size];
        for (var i = 0; i < Size; i++)
        {
            modules[i, 6] = i % 2 == 0;
            modules[6, i] = i % 2 == 0;
        }
        Finder(modules, 3, 3);
        Finder(modules, Size - 4, 3);
        Finder(modules, 3, Size - 4);
        foreach (var x in new[] { 6, 28, 50 })
        foreach (var y in new[] { 6, 28, 50 })
        {
            if ((x, y) is (6, 6) or (6, 50) or (50, 6)) continue;
            for (var dx = -2; dx <= 2; dx++)
            for (var dy = -2; dy <= 2; dy++)
                modules[x + dx, y + dy] = Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1;
        }
        AddMetadata(modules);

        var position = 0;
        var upward = true;
        for (var right = Size - 1; right > 0; right -= 2)
        {
            if (right == 6) right--;
            for (var index = 0; index < Size; index++)
            {
                var y = upward ? Size - 1 - index : index;
                for (var x = right; x >= right - 1; x--)
                {
                    if (modules[x, y].HasValue) continue;
                    var dark = position < words.Length * 8 && Bit(words[position / 8], 7 - position % 8);
                    modules[x, y] = dark ^ ((x + y) % 2 == 0);
                    position++;
                }
            }
            upward = !upward;
        }

        const int scale = 4;
        const int border = 4;
        const int width = (Size + 2 * border) * scale;
        var pixels = new byte[width * width];
        Array.Fill(pixels, byte.MaxValue);
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            if (modules[x, y] != true) continue;
            for (var line = 0; line < scale; line++)
                Array.Clear(pixels, ((y + border) * scale + line) * width + (x + border) * scale, scale);
        }
        return BitmapSource.Create(width, width, 96, 96, PixelFormats.Gray8, null, pixels, width);
    }

    private static void Finder(bool?[,] modules, int x, int y)
    {
        for (var dx = -4; dx <= 4; dx++)
        for (var dy = -4; dy <= 4; dy++)
        {
            if (x + dx < 0 || x + dx >= Size || y + dy < 0 || y + dy >= Size) continue;
            modules[x + dx, y + dy] = Math.Max(Math.Abs(dx), Math.Abs(dy)) is not (2 or 4);
        }
    }

    private static void AddMetadata(bool?[,] modules)
    {
        const int format = 0x77C4; // EC level L, mask 0.
        const int version = 0x0A4D3; // Version 10 and its BCH remainder.
        for (var i = 0; i < 6; i++) modules[8, i] = Bit(format, i);
        modules[8, 7] = Bit(format, 6);
        modules[8, 8] = Bit(format, 7);
        modules[7, 8] = Bit(format, 8);
        for (var i = 9; i < 15; i++) modules[14 - i, 8] = Bit(format, i);
        for (var i = 0; i < 8; i++) modules[Size - 1 - i, 8] = Bit(format, i);
        for (var i = 8; i < 15; i++) modules[8, Size - 15 + i] = Bit(format, i);
        modules[8, Size - 8] = true;
        for (var i = 0; i < 18; i++)
        {
            var x = Size - 11 + i % 3;
            var y = i / 3;
            modules[x, y] = modules[y, x] = Bit(version, i);
        }
    }

    private static byte[] Interleave(byte[] data)
    {
        byte[][] blocks = [data[..68], data[68..136], data[136..205], data[205..]];
        var correction = blocks.Select(Correct).ToArray();
        var words = new List<byte>(346);
        for (var index = 0; index < 69; index++)
            foreach (var block in blocks)
                if (index < block.Length) words.Add(block[index]);
        for (var index = 0; index < CorrectionBytes; index++)
            foreach (var block in correction) words.Add(block[index]);
        return words.ToArray();
    }

    private static byte[] Correct(byte[] data)
    {
        var generator = new byte[CorrectionBytes];
        generator[^1] = 1;
        byte root = 1;
        for (var i = 0; i < CorrectionBytes; i++)
        {
            for (var j = 0; j < CorrectionBytes; j++)
            {
                generator[j] = Multiply(generator[j], root);
                if (j + 1 < CorrectionBytes) generator[j] ^= generator[j + 1];
            }
            root = Multiply(root, 2);
        }

        var result = new byte[CorrectionBytes];
        foreach (var value in data)
        {
            var factor = (byte)(value ^ result[0]);
            for (var i = 0; i < CorrectionBytes - 1; i++) result[i] = result[i + 1];
            result[^1] = 0;
            for (var i = 0; i < CorrectionBytes; i++) result[i] ^= Multiply(generator[i], factor);
        }
        return result;
    }

    private static byte Multiply(byte left, byte right)
    {
        var result = 0;
        for (var i = 7; i >= 0; i--)
        {
            result = (result << 1) ^ ((result >> 7) * 0x11D);
            if (Bit(right, i)) result ^= left;
        }
        return (byte)result;
    }

    private static void Append(List<bool> bits, int value, int length)
    {
        for (var i = length - 1; i >= 0; i--) bits.Add(Bit(value, i));
    }

    private static bool Bit(int value, int index) => ((value >> index) & 1) != 0;
}
