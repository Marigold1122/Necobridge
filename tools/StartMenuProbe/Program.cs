using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using NekoBridge.Windows.Input;

internal static class Program
{
    [DllImport("user32.dll")]private static extern nint WindowFromPoint(NativeMethods.Point p);
    [STAThread] static int Main(string[] args)
    {
        if(args.Length!=2||args[0]!="--watch")
        {
            Console.Error.WriteLine("Usage: StartMenuProbe --watch OUTPUT_DIRECTORY. Passive observation only.");return 1;
        }
        return Watch(args[1]);
    }
    static int Watch(string folder)
    {
        Directory.CreateDirectory(folder);
        using var log=new StreamWriter(Path.Combine(folder,"observations.txt")){AutoFlush=true};
        Console.WriteLine("READY: watching for 5 minutes. Open Start and leave it open for at least 3 seconds before crossing. Screenshots wait until its bounds have been stable for 2 seconds. Passive observation only.");
        var end=DateTime.UtcNow.AddMinutes(5);string previous="",lastBounds="";AutomationElement? menu=null;bool seen=false;int screenshot=0;
        var stableSince=DateTime.UtcNow;var lastShot=DateTime.MinValue;
        while(DateTime.UtcNow<end)
        {
            try
            {
                if(menu==null)
                {
                    var candidates=AutomationElement.RootElement.FindAll(TreeScope.Children,Condition.TrueCondition);
                    foreach(AutomationElement candidate in candidates)
                    {
                        var current=candidate.Current;var bounds=current.BoundingRectangle;
                        if(current.IsOffscreen||bounds.IsEmpty||bounds.Width<200||bounds.Height<200)continue;
                        var process=System.Diagnostics.Process.GetProcessById(current.ProcessId);
                        bool shell=process.ProcessName is "StartMenuExperienceHost" or "ShellExperienceHost";
                        bool explorerMenu=process.ProcessName=="explorer"&&current.ClassName is "Windows.UI.Core.CoreWindow" or "XamlExplorerHostIslandWindow" or "DV2ControlHost";
                        if(shell||explorerMenu){menu=candidate;lastBounds="";stableSince=DateTime.UtcNow;Console.WriteLine($"Detected candidate: {process.ProcessName} / {current.ClassName} / {bounds}");break;}
                    }
                }
                if(menu!=null&&!menu.Current.IsOffscreen)
                {
                    var bounds=menu.Current.BoundingRectangle.ToString();
                    if(bounds!=lastBounds){lastBounds=bounds;stableSince=DateTime.UtcNow;}
                    seen=true;GetPhysicalCursorPos(out var p);var hit=WindowFromPoint(p);
                    var focused=AutomationElement.FocusedElement;string focus="none";
                    if(focused!=null&&focused.Current.ProcessId==menu.Current.ProcessId)focus=focused.Current.ControlType.ProgrammaticName+" "+focused.Current.BoundingRectangle;
                    var selections=menu.FindAll(TreeScope.Descendants,new PropertyCondition(SelectionItemPattern.IsSelectedProperty,true));
                    string selected="";foreach(AutomationElement e in selections)selected+=e.Current.ControlType.ProgrammaticName+" "+e.Current.BoundingRectangle+";";
                    var state=$"cursor={p.X},{p.Y} hit=0x{hit:X} menu={menu.Current.BoundingRectangle} focus={focus} selected={selected}";
                    if(state!=previous)
                    {
                        string line=DateTime.UtcNow.ToString("O")+" "+state;Console.WriteLine(line);log.WriteLine(line);previous=state;
                    }
                    // Wait for the opening animation, then retain a periodic
                    // sample even if pointer/focus state stops changing.
                    var r=menu.Current.BoundingRectangle;
                    if(screenshot<90&&(DateTime.UtcNow-stableSince).TotalSeconds>=2&&(DateTime.UtcNow-lastShot).TotalSeconds>=1&&!r.IsEmpty&&r.Width>0&&r.Height>0)
                    {
                        using var image=new System.Drawing.Bitmap((int)r.Width,(int)r.Height);using var g=System.Drawing.Graphics.FromImage(image);g.CopyFromScreen((int)r.X,(int)r.Y,0,0,image.Size);image.Save(Path.Combine(folder,$"menu-{screenshot++:00}.png"));lastShot=DateTime.UtcNow;
                        log.WriteLine($"{lastShot:O} screenshot=menu-{screenshot-1:00}.png {state}");
                    }
                }
                else menu=null;
            }
            catch(ElementNotAvailableException){menu=null;}
            catch(Exception ex){log.WriteLine(ex.GetType().Name+": "+ex.Message);}
            Thread.Sleep(250);
        }
        Console.WriteLine(seen?"Observation completed; inspect saved states and images. No fix inferred automatically.":"BLOCKED: no visible Start menu observed; no reproduction and no success claim.");return seen?0:2;
    }
    [DllImport("user32.dll")]private static extern bool GetPhysicalCursorPos(out NativeMethods.Point p);
}
