using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Forms = System.Windows.Forms;

namespace NekoBridge.Windows.Input;

internal readonly record struct PhysicalInput(string Kind,int X=0,int Y=0,int Data=0,int Flags=0,bool SourceInjected=false,nuint SourceExtraInfo=0);

internal sealed class InputHooks : IDisposable
{
    private readonly Func<PhysicalInput,bool> _handle;
    private readonly Action<string> _log;
    private readonly NativeMethods.HookProc _mouseCallback;
    private readonly NativeMethods.HookProc _keyCallback;
    private readonly Thread _thread;
    private Forms.Control? _dispatcher;
    private nint _mouse;
    private nint _keyboard;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;
    private readonly HashSet<int> _physicalKeys = new();

    internal InputHooks(Func<PhysicalInput,bool> handle,Action<string> log)
    {
        _handle=handle; _log=log; _mouseCallback=MouseCallback; _keyCallback=KeyCallback;
        _thread=new Thread(Run){ IsBackground=true,Name="NekoBridge input hooks" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }
    internal Task Ready => _ready.Task;
    private void Run()
    {
        try
        {
            _dispatcher=new Forms.Control();
            _ = _dispatcher.Handle;
            _mouse=NativeMethods.SetWindowsHookEx(14,_mouseCallback,NativeMethods.GetModuleHandle(null),0);
            _keyboard=NativeMethods.SetWindowsHookEx(13,_keyCallback,NativeMethods.GetModuleHandle(null),0);
            if(_mouse==0 || _keyboard==0) throw new Win32Exception(Marshal.GetLastWin32Error(),"无法注册键鼠钩子");
            _ready.TrySetResult();
            Forms.Application.Run();
        }
        catch(Exception ex){ _ready.TrySetException(ex); _log($"输入监听失败：{ex.Message}"); }
        finally
        {
            if(_mouse!=0) NativeMethods.UnhookWindowsHookEx(_mouse);
            if(_keyboard!=0) NativeMethods.UnhookWindowsHookEx(_keyboard);
            _dispatcher?.Dispose();
        }
    }
    internal static bool ShouldForward(nuint extraInfo)=>extraInfo!=NativeMethods.Marker;

    private nint MouseCallback(int code,nuint message,nint data)
    {
        if(code>=0)
        {
            var m=Marshal.PtrToStructure<NativeMethods.MouseHook>(data);
            if(ShouldForward(m.ExtraInfo))
            {
                var e=(uint)message switch
                {
                    0x200=>new PhysicalInput("move",m.Position.X,m.Position.Y),
                    0x201=>new PhysicalInput("button",Data:1,Flags:0),
                    0x202=>new PhysicalInput("button",Data:1,Flags:1),
                    0x204=>new PhysicalInput("button",Data:2,Flags:0),
                    0x205=>new PhysicalInput("button",Data:2,Flags:1),
                    0x207=>new PhysicalInput("button",Data:3,Flags:0),
                    0x208=>new PhysicalInput("button",Data:3,Flags:1),
                    0x20B=>new PhysicalInput("button",Data:((m.MouseData>>16)==1 ? 4:5),Flags:0),
                    0x20C=>new PhysicalInput("button",Data:((m.MouseData>>16)==1 ? 4:5),Flags:1),
                    0x20A=>new PhysicalInput("wheel",Data:unchecked((short)(m.MouseData>>16))),
                    0x20E=>new PhysicalInput("wheel",Data:unchecked((short)(m.MouseData>>16)),Flags:1),
                    _=>default
                };
                e=e with {SourceInjected=(m.Flags&1)!=0,SourceExtraInfo=m.ExtraInfo};
                try { if(e.Kind!=null && _handle(e)) return 1; }
                catch(Exception ex){ _log($"输入处理失败：{ex.Message}"); }
            }
        }
        return NativeMethods.CallNextHookEx(_mouse,code,message,data);
    }
    private nint KeyCallback(int code,nuint message,nint data)
    {
        if(code>=0)
        {
            var k=Marshal.PtrToStructure<NativeMethods.KeyHook>(data);
            if(ShouldForward(k.ExtraInfo))
            {
                int vk=(int)k.VirtualKey;
                bool up=(k.Flags&0x80)!=0;
                if(up) _physicalKeys.Remove(vk); else _physicalKeys.Add(vk);
                bool ctrl=_physicalKeys.Contains(0xA2)||_physicalKeys.Contains(0xA3)||_physicalKeys.Contains(0x11);
                bool alt=_physicalKeys.Contains(0xA4)||_physicalKeys.Contains(0xA5)||_physicalKeys.Contains(0x12);
                try
                {
                    if(!up && ctrl && alt && (vk==0x13 || vk==0x24))
                    {
                        _handle(new PhysicalInput("emergency",SourceInjected:(k.Flags&0x10)!=0,SourceExtraInfo:k.ExtraInfo));
                        return 1;
                    }
                    if(_handle(new PhysicalInput("key",X:vk,Y:(int)k.ScanCode,Flags:(up?1:0)|((k.Flags&1)!=0?2:0),SourceInjected:(k.Flags&0x10)!=0,SourceExtraInfo:k.ExtraInfo))) return 1;
                }
                catch(Exception ex){_log($"键盘处理失败：{ex.Message}");}
            }
        }
        return NativeMethods.CallNextHookEx(_keyboard,code,message,data);
    }
    public void Dispose()
    {
        if(_disposed)return; _disposed=true;
        var d=_dispatcher;
        if(d!=null && !d.IsDisposed) d.BeginInvoke(new Action(Forms.Application.ExitThread));
        if(Thread.CurrentThread!=_thread) _thread.Join(2000);
    }
}
