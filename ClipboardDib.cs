using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace PhotoChop;

internal static class ClipboardDib
{
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr window);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr memory);

    public static BitmapSource? Read()
    {
        if (!OpenClipboard(IntPtr.Zero)) return null;
        byte[]? v5;
        byte[]? dib;
        try
        {
            v5 = ReadFormat(17); // CF_DIBV5
            dib = ReadFormat(8); // CF_DIB
        }
        finally { CloseClipboard(); }
        return Decode(v5) ?? Decode(dib);
    }

    private static byte[]? ReadFormat(uint format)
    {
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero) return null;
        ulong length = GlobalSize(handle).ToUInt64();
        if (length is < 40 or > 129_000_000) return null;
        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero) return null;
        try
        {
            var bytes = new byte[(int)length + 14];
            Marshal.Copy(pointer, bytes, 14, (int)length);
            return bytes;
        }
        finally { GlobalUnlock(handle); }
    }

    private static BitmapSource? Decode(byte[]? bytes)
    {
        if (bytes is null) return null;
        var dib = bytes.AsSpan(14);
        uint header = BinaryPrimitives.ReadUInt32LittleEndian(dib);
        if (header < 40 || header > dib.Length) return null;
        ushort depth = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        uint compression = BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]);
        uint palette = BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);
        if (palette == 0 && depth is > 0 and <= 8) palette = 1u << depth;
        int maskBytes = header == 40 ? compression switch { 3 => 12, 6 => 16, _ => 0 } : 0;
        long pixels = 14L + header + maskBytes + 4L * palette;
        if (pixels >= bytes.Length) return null;
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), (int)pixels);
        try
        {
            using var file = new MemoryStream(bytes, writable: false);
            return BitmapDecoder.Create(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames.FirstOrDefault();
        }
        catch (Exception error) when (error is IOException or NotSupportedException or ArgumentException or COMException)
        {
            return null;
        }
    }
}
