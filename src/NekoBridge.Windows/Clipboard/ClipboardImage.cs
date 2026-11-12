using System;
using System.IO;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NekoBridge.Windows;

// Native format semantics matter: BI_RGB's fourth byte is reserved, not alpha.
internal static class ClipboardImage
{
    internal static RichClipboard? CaptureFromDataObject(IDataObject data)
    {
        foreach (short id in new[] { unchecked((short)RegisterClipboardFormat("PNG")), (short)17, (short)8, (short)2 })
        {
            var format = new FORMATETC { cfFormat = id, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1,
                tymed = id == 2 ? TYMED.TYMED_GDI : TYMED.TYMED_HGLOBAL };
            if (data.QueryGetData(ref format) != 0) continue;
            data.GetData(ref format, out var medium);
            try
            {
                if (medium.tymed != format.tymed || medium.unionmember == 0) throw new InvalidDataException("无效的剪贴板图片介质。");
                if (id == 2) return CaptureBitmap(medium.unionmember);
                byte[] bytes = ReadGlobal(medium.unionmember);
                return id is 17 or 8 ? DecodeDib(bytes) : RichClipboard.Image(bytes);
            }
            finally { ReleaseStgMedium(ref medium); }
        }
        return null;
    }

    private static byte[] ReadGlobal(nint handle)
    {
        ulong size = (ulong)GlobalSize(handle);
        if (size == 0 || size > RichClipboard.MaxBytes) throw new InvalidDataException("剪贴板图片数据长度无效。");
        nint pointer = GlobalLock(handle);
        if (pointer == 0) throw new ExternalException("无法锁定剪贴板图片。");
        try { byte[] bytes = new byte[(int)size]; Marshal.Copy(pointer, bytes, 0, bytes.Length); return bytes; }
        finally { GlobalUnlock(handle); }
    }

    internal static RichClipboard DecodeDib(byte[] bytes)
    {
        if (bytes.Length < 40 || bytes.Length > RichClipboard.MaxBytes) throw new InvalidDataException("DIB 数据截断。");
        int I(int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
        uint U(int offset) => unchecked((uint)I(offset));
        int header = I(0);
        if (header is not (40 or 52 or 56 or 108 or 124)) throw new InvalidDataException("不支持的 DIB 头。");
        if (header > bytes.Length) throw new InvalidDataException("DIB 头截断。");
        int width = I(4), signedHeight = I(8);
        if (signedHeight == int.MinValue) throw new InvalidDataException("无效的 DIB 高度。");
        int height = Math.Abs(signedHeight);
        RichClipboard.CheckDimensions(width, height);
        int planes = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12, 2));
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(14, 2));
        uint compression = U(16);
        if (planes != 1 || bits is not (24 or 32) || compression is not (0 or 3 or 6) || (bits == 24 && compression != 0))
            throw new InvalidDataException("不支持的 DIB 像素格式或压缩方式。");
        int offset = header;
        uint red = 0x00ff0000, green = 0x0000ff00, blue = 0x000000ff, alpha = 0;
        if (compression is 3 or 6)
        {
            int maskEnd = header == 40 ? 40 + (compression == 6 ? 16 : 12) : header;
            if (maskEnd > bytes.Length || (header != 40 && header < (compression == 6 ? 56 : 52))) throw new InvalidDataException("DIB 掩码截断。");
            red = U(40); green = U(44); blue = U(48);
            if (header >= 56 || compression == 6) alpha = U(52);
            offset = maskEnd;
            if (!ValidMask(red) || !ValidMask(green) || !ValidMask(blue) || (alpha != 0 && !ValidMask(alpha)) ||
                (red & green) != 0 || (red & blue) != 0 || (green & blue) != 0 || ((red | green | blue) & alpha) != 0 || (compression == 6 && alpha == 0))
                throw new InvalidDataException("无效的 DIB 颜色掩码。");
        }
        long tableEnd = offset + (long)U(32) * 4;
        if (tableEnd > bytes.Length) throw new InvalidDataException("DIB 颜色表截断。");
        offset = (int)tableEnd;
        if (header == 124 && (U(112) != 0 || U(116) != 0))
            throw new InvalidDataException("不支持带嵌入或链接配置文件的 DIB。");
        int stride = checked((width * bits + 31) / 32 * 4);
        int length = checked(stride * height);
        if ((long)offset + length > bytes.Length || (U(20) != 0 && (U(20) < length || (long)offset + U(20) > bytes.Length)))
            throw new InvalidDataException("DIB 像素数据截断。");
        byte[] pixels = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        {
            int source = offset + (signedHeight < 0 ? y : height - 1 - y) * stride;
            for (int x = 0; x < width; x++)
            {
                int s = source + x * (bits / 8), d = (y * width + x) * 4;
                uint value = bits == 32 ? U(s) : (uint)(bytes[s] | bytes[s + 1] << 8 | bytes[s + 2] << 16);
                pixels[d] = Channel(value, blue); pixels[d + 1] = Channel(value, green); pixels[d + 2] = Channel(value, red);
                pixels[d + 3] = alpha == 0 ? (byte)255 : Channel(value, alpha);
            }
        }
        return Encode(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4));
    }

    private static bool ValidMask(uint mask)
    {
        if (mask == 0) return false;
        uint shifted = mask >> System.Numerics.BitOperations.TrailingZeroCount(mask);
        return (shifted & unchecked(shifted + 1)) == 0;
    }
    private static byte Channel(uint value, uint mask)
    {
        int shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        uint maximum = mask >> shift;
        return (byte)((((ulong)(value & mask) >> shift) * 255 + maximum / 2) / maximum);
    }
    private static RichClipboard CaptureBitmap(nint handle)
    {
        // Query dimensions before WPF can allocate a bitmap; CF_BITMAP has no alpha contract.
        if (GetObject(handle, Marshal.SizeOf<NativeBitmap>(), out var bitmap) == 0) throw new InvalidDataException("无效的 HBITMAP。");
        RichClipboard.CheckDimensions(bitmap.Width, bitmap.Height);
        var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(handle, 0, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        return Encode(new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0));
    }
    internal static RichClipboard Encode(BitmapSource image)
    {
        RichClipboard.CheckDimensions(image.PixelWidth, image.PixelHeight);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream(); encoder.Save(stream);
        if (stream.Length > RichClipboard.MaxBytes) throw new InvalidDataException("剪贴板图片超过长度限制。");
        return RichClipboard.Image(stream.ToArray());
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeBitmap
    { public int Type, Width, Height, WidthBytes; public ushort Planes, BitsPixel; public nint Bits; }
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] private static extern int GetObject(nint handle, int size, out NativeBitmap bitmap);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
}
