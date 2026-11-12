using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace NekoBridge.Windows.DragDrop;

internal static class OleNative
{
    internal const int Ok = 0, False = 1, NotImpl = unchecked((int)0x80004001);
    internal const int FormatError = unchecked((int)0x80040064);
    internal const int MediumError = unchecked((int)0x80040069);
    internal const int IndexError = unchecked((int)0x80040068);
    internal const int Cancel = 0x40101, Drop = 0x40100, DefaultCursors = 0x40102;
    [DllImport("ole32.dll")] internal static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] internal static extern void OleUninitialize();
    [DllImport("ole32.dll")] internal static extern int DoDragDrop([MarshalAs(UnmanagedType.Interface)] IDataObject data, IDropSource source, uint effects, out uint effect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] internal static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] internal static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] internal static extern IntPtr GlobalFree(IntPtr memory);
    [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
}

[ComVisible(true), Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDropSource
{
    [PreserveSig] int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool escape, uint keys);
    [PreserveSig] int GiveFeedback(uint effect);
}

[ComVisible(true), Guid("3D8B0590-F691-11d2-8EA9-006097DF5BD4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDataObjectAsyncCapability
{
    [PreserveSig] int SetAsyncMode([MarshalAs(UnmanagedType.Bool)] bool value);
    [PreserveSig] int GetAsyncMode([MarshalAs(UnmanagedType.Bool)] out bool value);
    [PreserveSig] int StartOperation(IBindCtx? context);
    [PreserveSig] int InOperation([MarshalAs(UnmanagedType.Bool)] out bool value);
    [PreserveSig] int EndOperation(int result, IBindCtx? context, uint effects);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class FormatEnumerator : IEnumFORMATETC
{
    private readonly FORMATETC[] formats;
    private int position;
    internal FormatEnumerator(FORMATETC[] formats, int position = 0) { this.formats = formats; this.position = position; }
    public int Next(int count, FORMATETC[] output, int[]? fetched)
    {
        if (count < 0 || output.Length < count || (count != 1 && fetched == null)) return unchecked((int)0x80070057);
        int copied = 0;
        while (copied < count && position < formats.Length) output[copied++] = formats[position++];
        if (fetched != null && fetched.Length > 0) fetched[0] = copied;
        return copied == count ? 0 : 1;
    }
    public int Skip(int count) { if (count < 0) return unchecked((int)0x80070057); int left = formats.Length - position; position += Math.Min(count, left); return left >= count ? 0 : 1; }
    public int Reset() { position = 0; return 0; }
    public void Clone(out IEnumFORMATETC result) { result = new FormatEnumerator(formats, position); }
}
