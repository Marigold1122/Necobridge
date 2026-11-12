using System;
using System.IO;

namespace NekoBridge.Windows;

internal static class DragDiagnostics
{
    private static readonly object Gate=new();
    internal static void Failure(string stage,Exception error)
        => Write($"{stage} {error.GetType().FullName} HRESULT=0x{error.HResult:X8}");
    internal static void Stage(Guid session,string stage)=>Write($"{session:N} {stage}");
    private static void Write(string entry)
    {
        lock(Gate)
        {
            try
            {
                string directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"NekoBridge");
                Directory.CreateDirectory(directory);
                string path=Path.Combine(directory,"drag-diagnostics.log");
                if(File.Exists(path)&&new FileInfo(path).Length>262144)File.Move(path,path+".previous",true);
                File.AppendAllText(path,$"{DateTimeOffset.Now:O} {entry}\n");
            }
            catch(IOException){}
            catch(UnauthorizedAccessException){}
        }
    }
}
