namespace NekoBridge.Core;

public sealed class AppSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DeviceName { get; set; } = Environment.MachineName;
    public int Port { get; set; } = 24870;
    public string ReceiveFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public bool ClipboardEnabled { get; set; } = true;
    public bool ClipboardHistoryEnabled { get; set; } = true;
    public string ClipboardHistoryHotkey { get; set; } = "Alt+V";
    public bool ClipboardHistoryCaptureImages { get; set; } = true;
    public bool ClipboardHistoryCaptureFiles { get; set; } = true;
    public int ClipboardHistoryDays { get; set; } = 30;
    public int ClipboardHistoryMaxItems { get; set; } = 1000;
    public long ClipboardHistoryMaxBytes { get; set; } = 512L*1024*1024;
    public bool StartWithWindows { get; set; }
    public List<PeerSettings> Peers { get; set; } = [];
    public List<Guid> Layout { get; set; } = [];
}
public sealed class PeerSettings
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 24870;
    public string Fingerprint { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
}
public sealed record DeviceDescriptor(Guid Id, string Name, int Width, int Height);
public sealed record PeerSnapshot(Guid Id, string DisplayName, string Address, string StateText, int Width, int Height, bool IsConnected)
{
    public string SizeText => $"{Width} × {Height}";
}
public sealed record DiscoveredPeer(Guid Id, string Name, string Host, int Port)
{
    public string Display => $"{Name} — {Host}:{Port}";
}
public sealed record ControlMessage
{
    public string Kind { get; init; } = "";
    public int X { get; init; }
    public int Y { get; init; }
    public int Data { get; init; }
    public int Flags { get; init; }
    public string? Text { get; init; }
    public Guid Id { get; init; }
    public string? Payload { get; init; }
}
public sealed record FileEntry
{
    public int Index { get; init; }
    public string RelativePath { get; init; } = "";
    public long Length { get; init; }
    public bool IsDirectory { get; init; }
}
public sealed record FileOffer
{
    public Guid Id { get; init; }
    public List<FileEntry> Entries { get; init; } = [];
    public long TotalBytes { get; init; }
    public int ItemCount { get; init; }
    public string DisplayName { get; init; } = "";
}
public sealed record FileTransferProgress(Guid TransferId, string Name, long Bytes, long TotalBytes, string State);
