using System;
using NekoBridge.Windows.Input;

// Pure arithmetic only: never construct InputRouter/InputHooks, inject input,
// register hooks, open a window, or move the real desktop cursor.
static void Equal(double actual,double expected,string name)
{
    if(Math.Abs(actual-expected)>1e-9)throw new Exception($"{name}: {actual} != {expected}");
}

Equal(InputRouter.NormalizePointerDelta(120,3840,2560),80,"4K to 2K horizontal");
Equal(InputRouter.NormalizePointerDelta(-120,2160,1440),-80,"4K to 2K vertical");
Equal(InputRouter.NormalizePointerDelta(80,2560,3840),120,"2K to 4K horizontal");
Equal(InputRouter.NormalizePointerDelta(80,1440,2160),120,"2K to 4K vertical");
Equal(InputRouter.NormalizePointerDelta(120,3840,1920),60,"different aspect ratio X");
Equal(InputRouter.NormalizePointerDelta(120,2160,1200),120.0/2160*1200,"different aspect ratio Y");
Equal(InputRouter.NormalizePointerDelta(-45,1920,1920),-45,"same screen pixels");
Equal(InputRouter.NormalizePointerDelta(0,3840,2560),0,"stationary");
double position=0;
for(int i=0;i<300;i++)position+=InputRouter.NormalizePointerDelta(1,3840,2560);
Equal(position,200,"small deltas retain fractions");
// MWB default absolute packet: virtual source position / source extent,
// normalized to 65535 and mapped by the destination. Ignore packet integer
// quantization; the router retains fractions until emitting destination pixels.
foreach(var (source,target) in new[]{(3840,2560),(2160,1440),(2560,3840),(1440,2160),(3840,1920)})
{
    double sourcePosition=source*0.4;
    double targetPosition=sourcePosition/source*target;
    foreach(int delta in new[]{1,-3,20,240,-100,0})
    {
        sourcePosition+=delta;
        targetPosition+=InputRouter.NormalizePointerDelta(delta,source,target);
        Equal(targetPosition,(sourcePosition*65535/source)/65535*target,"MWB absolute-mode equivalence");
    }
}
if(!InputHooks.ShouldForward(0)||!InputHooks.ShouldForward(0x12345678))throw new Exception("Driver-injected events rejected");
if(InputHooks.ShouldForward(NativeMethods.Marker))throw new Exception("Own injected events must not loop");
foreach(string kind in new[]{"move","button","key","emergency"})
{
    var replay=new PhysicalInput(kind,SourceInjected:true,SourceExtraInfo:0x12345678);
    if(!InputRouter.IsReceiverReplay(replay,true))throw new Exception($"Receiver replay can take ownership: {kind}");
    if(InputRouter.IsReceiverReplay(replay,false))throw new Exception($"Controller driver input rejected: {kind}");
    if(InputRouter.IsReceiverReplay(new PhysicalInput(kind),true))throw new Exception($"Physical local takeover rejected: {kind}");
}
Console.WriteLine("PASS receiver software replay preserves remote ownership; controller injections and real local takeover retained");
Console.WriteLine("PASS driver injections accepted; NekoBridge self-injection excluded");
Console.WriteLine("InputSmoke passed: MWB default absolute mapping, axis independence, bidirectional resolutions, fractional accumulation. No physical input exercised.");
