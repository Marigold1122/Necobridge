using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace NekoBridge.Storage;

// Used by the installer while holding the same per-user mutex as the app.
// Copy only owned data, never the legacy install's binaries. Do not delete source.
internal static class DataMigration
{
    internal static bool HasData(string path)=>File.Exists(Path.Combine(path,"settings.json"))||Directory.Exists(Path.Combine(path,"ClipboardHistory"));
    internal static void Validate(string source,string target,string install)
    {
        source=DataLocation.Normalize(source);target=DataLocation.Normalize(target);install=DataLocation.Normalize(install);
        DataLocation.EnsureNoLinks(source);DataLocation.EnsureNoLinks(target);DataLocation.EnsureNoLinks(install);
        if(DataLocation.Within(target,install)||DataLocation.Within(install,target))throw new IOException("程序目录和数据目录不能相同，也不能互相包含。");
        if(!DataLocation.Same(source,target))
        {
            if(DataLocation.Within(target,source)||DataLocation.Within(source,target))throw new IOException("新数据目录不能与原数据目录互相包含。");
            if(File.Exists(target)||Directory.Exists(target)&&Directory.EnumerateFileSystemEntries(target).Any())throw new IOException("新数据目录不是空目录。为避免覆盖，请选择空文件夹。");
        }
        if(HasData(source)&&DataLocation.Within(source,install))throw new IOException("安装位置包含现有数据，请选择单独的程序目录。");
    }
    internal static void Prepare(string source,string target,string install)
    {
        Validate(source,target,install);
        if(DataLocation.Same(source,target))
        {
            Directory.CreateDirectory(target);ProbeWritable(target);return;
        }
        var parent=Path.GetDirectoryName(target)!;Directory.CreateDirectory(parent);
        var staging=Path.Combine(parent,".nekobridge-migrate-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(staging);
        // Private staging is retained on failure; original data is never removed.
        foreach(var name in new[]{"settings.json","identity-thumbprint.txt","errors.log","drag-diagnostics.log"})
        {
            var file=Path.Combine(source,name);
            if(File.Exists(file)){DataLocation.EnsureNoLinks(file);File.Copy(file,Path.Combine(staging,name));}
        }
        var history=Path.Combine(source,"ClipboardHistory");
        if(Directory.Exists(history))
        {
            CopyTree(history,Path.Combine(staging,"ClipboardHistory"));
            var database=Path.Combine(history,"history.sqlite3");
            if(File.Exists(database))CopyDatabase(database,Path.Combine(staging,"ClipboardHistory","history.sqlite3"),source,target);
        }
        var settings=Path.Combine(staging,"settings.json");
        if(File.Exists(settings))using(JsonDocument.Parse(File.ReadAllText(settings))) { }
        // Only remove an already validated EMPTY destination. Source remains
        // intact even if installing or recording the locator later fails.
        if(Directory.Exists(target))Directory.Delete(target,false);
        Directory.Move(staging,target);
    }
    internal static void ProbeWritable(string directory)
    {
        Directory.CreateDirectory(directory);DataLocation.EnsureNoLinks(directory);
        var file=Path.Combine(directory,".nekobridge-write-"+Guid.NewGuid().ToString("N"));
        using(var stream=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None)){stream.WriteByte(0);stream.Flush(true);}
        File.Delete(file);
    }
    private static void CopyTree(string source,string target)
    {
        DataLocation.EnsureNoLinks(source);Directory.CreateDirectory(target);
        foreach(var file in Directory.EnumerateFiles(source))
        {
            DataLocation.EnsureNoLinks(file);
            if(Path.GetFileName(file) is "history.sqlite3" or "history.sqlite3-wal" or "history.sqlite3-shm")continue;
            var destination=Path.Combine(target,Path.GetFileName(file));File.Copy(file,destination);
            if(new FileInfo(file).Length!=new FileInfo(destination).Length)throw new IOException("数据文件复制不完整。");
        }
        foreach(var child in Directory.EnumerateDirectories(source))CopyTree(child,Path.Combine(target,Path.GetFileName(child)));
    }
    private static void CopyDatabase(string sourceFile,string targetFile,string oldRoot,string newRoot)
    {
        using var source=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=sourceFile,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());source.Open();
        using var destination=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=targetFile,Pooling=false}.ToString());destination.Open();
        source.BackupDatabase(destination);
        using(var command=destination.CreateCommand()){command.CommandText="PRAGMA integrity_check";if(!Equals(command.ExecuteScalar(),"ok"))throw new IOException("历史数据库完整性检查失败。");}
        var oldCopies=Path.Combine(oldRoot,"ClipboardHistory","copies");var newCopies=Path.Combine(newRoot,"ClipboardHistory","copies");
        var changes=new List<(string Id,string Paths)>();
        using(var command=destination.CreateCommand())
        {
            command.CommandText="SELECT id,saved FROM history WHERE saved IS NOT NULL";
            using var reader=command.ExecuteReader();
            while(reader.Read())
            {
                var paths=JsonSerializer.Deserialize<string[]>(reader.GetString(1))??throw new IOException("历史文件副本路径无效。");
                var updated=paths.Select(path=>DataLocation.Within(path,oldCopies)?Path.Combine(newCopies,Path.GetRelativePath(oldCopies,path)):path).ToArray();
                changes.Add((reader.GetString(0),JsonSerializer.Serialize(updated)));
            }
        }
        using var transaction=destination.BeginTransaction();
        foreach(var change in changes)
        {
            using var command=destination.CreateCommand();command.Transaction=transaction;
            command.CommandText="UPDATE history SET saved=$paths WHERE id=$id";command.Parameters.AddWithValue("$paths",change.Paths);command.Parameters.AddWithValue("$id",change.Id);command.ExecuteNonQuery();
        }
        transaction.Commit();
    }
}
