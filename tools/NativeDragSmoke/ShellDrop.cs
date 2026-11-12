using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using NekoBridge.Windows.DragDrop;

internal static class ShellDrop
{
    internal static IDropTarget Open(string destination)
    {
        Directory.CreateDirectory(destination);
        var iid=typeof(IShellItem).GUID;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(destination,0,in iid,out var item));
        try
        {
            Guid handler=new("3981E225-F559-11D3-8E3A-00C04F6837D5"),targetId=typeof(IDropTarget).GUID;
            Marshal.ThrowExceptionForHR(item.BindToHandler(0,in handler,in targetId,out var pointer));
            try{return (IDropTarget)Marshal.GetObjectForIUnknown(pointer);}
            finally{Marshal.Release(pointer);}
        }
        finally{Marshal.ReleaseComObject(item);}
    }
    internal static void Check(VirtualFileDataObject data,string destination,IReadOnlyDictionary<string,byte[]?> expected)
    {
        var target=Open(destination);
        try
        {
            data.SetAsyncMode(false);
            uint effect=1;
            Marshal.ThrowExceptionForHR(target.DragEnter(data,1,new Point(),ref effect));
            if(effect!=1){target.DragLeave();throw new Exception("Windows Shell refused file data object on DragEnter");}
            effect=1;
            Marshal.ThrowExceptionForHR(target.Drop(data,0,new Point(),ref effect));
            Verify(destination,expected);
        }
        finally{Marshal.ReleaseComObject(target);}
    }
    internal static void Verify(string destination,IReadOnlyDictionary<string,byte[]?> expected)
    {
        foreach(var entry in expected)
        {
            string path=Path.Combine(destination,entry.Key);
            if(entry.Value==null){if(!Directory.Exists(path))throw new Exception("Windows Shell did not create expected directory");}
            else if(!File.Exists(path)||!File.ReadAllBytes(path).AsSpan().SequenceEqual(entry.Value))throw new Exception("Windows Shell did not write exact contents");
        }
        Console.WriteLine("PASS Windows Shell native folder drop writes all expected files and directories");
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Point{public int X,Y;}
    [ComImport,Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem
    {
        [PreserveSig]int BindToHandler(nint context,in Guid handler,in Guid iid,out nint result);
    }
    [ComImport,ComVisible(true),Guid("00000122-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDropTarget
    {
        [PreserveSig]int DragEnter([MarshalAs(UnmanagedType.Interface)]IDataObject data,uint keys,Point point,ref uint effect);
        [PreserveSig]int DragOver(uint keys,Point point,ref uint effect);
        [PreserveSig]int DragLeave();
        [PreserveSig]int Drop([MarshalAs(UnmanagedType.Interface)]IDataObject data,uint keys,Point point,ref uint effect);
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]static extern int SHCreateItemFromParsingName(string path,nint context,in Guid iid,[MarshalAs(UnmanagedType.Interface)]out IShellItem item);
}
