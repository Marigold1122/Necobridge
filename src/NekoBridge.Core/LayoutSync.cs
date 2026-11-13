using System.Text.Json;

namespace NekoBridge.Core;

public sealed record LayoutCell(Guid Id, int Column, int Row);
public enum LayoutSide { None, Left, Right, Up, Down }
public sealed record LayoutSnapshot(long Revision, Guid Editor, Guid[] DeviceIds, LayoutCell[]? Cells = null);

public static class LayoutSync
{
    public const int MaxDevices = 64;
    public const int MaxPayloadLength = 16384;
    public const string MessageKind = "layout2";
    public static LayoutCell[] Positions(LayoutSnapshot snapshot) => snapshot.Cells ?? snapshot.DeviceIds.Select((id,i)=>new LayoutCell(id,i,0)).ToArray();
    public static bool ValidCells(IReadOnlyList<LayoutCell>? cells) => cells is { Count: > 0 and <= MaxDevices }
        && cells.All(c=>c is not null && c.Id!=Guid.Empty && c.Column is >= -1024 and <= 1024 && c.Row is >= -1024 and <= 1024)
        && cells.Select(c=>c.Id).Distinct().Count()==cells.Count
        && cells.Select(c=>(c.Column,c.Row)).Distinct().Count()==cells.Count;
    public static bool IsValid(LayoutSnapshot? snapshot) => snapshot is { Revision: > 0, DeviceIds: not null }
        && snapshot.Editor!=Guid.Empty && snapshot.DeviceIds.Length is > 0 and <= MaxDevices
        && snapshot.DeviceIds.All(id=>id!=Guid.Empty) && snapshot.DeviceIds.Distinct().Count()==snapshot.DeviceIds.Length
        && (snapshot.Cells==null || ValidCells(snapshot.Cells) && snapshot.DeviceIds.Order().SequenceEqual(snapshot.Cells.Select(c=>c.Id).Order()));
    public static LayoutSnapshot Read(AppSettings settings) => new(settings.LayoutRevision,settings.LayoutEditor,settings.Layout.ToArray(),
        settings.LayoutCells.Count==0?null:settings.LayoutCells.ToArray());
    public static int Compare(LayoutSnapshot left,LayoutSnapshot right)
    {
        int revision=left.Revision.CompareTo(right.Revision);
        return revision!=0?revision:left.Editor.CompareTo(right.Editor);
    }
    public static LayoutSnapshot Create(AppSettings settings,IEnumerable<Guid> ids)
    {
        var order=ids.ToArray();var previous=Positions(Read(settings));
        var cells=order.Where(id=>previous.Any(c=>c.Id==id)).Select(id=>previous.First(c=>c.Id==id)).ToList();
        foreach(var id in order.Where(id=>!cells.Any(c=>c.Id==id)))
        {
            int column=cells.Count==0?0:cells.Max(c=>c.Column)+1;
            cells.Add(new(id,column,0));
        }
        return CreateCells(settings,cells);
    }
    public static LayoutSnapshot CreateCells(AppSettings settings,IEnumerable<LayoutCell> cells)
    {
        var positions=cells.OrderBy(c=>c.Row).ThenBy(c=>c.Column).ToArray();
        var snapshot=new LayoutSnapshot(checked(Math.Max(0,settings.LayoutRevision)+1),settings.Id,positions.Select(c=>c.Id).ToArray(),positions);
        if(!IsValid(snapshot))throw new InvalidDataException("屏幕位置无效、重复或超过范围。");
        return snapshot;
    }
    public static bool SamePositions(LayoutSnapshot a,LayoutSnapshot b)=>Positions(a).OrderBy(c=>c.Id).SequenceEqual(Positions(b).OrderBy(c=>c.Id));
    public static bool Apply(AppSettings settings,LayoutSnapshot snapshot,Action persist)
    {
        if(!IsValid(snapshot))throw new InvalidDataException("共享屏幕排列无效。");
        var previous=Read(settings);
        if(Compare(snapshot,previous)<=0)return false;
        settings.Layout=snapshot.DeviceIds.ToList();settings.LayoutCells=Positions(snapshot).ToList();
        settings.LayoutRevision=snapshot.Revision;settings.LayoutEditor=snapshot.Editor;
        try{persist();}
        catch{settings.Layout=previous.DeviceIds.ToList();settings.LayoutCells=previous.Cells?.ToList()??[];settings.LayoutRevision=previous.Revision;settings.LayoutEditor=previous.Editor;throw;}
        return true;
    }
    public static LayoutCell[] Move(IReadOnlyList<LayoutCell> cells,Guid id,int column,int row)
    {
        if(!ValidCells(cells)||column is < -1024 or > 1024||row is < -1024 or > 1024)throw new ArgumentException("屏幕位置超出范围。");
        var source=cells.FirstOrDefault(c=>c.Id==id)??throw new ArgumentException("未找到设备位置。");
        var target=cells.FirstOrDefault(c=>c.Column==column&&c.Row==row);
        return cells.Select(c=>c.Id==source.Id?source with{Column=column,Row=row}:target!=null&&c.Id==target.Id?target with{Column=source.Column,Row=source.Row}:c).ToArray();
    }
    public static Guid Neighbor(IReadOnlyList<LayoutCell> cells,Guid source,LayoutSide side)
    {
        var cell=cells.FirstOrDefault(c=>c.Id==source);if(cell==null)return Guid.Empty;
        var (dx,dy)=side switch{LayoutSide.Left=>(-1,0),LayoutSide.Right=>(1,0),LayoutSide.Up=>(0,-1),LayoutSide.Down=>(0,1),_=>(0,0)};
        return side==LayoutSide.None?Guid.Empty:cells.FirstOrDefault(c=>c.Column==cell.Column+dx&&c.Row==cell.Row+dy)?.Id??Guid.Empty;
    }
    public static (int X,int Y) Entry(LayoutSide side,double x,double y,int width,int height,int targetWidth,int targetHeight)
    {
        int Map(double value,int source,int target)=>(int)Math.Round(Math.Clamp(value/Math.Max(1,source-1),0,1)*Math.Max(0,target-1));
        return side switch{
            LayoutSide.Left=>(Math.Max(0,targetWidth-3),Map(y,height,targetHeight)),
            LayoutSide.Right=>(Math.Min(2,targetWidth-1),Map(y,height,targetHeight)),
            LayoutSide.Up=>(Map(x,width,targetWidth),Math.Max(0,targetHeight-3)),
            LayoutSide.Down=>(Map(x,width,targetWidth),Math.Min(2,targetHeight-1)),
            _=>throw new ArgumentException("无穿屏方向。")};
    }
    public static ControlMessage Encode(LayoutSnapshot snapshot)
    {
        if(!IsValid(snapshot))throw new InvalidDataException("共享屏幕排列无效。");
        return new(){Kind=MessageKind,Payload=JsonSerializer.Serialize(snapshot)};
    }
    public static bool TryDecode(ControlMessage message,out LayoutSnapshot snapshot)
    {
        snapshot=null!;
        if(message.Kind!=MessageKind||message.Payload is not{Length:>0 and <=MaxPayloadLength})return false;
        try{var value=JsonSerializer.Deserialize<LayoutSnapshot>(message.Payload);if(!IsValid(value)||value!.Cells==null)return false;snapshot=value;return true;}
        catch(JsonException){return false;}
    }
}
