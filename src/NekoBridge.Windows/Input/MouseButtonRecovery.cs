using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace NekoBridge.Windows.Input;

// Only releases are used. Pointer motion remains exclusively the LL hook pixel path.
internal sealed class MouseButtonRecovery : Forms.NativeWindow,IDisposable
{
    private readonly Action<int> _release;
    private readonly Action<string> _log;
    internal MouseButtonRecovery(Action<int> release,Action<string> log)
    {
        _release=release;_log=log;
        CreateHandle(new Forms.CreateParams{Caption="NekoBridge button recovery",Parent=new nint(-3)});
        var devices=new[]{new Device{Page=1,Usage=2,Flags=0x100,Target=Handle}};
        if(!RegisterRawInputDevices(devices,1,(uint)Marshal.SizeOf<Device>()))
        {DestroyHandle();throw new Win32Exception(Marshal.GetLastWin32Error(),"无法注册鼠标松键恢复");}
    }
    protected override void WndProc(ref Forms.Message m)
    {
        if(m.Msg==0xFF)
        {
            try
            {
                uint size=0,header=(uint)Marshal.SizeOf<Header>();
                if(GetRawInputData(m.LParam,0x10000003,0,ref size,header)==uint.MaxValue)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if(size>=header+24&&size<=4096)
                {
                    nint buffer=Marshal.AllocHGlobal((int)size);
                    try
                    {
                        if(GetRawInputData(m.LParam,0x10000003,buffer,ref size,header)==uint.MaxValue)
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                        var h=Marshal.PtrToStructure<Header>(buffer);
                        if(h.Type==0&&h.Device!=0)
                        {
                            ushort flags=unchecked((ushort)Marshal.ReadInt16(buffer,(int)header+4));
                            for(int button=1;button<=5;button++)
                                if((flags&(1<<(button*2-1)))!=0)_release(button);
                        }
                    }
                    finally{Marshal.FreeHGlobal(buffer);}
                }
            }
            catch(Exception ex){_log($"鼠标松键恢复失败：{ex.Message}");}
        }
        base.WndProc(ref m);
    }
    public void Dispose()
    {
        var devices=new[]{new Device{Page=1,Usage=2,Flags=1}};
        if(!RegisterRawInputDevices(devices,1,(uint)Marshal.SizeOf<Device>()))_log("注销鼠标松键恢复失败。");
        DestroyHandle();
    }
    [StructLayout(LayoutKind.Sequential)] private struct Device{public ushort Page,Usage;public uint Flags;public nint Target;}
    [StructLayout(LayoutKind.Sequential)] private struct Header{public uint Type,Size;public nint Device;public nuint Param;}
    [DllImport("user32.dll",SetLastError=true)] private static extern bool RegisterRawInputDevices(Device[] devices,uint count,uint size);
    [DllImport("user32.dll",SetLastError=true)] private static extern uint GetRawInputData(nint input,uint command,nint data,ref uint size,uint headerSize);
}
