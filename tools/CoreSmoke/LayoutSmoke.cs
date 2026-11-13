using NekoBridge.Core;
using System.Text.Json;

internal static class LayoutSmoke
{
    internal static void Run()
    {
        Guid a=Guid.Parse("00000000-0000-0000-0000-000000000001"),b=Guid.Parse("00000000-0000-0000-0000-000000000002"),c=Guid.NewGuid(),d=Guid.NewGuid();
        var legacy=new AppSettings{Id=a,Layout=[b,a,c]};
        var migrated=LayoutSync.Create(legacy,legacy.Layout);
        Require(LayoutSync.Positions(migrated).SequenceEqual(new[]{new LayoutCell(b,0,0),new LayoutCell(a,1,0),new LayoutCell(c,2,0)}),"legacy horizontal migration");
        int saves=0;LayoutSync.Apply(legacy,migrated,()=>saves++);
        var grid=new[]{new LayoutCell(a,0,0),new LayoutCell(b,1,0),new LayoutCell(c,0,1),new LayoutCell(d,1,1)};
        var snapshot=LayoutSync.CreateCells(legacy,grid);
        Require(LayoutSync.Apply(legacy,snapshot,()=>saves++),"2D apply");
        Require(!LayoutSync.Apply(legacy,snapshot,()=>saves++),"duplicate ignored");Require(saves==2,"duplicate not persisted");
        Require(LayoutSync.Neighbor(grid,a,LayoutSide.Right)==b&&LayoutSync.Neighbor(grid,a,LayoutSide.Down)==c,"A right/down");
        Require(LayoutSync.Neighbor(grid,d,LayoutSide.Left)==c&&LayoutSync.Neighbor(grid,d,LayoutSide.Up)==b,"D left/up");
        Require(LayoutSync.Neighbor(grid,b,LayoutSide.Right)==Guid.Empty&&LayoutSync.Neighbor(grid,a,LayoutSide.Up)==Guid.Empty,"no wrapping");
        // An offline cell remains stored: never filter/repack before finding adjacency.
        var gap=new[]{new LayoutCell(a,0,0),new LayoutCell(b,1,0),new LayoutCell(c,2,0)};
        Require(LayoutSync.Neighbor(gap,a,LayoutSide.Right)==b,"offline slot not skipped to C");
        var swapped=LayoutSync.Move(grid,b,0,1);
        Require(swapped.Single(x=>x.Id==b)==new LayoutCell(b,0,1)&&swapped.Single(x=>x.Id==c)==new LayoutCell(c,1,0),"occupied slot swap");
        var moved=LayoutSync.Move(grid,d,-1,2);Require(LayoutSync.ValidCells(moved)&&moved.Single(x=>x.Id==d).Column==-1,"empty slot move");
        var nodeB=new AppSettings{Id=b,Layout=[a,b,c,d]};var nodeC=new AppSettings{Id=c,Layout=[a,b,c,d]};
        Require(LayoutSync.Apply(nodeB,snapshot,()=>{})&&LayoutSync.Apply(nodeC,snapshot,()=>{}),"relay to peers");
        var concurrent=LayoutSync.CreateCells(nodeB,swapped) with{Revision=snapshot.Revision,Editor=b};
        Require(LayoutSync.Apply(legacy,concurrent,()=>{}),"editor conflict winner");
        Require(!LayoutSync.Apply(legacy,snapshot,()=>{}),"stale replay ignored");
        Require(LayoutSync.Apply(nodeC,concurrent,()=>{})&&LayoutSync.SamePositions(LayoutSync.Read(nodeC),concurrent),"reconnect converges");
        var next=LayoutSync.CreateCells(legacy,moved);var before=LayoutSync.Read(legacy);
        try{LayoutSync.Apply(legacy,next,()=>throw new IOException("expected"));throw new Exception("missing persistence failure");}catch(IOException){}
        Require(LayoutSync.SamePositions(LayoutSync.Read(legacy),before)&&legacy.LayoutRevision==before.Revision,"persist rollback");
        Require(LayoutSync.TryDecode(LayoutSync.Encode(next),out var decoded)&&LayoutSync.SamePositions(next,decoded),"2D codec");
        Require(!LayoutSync.TryDecode(new(){Kind="layout",Payload=JsonSerializer.Serialize(next)},out _),"old protocol not accepted as 2D");
        Require(!LayoutSync.IsValid(next with{Cells=[new(a,0,0),new(b,0,0)]}),"overlap rejected");
        Require(!LayoutSync.IsValid(next with{Cells=[new(a,2000,0)]}),"coordinate bound");
        Require(!LayoutSync.TryDecode(new(){Kind="layout2",Payload="{}"},out _),"invalid payload");
        var persisted=JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(legacy))!;
        Require(LayoutSync.SamePositions(LayoutSync.Read(persisted),LayoutSync.Read(legacy)),"JSON settings retain 2D positions");
        Require(!LayoutSync.ValidCells([new(a,0,0),new(a,1,0)]),"duplicate device rejected");
        Require(legacy.Peers.Count==0,"positions never confer trust");
        foreach(var side in new[]{LayoutSide.Left,LayoutSide.Right,LayoutSide.Up,LayoutSide.Down})
        {
            var entry=LayoutSync.Entry(side,1919.5,1079.5,3840,2160,1920,1080);
            Require(entry.X>=0&&entry.X<1920&&entry.Y>=0&&entry.Y<1080,"entry bounds "+side);
            if(side is LayoutSide.Up or LayoutSide.Down)Require(entry.X==960,"vertical entry preserves X ratio");
            else Require(entry.Y==540,"horizontal entry preserves Y ratio");
        }
        Require(LayoutSync.Entry(LayoutSide.Down,0,0,1,1,1,1)==(0,0),"one-pixel dimensions");
        Console.WriteLine("PASS 2D migration, four directions, holes, swap, bounds, relay, reconnect, conflict, persistence rollback and proportional entry");
    }
    private static void Require(bool condition,string name){if(!condition)throw new Exception("Layout smoke: "+name);}
}
