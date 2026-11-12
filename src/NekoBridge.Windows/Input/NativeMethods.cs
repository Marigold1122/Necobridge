using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NekoBridge.Windows.Input;

internal static class NativeMethods
{
    internal static readonly nuint Marker = 0x4E454B4F;
    internal delegate nint HookProc(int code, nuint message, nint data);
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseHook { public Point Position; public uint MouseData; public uint Flags; public uint Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyHook { public uint VirtualKey; public uint ScanCode; public uint Flags; public uint Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput { public int Dx; public int Dy; public uint MouseData; public uint Flags; public uint Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput { public ushort VirtualKey; public ushort ScanCode; public uint Flags; public uint Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint Type; public InputUnion Union; }
    [DllImport("user32.dll", SetLastError=true)] internal static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWindowsHookEx(nint handle);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint handle,int code,nuint message,nint data);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", SetLastError=true)] internal static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window,out uint processId);
    [DllImport("user32.dll")] internal static extern nint SetCapture(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool ReleaseCapture();

    internal static void MoveCursor(int x,int y)
    {
        int left=GetSystemMetrics(76),top=GetSystemMetrics(77);
        int width=Math.Max(2,GetSystemMetrics(78)),height=Math.Max(2,GetSystemMetrics(79));
        InjectMouse((int)((long)(x-left)*65535/(width-1)),(int)((long)(y-top)*65535/(height-1)),0xC001);
    }
    internal static void InjectMouse(int x,int y,uint flags,uint data=0)
    {
        var input = new Input { Type=0, Union = new InputUnion { Mouse = new MouseInput { Dx=x, Dy=y, Flags=flags, MouseData=data, ExtraInfo=Marker } } };
        Send(input);
    }
    internal static void InjectKey(int vk,int scan,bool up,bool extended)
    {
        uint flags = (up ? 2u : 0) | (extended ? 1u : 0);
        var input = new Input { Type=1, Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey=(ushort)vk, ScanCode=(ushort)scan, Flags=flags, ExtraInfo=Marker } } };
        Send(input);
    }
    private static void Send(Input input)
    {
        if(SendInput(1,new[]{input},Marshal.SizeOf<Input>())!=1)
            throw new Win32Exception(Marshal.GetLastWin32Error(),"系统拒绝注入输入。请检查窗口权限；不支持 UAC 安全桌面。");
    }
}
