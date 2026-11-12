using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using NekoBridge.Core;
using NekoBridge.Windows.History;

internal static class Program
{
 [STAThread] static int Main()
 {
  string root=Path.Combine(Path.GetTempPath(),"NekoBridge-HistoryUi-"+Guid.NewGuid().ToString("N"));
  try
  {
   SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
   using var store=new HistoryStore(root);
   store.AddAsync(new HistoryCapture(Guid.NewGuid(),"text",Guid.NewGuid(),"Smoke","Temporary copy-button item")).GetAwaiter().GetResult();
   var settings=new AppSettings();
   var constructor=typeof(HistoryWindow).GetConstructors(BindingFlags.NonPublic|BindingFlags.Instance).Single();
   var window=(Window)constructor.Invoke(new object?[]{store,null,settings,(Action)(()=>{}),(Func<string,Task>)(_=>Task.CompletedTask),CancellationToken.None});
   var model=window.DataContext;
   var property=model.GetType().GetProperty("IsSettingsOpen")!;
   Wait((Task)model.GetType().GetMethod("RefreshAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(model,null)!);
   foreach(var text in new[]{"0","9999","1000"})
   {
       model.GetType().GetProperty("MaxItems")!.SetValue(model,text);
       ((System.Windows.Input.ICommand)model.GetType().GetProperty("CloseSettingsCommand")!.GetValue(model)!).Execute(null);
       var deadline=DateTime.UtcNow.AddSeconds(5);
       while((bool)model.GetType().GetProperty("Busy",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(model)!&&DateTime.UtcNow<deadline)Pump();
       if(settings.ClipboardHistoryMaxItems!=int.Parse(text))throw new Exception("Numeric count was clamped");
   }
   window.Show();window.UpdateLayout();
   var list=(System.Windows.Controls.ListBox)window.FindName("ClipList")!;list.UpdateLayout();
   var container=list.ItemContainerGenerator.ContainerFromIndex(0) as System.Windows.DependencyObject;
   if(container==null)throw new Exception("Missing test item");
   var copy=Descendants(container).OfType<System.Windows.Controls.Button>().FirstOrDefault(b=>Equals(b.ToolTip,"复制"));
   if(copy==null||copy.CommandParameter!=list.Items[0]||copy.Command!=model.GetType().GetProperty("CopyCommand")!.GetValue(model))throw new Exception("Inline copy action not bound to its item");
   if(model.GetType().GetProperty("AutoPaste")!=null||model.GetType().GetProperty("CopyAndPasteCommand")!=null)throw new Exception("Auto paste should be removed");
   if(window.Width>600)throw new Exception("Expected compact single-list window");
   property.SetValue(model,true);window.UpdateLayout();
   if(window.FindName("HotkeyRecorder")==null||window.FindName("ClipList")==null)throw new Exception("Missing functional controls");
   property.SetValue(model,false);window.UpdateLayout();
   File.WriteAllText("D:/code/nekobridge/design/simple-history/ui-smoke-result.txt","PASS compact window and inline copy bound to its own item; numeric 0/9999/1000 retained without clamping; auto-paste removed; settings open/close. Temporary history only, actual clipboard untouched.");
   var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(200)};
   timer.Tick+=(_,_)=>{timer.Stop();window.Hide();Dispatcher.CurrentDispatcher.InvokeShutdown();};timer.Start();
   Dispatcher.Run();return 0;
  }
  catch(Exception ex){File.WriteAllText("D:/code/nekobridge/design/simple-history/ui-smoke-result.txt",ex.ToString());return 1;}
  finally{if(Directory.Exists(root))Directory.Delete(root,true);}
 }
 static void Wait(Task task)
 {
  if(!task.IsCompleted){var frame=new DispatcherFrame();var dispatcher=Dispatcher.CurrentDispatcher;task.ContinueWith(_=>dispatcher.BeginInvoke(new Action(()=>frame.Continue=false)),TaskScheduler.Default);Dispatcher.PushFrame(frame);}
  task.GetAwaiter().GetResult();
 }
 static void Pump(){var f=new DispatcherFrame();var t=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(10)};t.Tick+=(_,_)=>{t.Stop();f.Continue=false;};t.Start();Dispatcher.PushFrame(f);}
 static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject parent)
 {
  for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);i++){var child=System.Windows.Media.VisualTreeHelper.GetChild(parent,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}
 }
}
