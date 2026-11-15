using System;
using System.IO;
using Microsoft.Win32;

namespace NekoBridge.Storage;

// A per-user locator outside the install tree survives updates and uninstall.
internal static class DataLocation
{
    internal const string RegistryPath=@"Software\NekoBridge";
    internal const string InstanceMutex=@"Local\NekoBridge-desktop";
    internal static string LegacyDirectory=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"NekoBridge");
    internal static string DefaultInstallDirectory=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","NekoBridge");
    internal static string Resolve()
    {
        using var key=Registry.CurrentUser.OpenSubKey(RegistryPath);
        var configured=key?.GetValue("DataDirectory") as string;
        return string.IsNullOrWhiteSpace(configured)?LegacyDirectory:Normalize(configured);
    }
    internal static string InstallDirectory()
    {
        using var key=Registry.CurrentUser.OpenSubKey(RegistryPath);
        var configured=key?.GetValue("InstallDirectory") as string;
        return string.IsNullOrWhiteSpace(configured)?DefaultInstallDirectory:Normalize(configured);
    }
    internal static string Normalize(string path)
    {
        if(string.IsNullOrWhiteSpace(path)||!Path.IsPathFullyQualified(path))throw new ArgumentException("请选择完整的本地目录路径。");
        var full=Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if(full.StartsWith(@"\\",StringComparison.Ordinal)||full.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!),StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择本地磁盘中的文件夹，不要选择磁盘根目录或网络路径。");
        return full;
    }
    internal static bool Same(string a,string b)=>Normalize(a).Equals(Normalize(b),StringComparison.OrdinalIgnoreCase);
    internal static bool Within(string child,string parent)=>Same(child,parent)||Normalize(child).StartsWith(Normalize(parent)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    internal static void EnsureNoLinks(string path)
    {
        for(string? current=Path.GetFullPath(path);current!=null;current=Path.GetDirectoryName(current))
            if((Directory.Exists(current)||File.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                throw new IOException("目录包含链接或重解析点，请选择普通本地文件夹。");
    }
    internal static void Save(string dataDirectory,string installDirectory)
    {
        using var key=Registry.CurrentUser.CreateSubKey(RegistryPath);
        // InstallDirectory is only a wizard default. DataDirectory is the single
        // authoritative switch and is committed last, after migration succeeds.
        key.SetValue("InstallDirectory",Normalize(installDirectory),RegistryValueKind.String);
        key.SetValue("DataDirectory",Normalize(dataDirectory),RegistryValueKind.String);
    }
}
