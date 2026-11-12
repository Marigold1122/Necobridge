using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using NekoBridge.Core;

namespace NekoBridge.Windows.DragDrop;

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class VirtualFileDataObject : IDataObject, IDataObjectAsyncCapability
{
    private static readonly short Descriptor = unchecked((short)OleNative.RegisterClipboardFormat("FileGroupDescriptorW"));
    private static readonly short Contents = unchecked((short)OleNative.RegisterClipboardFormat("FileContents"));
    private static readonly short PreferredEffect = unchecked((short)OleNative.RegisterClipboardFormat("Preferred DropEffect"));
    private readonly FileEntry[] entries;
    private readonly RemoteFileCache cache;
    private readonly Action<string> log;
    private readonly object gate = new();
    private bool asynchronous = true;
    private GCHandle operation;
    internal VirtualFileDataObject(FileOffer offer, RemoteFileCache cache, Action<string> log)
    {
        this.cache = cache; this.log = log; entries = offer.Entries.ToArray();
        if (entries.Length == 0 || entries.Length > 100000) throw new InvalidDataException("Invalid file offer item count.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var indices = new HashSet<int>();
        foreach (FileEntry entry in entries)
        {
            string name = entry.RelativePath.Replace('/', '\\');
            if (name.Length == 0 || name.Length >= 260 || Path.IsPathRooted(name) || entry.Length < 0 || entry.Index < 0 || !indices.Add(entry.Index) || !names.Add(name))
                throw new InvalidDataException("Invalid or overlong virtual file path.");
            foreach (string segment in name.Split('\\'))
            {
                if (segment.Length == 0 || segment == "." || segment == ".." || segment.EndsWith(' ') || segment.EndsWith('.') || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new InvalidDataException("Unsafe virtual file path.");
                string stem = segment.Split('.')[0].ToUpperInvariant();
                if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] >= '1' && stem[3] <= '9'))
                    throw new InvalidDataException("Reserved virtual file name.");
            }
        }
    }
    private static FORMATETC Format(short id, TYMED medium, int index = -1) => new() { cfFormat = id, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = index, tymed = medium };
    public int QueryGetData(ref FORMATETC format)
    {
        if (format.dwAspect != DVASPECT.DVASPECT_CONTENT || format.ptd != IntPtr.Zero) return OleNative.FormatError;
        if (format.cfFormat == Contents)
        {
            if ((format.tymed & TYMED.TYMED_ISTREAM) == 0) return OleNative.MediumError;
            return format.lindex >= 0 && format.lindex < entries.Length && !entries[format.lindex].IsDirectory ? 0 : OleNative.IndexError;
        }
        if (format.cfFormat != Descriptor && format.cfFormat != PreferredEffect) return OleNative.FormatError;
        if (format.lindex != -1) return OleNative.IndexError;
        return (format.tymed & TYMED.TYMED_HGLOBAL) != 0 ? 0 : OleNative.MediumError;
    }
    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = default;
        int result = QueryGetData(ref format); if (result != 0) Marshal.ThrowExceptionForHR(result);
        if (format.cfFormat == Contents)
        {
            var stream = new CachedComStream(cache, entries[format.lindex]);
            medium.tymed = TYMED.TYMED_ISTREAM;
            medium.unionmember = Marshal.GetComInterfaceForObject(stream, typeof(IStream));
            return;
        }
        byte[] bytes;
        if (format.cfFormat == PreferredEffect) bytes = BitConverter.GetBytes(1);
        else
        {
            bytes = new byte[checked(4 + 592 * entries.Length)];
            using var writer = new BinaryWriter(new MemoryStream(bytes), Encoding.Unicode);
            writer.Write(entries.Length);
            for (int i = 0; i < entries.Length; i++)
            {
                FileEntry entry = entries[i]; int start = 4 + i * 592;
                writer.BaseStream.Position = start;
                writer.Write(entry.IsDirectory ? 0x80000004u : 0x80000044u); // FD_UNICODE | ATTRIBUTES | FILESIZE
                writer.BaseStream.Position = start + 36;
                writer.Write(entry.IsDirectory ? 0x10u : 0x80u);
                writer.BaseStream.Position = start + 64;
                writer.Write((uint)((ulong)entry.Length >> 32)); writer.Write((uint)entry.Length);
                writer.Write(Encoding.Unicode.GetBytes(entry.RelativePath.Replace('/', '\\')));
            }
        }
        IntPtr memory = OleNative.GlobalAlloc(0x42, (UIntPtr)bytes.Length);
        if (memory == IntPtr.Zero) throw new OutOfMemoryException();
        IntPtr pointer = OleNative.GlobalLock(memory);
        if (pointer == IntPtr.Zero) { OleNative.GlobalFree(memory); throw new OutOfMemoryException(); }
        try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
        finally { OleNative.GlobalUnlock(memory); }
        medium.tymed = TYMED.TYMED_HGLOBAL; medium.unionmember = memory;
    }
    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        if (direction != DATADIR.DATADIR_GET) throw new COMException("Read only.", OleNative.NotImpl);
        return new FormatEnumerator(new[] { Format(Descriptor, TYMED.TYMED_HGLOBAL), Format(Contents, TYMED.TYMED_ISTREAM), Format(PreferredEffect, TYMED.TYMED_HGLOBAL) });
    }
    public int GetCanonicalFormatEtc(ref FORMATETC input, out FORMATETC output) { output = input; output.ptd = IntPtr.Zero; return 0x40130; }
    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new COMException("Not supported.", OleNative.NotImpl);
    public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release) => throw new COMException("Read only.", OleNative.NotImpl);
    public int DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink sink, out int connection) { connection = 0; return OleNative.NotImpl; }
    public void DUnadvise(int connection) => throw new COMException("Not supported.", OleNative.NotImpl);
    public int EnumDAdvise(out IEnumSTATDATA enumerator) { enumerator = null!; return OleNative.NotImpl; }
    public int SetAsyncMode(bool value) { lock (gate) asynchronous = value; return 0; }
    public int GetAsyncMode(out bool value) { lock (gate) value = asynchronous; return 0; }
    public int StartOperation(IBindCtx? context) { lock (gate) { if (!operation.IsAllocated) operation = GCHandle.Alloc(this); } return 0; }
    public int InOperation(out bool value) { lock (gate) value = operation.IsAllocated; return 0; }
    public int EndOperation(int result, IBindCtx? context, uint effects)
    {
        lock (gate) { if (operation.IsAllocated) operation.Free(); }
        if (result < 0) log("目标程序异步提取文件失败：0x" + result.ToString("X8"));
        return 0;
    }
    internal void Abort() { lock (gate) { if (operation.IsAllocated) operation.Free(); } cache.Dispose(); }
}
