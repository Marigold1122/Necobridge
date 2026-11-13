using System.Text.Json;

namespace NekoBridge.Core;

public sealed record LayoutSnapshot(long Revision, Guid Editor, Guid[] DeviceIds);

/// <summary>Shared horizontal order only. Device IDs never confer pairing or routing permission.</summary>
public static class LayoutSync
{
    public const int MaxDevices = 64;
    public const int MaxPayloadLength = 4096;
    public const string MessageKind = "layout";

    public static bool IsValid(LayoutSnapshot? snapshot) => snapshot is { Revision: > 0, DeviceIds: not null }
        && snapshot.Editor != Guid.Empty && snapshot.DeviceIds.Length is > 0 and <= MaxDevices
        && snapshot.DeviceIds.All(id => id != Guid.Empty)
        && snapshot.DeviceIds.Distinct().Count() == snapshot.DeviceIds.Length;

    public static LayoutSnapshot Read(AppSettings settings) => new(settings.LayoutRevision, settings.LayoutEditor, settings.Layout.ToArray());

    public static int Compare(LayoutSnapshot left, LayoutSnapshot right)
    {
        int revision = left.Revision.CompareTo(right.Revision);
        return revision != 0 ? revision : left.Editor.CompareTo(right.Editor);
    }

    public static LayoutSnapshot Create(AppSettings settings, IEnumerable<Guid> ids)
    {
        var snapshot = new LayoutSnapshot(checked(Math.Max(0, settings.LayoutRevision) + 1), settings.Id, ids.ToArray());
        if (!IsValid(snapshot)) throw new InvalidDataException("屏幕排列无效或超过 64 台设备。");
        return snapshot;
    }

    public static bool Apply(AppSettings settings, LayoutSnapshot snapshot, Action persist)
    {
        if (!IsValid(snapshot)) throw new InvalidDataException("共享屏幕排列无效。");
        var previous = Read(settings);
        if (Compare(snapshot, previous) <= 0) return false;
        settings.Layout = snapshot.DeviceIds.ToList();
        settings.LayoutRevision = snapshot.Revision;
        settings.LayoutEditor = snapshot.Editor;
        try { persist(); }
        catch
        {
            settings.Layout = previous.DeviceIds.ToList();
            settings.LayoutRevision = previous.Revision;
            settings.LayoutEditor = previous.Editor;
            throw;
        }
        return true;
    }

    public static ControlMessage Encode(LayoutSnapshot snapshot)
    {
        if (!IsValid(snapshot)) throw new InvalidDataException("共享屏幕排列无效。");
        return new ControlMessage { Kind = MessageKind, Payload = JsonSerializer.Serialize(snapshot) };
    }

    public static bool TryDecode(ControlMessage message, out LayoutSnapshot snapshot)
    {
        snapshot = null!;
        if (message.Kind != MessageKind || message.Payload is not { Length: > 0 and <= MaxPayloadLength }) return false;
        try
        {
            var value = JsonSerializer.Deserialize<LayoutSnapshot>(message.Payload);
            if (!IsValid(value)) return false;
            snapshot = value!;
            return true;
        }
        catch (JsonException) { return false; }
    }
}
