using System;
using System.Collections.Generic;
using System.Reflection;
using NekoBridge.Core;
using NekoBridge.Windows.Input;

internal static class HandoffInputSmoke
{
    // Exercise the actual hook handler, without synthesizing desktop movement.
    // The caller has completed its native operations before entering this probe.
    internal static void Check(InputRouter router,Guid local,Guid remote)
    {
        var type=typeof(InputRouter);
        lock(type.GetField("_gate",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(router)!)
        {
        var names=new[]{"_owner","_active","_dragStarting","_leftDown","_edgePending","_parkController","_x","_y","_targetWidth","_targetHeight","_move"};
        var saved=new Dictionary<FieldInfo,object?>();
        foreach(string name in names){var f=type.GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!;saved.Add(f,f.GetValue(router));}
        void Set(string name,object? value)=>type.GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(router,value);
        object? Get(string name)=>type.GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(router);
        var handler=type.GetMethod("HandlePhysical",BindingFlags.Instance|BindingFlags.NonPublic)!;
        try
        {
            Set("_owner",Guid.Empty);Set("_parkController",Guid.Empty);Set("_dragStarting",true);Set("_leftDown",true);Set("_edgePending",false);
            Set("_active",remote);Set("_targetWidth",1920);Set("_targetHeight",1080);Set("_x",300d);Set("_y",300d);
            if(!NativeMethods.GetCursorPos(out var cursor))throw new Exception("Cannot inspect cursor for handoff input test");
            var move=new object[]{new PhysicalInput("move",cursor.X+24,cursor.Y+12)};
            Set("_move",null);
            if(!(bool)handler.Invoke(router,move)!||(double)Get("_x")! != 300||Get("_move")!=null)
                throw new Exception("Remote startup admitted movement before OLE captured the helper press");
            router.DragStarted();
            handler.Invoke(router,move);
            double expectedX=300+InputRouter.NormalizePointerDelta(24,router.Width,1920);
            if(Math.Abs((double)Get("_x")!-expectedX)>0.001||Get("_move")==null)throw new Exception("Remote movement did not resume after OLE startup");
            Set("_dragStarting",true);Set("_active",local);
            bool blocked=(bool)handler.Invoke(router,new object[]{new PhysicalInput("move",router.Left+router.Width/2,router.Top+router.Height/2)})!;
            if(!blocked)throw new Exception("Local startup let pre-handoff cursor movement reach Windows");
            router.DragStarted();
            blocked=(bool)handler.Invoke(router,new object[]{new PhysicalInput("move",router.Left+router.Width/2,router.Top+router.Height/2)})!;
            if(blocked)throw new Exception("Local movement did not resume after OLE startup");
            Console.WriteLine("PASS local and remote handoffs hold pointer at entry until startup acknowledgement, then resume movement; no desktop input injected");
        }
        finally{foreach(var entry in saved)entry.Key.SetValue(router,entry.Value);}
        }
    }
}
