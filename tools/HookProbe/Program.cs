using System;
using System.Runtime.InteropServices;
using NekoBridge.Windows.Input;
using Forms=System.Windows.Forms;
internal static class Program
{
    [STAThread]static int Main()
    {
        NativeMethods.GetCursorPos(out var original);
        bool got=false;int delta=0;int phase=0;
        NativeMethods.HookProc callback=(code,message,data)=>
        {
            var mouse=Marshal.PtrToStructure<NativeMethods.MouseHook>(data);
            if(code>=0&&message==0x200&&phase==1&&mouse.ExtraInfo==NativeMethods.Marker)
            {
                NativeMethods.GetCursorPos(out var actual);delta=mouse.Position.Y-actual.Y;got=true;
                return 1;
            }
            return NativeMethods.CallNextHookEx(0,code,message,data);
        };
        nint hook=NativeMethods.SetWindowsHookEx(14,callback,NativeMethods.GetModuleHandle(null),0);
        if(hook==0)throw new Exception("Cannot install probe");
        using var timer=new Forms.Timer{Interval=100};
        timer.Tick+=(_,_)=>
        {
            if(phase==0){NativeMethods.MoveCursor(NativeMethods.GetSystemMetrics(0)/2,0);phase=1;return;}
            if(phase==1){NativeMethods.InjectMouse(0,-12,1);phase=2;return;}
            Forms.Application.ExitThread();
        };
        try{timer.Start();Forms.Application.Run();}
        finally{NativeMethods.UnhookWindowsHookEx(hook);NativeMethods.MoveCursor(original.X,original.Y);GC.KeepAlive(callback);}
        Console.WriteLine($"Top-edge pre-suppression delta Y={delta}; observed={got}");
        return got&&delta<0?0:1;
    }
}
