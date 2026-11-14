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

foreach(string kind in new[]{"move","key","wheel","button"})
{
    if(!InputRouter.IsLocalTakeover(new PhysicalInput(kind),true))throw new Exception("Physical takeover missing: "+kind);
    if(InputRouter.IsLocalTakeover(new PhysicalInput(kind,SourceInjected:true),true))throw new Exception("Software replay stole ownership");
    if(InputRouter.IsLocalTakeover(new PhysicalInput(kind),false))throw new Exception("No owner to take over");
}
if(InputRouter.IsLocalTakeover(new PhysicalInput("button",Flags:1),true))throw new Exception("Release-only event stole ownership");
foreach(bool local in new[]{true,false})
{
    var points=new[]{(-1d,50d,NekoBridge.Core.LayoutSide.Left),(101d,50d,NekoBridge.Core.LayoutSide.Right),(50d,-1d,NekoBridge.Core.LayoutSide.Up),(50d,101d,NekoBridge.Core.LayoutSide.Down)};
    foreach(var (x,y,side) in points)
        if(InputRouter.DetectSide(x,y,100,100,local)!=side)throw new Exception("Wrong edge: "+side);
    if(InputRouter.DetectSide(50,50,100,100,local)!=NekoBridge.Core.LayoutSide.None)throw new Exception("Interior crossed");
}
Console.WriteLine("PASS local and remote four-direction edge detection");
Console.WriteLine("PASS local raw UP not replayed, remote release recovery retained, physical move/click/wheel takeover");
Console.WriteLine("PASS receiver software replay preserves remote ownership; controller injections and real local takeover retained");
Console.WriteLine("PASS driver injections accepted; NekoBridge self-injection excluded");
Console.WriteLine("InputSmoke passed: MWB default absolute mapping, axis independence, bidirectional resolutions, fractional accumulation. No physical input exercised.");

foreach(int dx in new[]{-1,0,1})
foreach(int dy in new[]{-1,0,1})
    if(!CursorParking.ContainsParkedPoint(100,4,100+dx,4+dy))throw new Exception("Rounded park point escaped shield");
if(CursorParking.ContainsParkedPoint(100,4,120,4))throw new Exception("Real movement mistaken for parked point");
if(!InputRouter.IsParkedReplay(new PhysicalInput("move",SourceInjected:true),true))throw new Exception("Parked receiver accepts software move");
if(InputRouter.IsParkedReplay(new PhysicalInput("move"),true))throw new Exception("Parked receiver blocks real local mouse");
if(InputRouter.IsParkedReplay(new PhysicalInput("move",SourceInjected:true),false))throw new Exception("Active receiver blocks software replay");
Console.WriteLine("PASS centered parking shield rounding and parked receiver replay isolation; physical takeover retained");

// Same state transition used by local Raw UP and ordinary LL button events.
foreach(bool clickLeftAfterWheel in new[]{false,true})
{
    var held=new System.Collections.Generic.HashSet<int>();
    InputRouter.RecordButton(held,2,false);
    InputRouter.RecordButton(held,2,true); // hardware Raw UP; ordinary UP consumed
    if(clickLeftAfterWheel){InputRouter.RecordButton(held,1,false);InputRouter.RecordButton(held,1,true);}
    for(int crossing=0;crossing<4;crossing++)
        if(held.Contains(2))throw new Exception("Released source right button replayed across screens");
}
var pressed=new System.Collections.Generic.HashSet<int>();
InputRouter.RecordButton(pressed,2,false);
if(!pressed.Contains(2))throw new Exception("Real held right button lost");
if(!InputRouter.RecordButton(pressed,2,true)||InputRouter.RecordButton(pressed,2,true))throw new Exception("Raw/LL release deduplication failed");
Console.WriteLine("PASS wheel consumed UP, with/without later left click, held right and Raw/LL release deduplication");
