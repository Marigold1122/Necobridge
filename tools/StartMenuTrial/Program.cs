using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using NekoBridge.Windows.Input;

internal static class Program
{
    [DllImport("user32.dll")]static extern nint GetDesktopWindow();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern nint FindWindowEx(nint parent,nint after,string? cls,string? title);
    [DllImport("user32.dll")]static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")]static extern nint SendMessage(nint h,uint msg,nint wp,nint lp);
    [DllImport("user32.dll")]static extern nint SetThreadDpiAwarenessContext(nint c);
    [DllImport("user32.dll")]static extern nint WindowFromPoint(NativeMethods.Point p);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetWindowText(nint h,System.Text.StringBuilder s,int count);
    [STAThread]static int Main(string[] args)
    {
        if(args.Length!=1)return 1;
        SetThreadDpiAwarenessContext(-4);Directory.CreateDirectory(args[0]);NativeMethods.GetCursorPos(out var original);
        using var parking=new CursorParking(Console.WriteLine);
        try
        {
            nint menuHandle=0,after=0;
            for(int i=0;i<1500;i++)
            {
                after=FindWindowEx(GetDesktopWindow(),after,null,null);if(after==0)break;
                NativeMethods.GetWindowThreadProcessId(after,out var pid);
                if(System.Diagnostics.Process.GetProcessById((int)pid).ProcessName=="StartMenuExperienceHost"&&IsWindowVisible(after)){menuHandle=after;break;}
            }
            if(menuHandle==0)throw new Exception("Start menu not currently open");
            var menu=AutomationElement.FromHandle(menuHandle);Console.WriteLine($"Menu=0x{menuHandle:X} bounds={menu.Current.BoundingRectangle}");
            var tile=menu.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.NameProperty,"Visual Studio Code"));
            if(tile==null)throw new Exception("Known VS Code tile not found; no movement performed");
            var r=tile.Current.BoundingRectangle;if(r.IsEmpty||r.Width<20)throw new Exception("Tile has no bounds");
            // Only cancel the product's own hider capture, never Shell's capture.
            var hit=WindowFromPoint(original);var caption=new System.Text.StringBuilder(128);GetWindowText(hit,caption,128);
            if(caption.ToString()=="NekoBridge cursor parking"){SendMessage(hit,0x1F,0,0);Console.WriteLine("Cancelled own parking capture for isolated test");}
            int x=(int)(r.X+r.Width/2),y=(int)(r.Y+r.Height/2);var screen=System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            void Shot(string name)
            {
                NativeMethods.GetCursorPos(out var p);Console.WriteLine($"{name}: pointer={p.X},{p.Y} hit=0x{WindowFromPoint(p):X} focused={tile.Current.HasKeyboardFocus}");
                using var b=new System.Drawing.Bitmap(screen.Width,screen.Height);using var g=System.Drawing.Graphics.FromImage(b);g.CopyFromScreen(screen.Left,screen.Top,0,0,b.Size);b.Save(Path.Combine(args[0],name+".png"));
            }
            NativeMethods.MoveCursor(x,y);Thread.Sleep(900);Shot("01-tile-hover");
            NativeMethods.MoveCursor(screen.Left+screen.Width/2,screen.Top+4);Thread.Sleep(900);Shot("02-top-move-only");
            NativeMethods.MoveCursor(x,y);Thread.Sleep(500);
            int px=screen.Left+screen.Width/2,py=screen.Top+4;parking.Show(px,py);NativeMethods.MoveCursor(px,py);Thread.Sleep(900);Shot("03-current-parking");
            parking.Hide();Thread.Sleep(200);
            NativeMethods.MoveCursor(x,y);Thread.Sleep(500);
            var bounds=menu.Current.BoundingRectangle;int outsideY=(int)Math.Ceiling(bounds.Bottom)+4;
            if(outsideY>=screen.Bottom)throw new Exception("No on-screen point below Start menu; skip outside test");
            NativeMethods.MoveCursor(px,outsideY);Thread.Sleep(1200);Shot("04-outside-menu");
            // Return to the same top parking point after a genuine departure.
            parking.Show(px,py);NativeMethods.MoveCursor(px,py);Thread.Sleep(900);Shot("05-outside-then-parking");
            parking.Hide();Thread.Sleep(200);
            NativeMethods.MoveCursor(screen.Left+20,screen.Top+500);Thread.Sleep(1800);Shot("06-sidebar-hover");
            parking.Show(px,py);NativeMethods.MoveCursor(px,py);Thread.Sleep(1800);Shot("07-sidebar-parking");
            parking.Hide();Thread.Sleep(200);NativeMethods.MoveCursor(px,outsideY);Thread.Sleep(1800);Shot("08-sidebar-real-exit");
            parking.Show(px,py);NativeMethods.MoveCursor(px,py);Thread.Sleep(1800);Shot("09-sidebar-exit-then-parking");
            parking.Hide();Thread.Sleep(200);NativeMethods.MoveCursor(screen.Left+20,screen.Top+500);Thread.Sleep(1800);Shot("10-adaptive-before");
            var adaptive=CursorParking.ParkingPoint(screen);Console.WriteLine($"Adaptive parking={adaptive.X},{adaptive.Y}");
            parking.Show(adaptive.X,adaptive.Y);NativeMethods.MoveCursor(adaptive.X,adaptive.Y);Thread.Sleep(1800);Shot("11-adaptive-after");
            parking.Hide();Thread.Sleep(200);NativeMethods.MoveCursor(screen.Left+20,screen.Top+500);Thread.Sleep(1800);Shot("12-ordered-before");
            NativeMethods.MoveCursor(adaptive.X,adaptive.Y);Thread.Sleep(120);parking.Show(adaptive.X,adaptive.Y);Thread.Sleep(1800);Shot("13-ordered-after");
            parking.Hide();Thread.Sleep(200);NativeMethods.MoveCursor(x,y);Thread.Sleep(800);Shot("14-tile-adaptive-before");
            adaptive=CursorParking.ParkingPoint(screen);parking.Show(adaptive.X,adaptive.Y);NativeMethods.MoveCursor(adaptive.X,adaptive.Y);Thread.Sleep(1800);Shot("15-tile-adaptive-after");
            return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
        finally{parking.Hide();Thread.Sleep(200);NativeMethods.MoveCursor(original.X,original.Y);}
    }
}
