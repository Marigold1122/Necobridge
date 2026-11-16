using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using NekoBridge.Windows.Input;

internal static class Program
{
    [STAThread] static int Main()
    {
        try
        {
            var info=new GuiInfo{Size=(uint)Marshal.SizeOf<GuiInfo>()};
            if(!GetGUIThreadInfo(0,ref info)||info.Capture!=0)
                throw new Exception("Skip capture smoke while a foreground app owns capture, or its state cannot be inspected.");
            var foreground=NativeMethods.GetForegroundWindow();
            using var shield=new Target();using var other=new Target();
            try
            {
                CursorParking.AcquireParkingCapture(shield.Handle);
                if(GetCapture()!=shield.Handle)throw new Exception("Shield capture missing");
                CursorParking.ReleaseParkingCapture(shield.Handle);
                if(GetCapture()!=0)throw new Exception("Shield capture retained on release");
                CursorParking.AcquireParkingCapture(other.Handle);
                CursorParking.ReleaseParkingCapture(shield.Handle);
                if(GetCapture()!=other.Handle)throw new Exception("Released unrelated window capture");
                CursorParking.ReleaseParkingCapture(other.Handle);
                if(NativeMethods.GetForegroundWindow()!=foreground)throw new Exception("Foreground changed during smoke");
                Console.WriteLine("PASS parking capture acquired/released, unrelated capture retained, foreground unchanged.");
                Console.WriteLine("No hooks, cursor movement, click, shell message or focus change requested; real Start menu behavior not exercised.");
            }
            finally
            {
                CursorParking.ReleaseParkingCapture(shield.Handle);
                CursorParking.ReleaseParkingCapture(other.Handle);
            }
            return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
    private sealed class Target:NativeWindow,IDisposable
    {
        internal Target(){CreateHandle(new CreateParams{Caption="NekoBridge capture smoke",X=-30000,Y=-30000,Width=9,Height=9,Style=unchecked((int)0x80000000),ExStyle=0x08000080});}
        public void Dispose(){DestroyHandle();}
    }
    [StructLayout(LayoutKind.Sequential)] private struct GuiInfo
    {
        public uint Size,Flags;
        public nint Active,Focus,Capture,MenuOwner,MoveSize,Caret;
        public int Left,Top,Right,Bottom;
    }
    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread,ref GuiInfo info);
}
