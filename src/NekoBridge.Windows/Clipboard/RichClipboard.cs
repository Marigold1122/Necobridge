using System;
using System.IO;
using System.Linq;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Collections.Generic;
using System.Text.Json;
using System.Runtime.InteropServices.ComTypes;
using NekoBridge.Core;
using NekoBridge.Windows.DragDrop;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ClipboardSmoke")]

namespace NekoBridge.Windows;

internal sealed record RichClipboard(string Kind, string Fingerprint, string[]? Paths = null, byte[]? Png = null,
    Guid Peer = default, FileOffer? Offer = null)
{
    internal const int MaxBytes = 64 * 1024 * 1024;
    internal static RichClipboard? Read()
    {
        // Explorer's file selection takes precedence over preview images and text.
        if (System.Windows.Clipboard.ContainsFileDropList())
        {
            string[] paths = System.Windows.Clipboard.GetFileDropList().Cast<string>().ToArray();
            if (paths.Length is < 1 or > 10000) throw new InvalidOperationException();
            return new("files", Digest("files:" + string.Join("\0", paths)), paths);
        }
        Marshal.ThrowExceptionForHR(OleGetClipboard(out var native));
        try
        {
            RichClipboard? virtualFiles = ReadVirtual(native);
            if (virtualFiles != null) return virtualFiles;
            RichClipboard? captured = ClipboardImage.CaptureFromDataObject(native);
            if (captured != null) return captured;
        }
        finally { if (Marshal.IsComObject(native)) Marshal.ReleaseComObject(native); }
        if (!System.Windows.Clipboard.ContainsImage()) return null;
        BitmapSource? image = System.Windows.Clipboard.GetImage();
        if (image == null) return null;
        CheckDimensions(image.PixelWidth, image.PixelHeight);
        var pixels = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0); pixels.Freeze();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(pixels));
        using var stream = new MemoryStream(); encoder.Save(stream);
        if (stream.Length > MaxBytes) throw new InvalidOperationException();
        return Image(stream.ToArray());
    }
    internal static string FilesFingerprint(IEnumerable<FileEntry> entries) => Digest("virtual-files:" +
        JsonSerializer.Serialize(entries.OrderBy(e => e.RelativePath, StringComparer.Ordinal).Select(e => new { e.RelativePath, e.Length, e.IsDirectory })));
    internal static RichClipboard? ReadVirtual(System.Runtime.InteropServices.ComTypes.IDataObject data)
    {
        var format = new FORMATETC { cfFormat = unchecked((short)RegisterClipboardFormat("FileGroupDescriptorW")),
            dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
        if (data.QueryGetData(ref format) != 0) return null;
        data.GetData(ref format, out var medium);
        try
        {
            if (medium.tymed != TYMED.TYMED_HGLOBAL || GlobalSize(medium.unionmember) < 4) throw new InvalidDataException();
            nint pointer = GlobalLock(medium.unionmember);
            if (pointer == 0) throw new ExternalException();
            try
            {
                int count = Marshal.ReadInt32(pointer);
                if (count is < 1 or > 10000 || (ulong)GlobalSize(medium.unionmember) < (ulong)(4 + 592 * count)) throw new InvalidDataException();
                var entries = new List<FileEntry>(count);
                for (int index = 0; index < count; index++)
                {
                    nint descriptor = pointer + 4 + index * 592;
                    string name = Marshal.PtrToStringUni(descriptor + 72, 260)!;
                    int end = name.IndexOf('\0'); if (end < 0) throw new InvalidDataException();
                    long length = checked((long)(((ulong)(uint)Marshal.ReadInt32(descriptor,64) << 32) | (uint)Marshal.ReadInt32(descriptor,68)));
                    entries.Add(new FileEntry { Index = index, RelativePath = name[..end].Replace('\\','/'), Length = length,
                        IsDirectory = (Marshal.ReadInt32(descriptor,36) & 16) != 0 });
                }
                return new RichClipboard("files", FilesFingerprint(entries));
            }
            finally { GlobalUnlock(medium.unionmember); }
        }
        finally { ReleaseStgMedium(ref medium); }
    }
    internal static RichClipboard Image(byte[] png)
    {
        // Inspect IHDR before invoking any image codec or allocating pixels.
        if (png.Length < 33 || png.Length > MaxBytes || !png.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}) ||
            !png.AsSpan(12, 4).SequenceEqual("IHDR"u8)) throw new InvalidDataException();
        int width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16,4));
        int height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20,4));
        CheckDimensions(width, height);
        using var input = new MemoryStream(png, false);
        var decoder = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1) throw new InvalidDataException();
        BitmapSource image = decoder.Frames[0]; CheckDimensions(image.PixelWidth, image.PixelHeight);
        var normalized = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[checked(width * height * 4)]; normalized.CopyPixels(pixels, width * 4, 0);
        return new("image", Digest($"image:{width}:{height}:" + Convert.ToHexString(SHA256.HashData(pixels))), Png: png);
    }
    internal static void CheckDimensions(int width, int height)
    {
        if (width < 1 || height < 1 || width > 16384 || height > 16384 || (long)width * height > 16 * 1024 * 1024)
            throw new InvalidDataException("剪贴板图片超过尺寸限制。");
    }
    internal static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal uint Write(BridgeNode node, Action<string> log)
    {
        if (Kind == "files")
        {
            if (Paths != null)
            {
                var local = new System.Windows.DataObject();
                var files = new System.Collections.Specialized.StringCollection(); files.AddRange(Paths);
                local.SetFileDropList(files);
                return SetNative((System.Runtime.InteropServices.ComTypes.IDataObject)local);
            }
            var cache = new RemoteFileCache(node, Peer, Offer!.Id, log);
            try { return SetNative(new VirtualFileDataObject(Offer, cache, log)); }
            catch { cache.Dispose(); throw; }
            // OLE and active Shell IStreams retain the cache. Do not abort on replacement.
        }
        return SetNative((System.Runtime.InteropServices.ComTypes.IDataObject)CreateImageData());
    }
    internal System.Windows.DataObject CreateImageData()
    {
        using var input = new MemoryStream(Png!, false);
        var decoder = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var image = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        image.Freeze();
        int width = image.PixelWidth, height = image.PixelHeight, stride = width * 4;
        byte[] pixels = new byte[stride * height]; image.CopyPixels(pixels, stride, 0);
        // Explicit top-down BI_BITFIELDS DIBV5 with alpha, plus legacy bottom-up BI_RGB DIB.
        byte[] v5 = new byte[124 + pixels.Length];
        Put(v5,0,124); Put(v5,4,width); Put(v5,8,-height); Put(v5,12,0x00200001); Put(v5,16,3); Put(v5,20,pixels.Length);
        Put(v5,40,0x00ff0000); Put(v5,44,0x0000ff00); Put(v5,48,0x000000ff); Put(v5,52,unchecked((int)0xff000000)); Put(v5,56,0x73524742);
        pixels.CopyTo(v5,124);
        byte[] dib = new byte[40 + pixels.Length]; Put(dib,0,40); Put(dib,4,width); Put(dib,8,height); Put(dib,12,0x00200001); Put(dib,20,pixels.Length);
        for(int row=0;row<height;row++) Buffer.BlockCopy(pixels,row*stride,dib,40+(height-row-1)*stride,stride);
        var data = new System.Windows.DataObject();
        data.SetData(DataFormats.Bitmap, image, false);
        data.SetData("PNG", new MemoryStream(Png!, false), false);
        data.SetData("DeviceIndependentBitmap", new MemoryStream(dib, false), false);
        data.SetData(DataFormats.GetDataFormat(17).Name, new MemoryStream(v5, false), false);
        return data;
    }
    private static void Put(byte[] bytes,int offset,int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset,4),value);
    private static uint SetNative(System.Runtime.InteropServices.ComTypes.IDataObject data)
    {
        Marshal.ThrowExceptionForHR(OleSetClipboard(data));
        uint sequence = GetClipboardSequenceNumber();
        // Never mark a competing application's intervening copy as our write.
        return OleIsCurrentClipboard(data) == 0 ? sequence : unchecked(sequence - 1);
    }
    [DllImport("ole32.dll")] private static extern int OleGetClipboard([MarshalAs(UnmanagedType.Interface)] out System.Runtime.InteropServices.ComTypes.IDataObject data);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("ole32.dll")] private static extern int OleIsCurrentClipboard([MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject data);
    [DllImport("ole32.dll")] private static extern int OleSetClipboard([MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject data);
}
