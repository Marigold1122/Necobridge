using NekoBridge.Storage;
using Microsoft.Data.Sqlite;
using System.Text.Json;

if(args.Length==2)
{
 using var stream=File.OpenRead(args[0]);using var pe=new System.Reflection.PortableExecutable.PEReader(stream);
 var metadata=System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
 var resources=pe.GetSectionData(pe.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress).GetContent();
 bool found=false;
 foreach(var handle in metadata.ManifestResources)
 {
  var resource=metadata.GetManifestResource(handle);
  if(metadata.GetString(resource.Name)!="NekoBridge.Setup.Payload.exe")continue;
  int offset=checked((int)resource.Offset);int size=System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(resources.AsSpan(offset,4));
  var embedded=resources.AsSpan(offset+4,size);
  var expected=System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(args[1]));
  Require(System.Security.Cryptography.SHA256.HashData(embedded).SequenceEqual(expected),"embedded installer payload hash");found=true;
 }
 Require(found,"installer payload embedded");Console.WriteLine("PASS embedded native installer payload matches generated package");
}
var root=Path.Combine(Path.GetTempPath(),"NekoBridge-InstallSmoke-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
try
{
 var source=Path.Combine(root,"old");var target=Path.Combine(root,"data");var install=Path.Combine(root,"program");
 Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"settings.json"),"{\"Id\":\"test-fixture\",\"StartWithWindows\":true}");
 File.WriteAllText(Path.Combine(source,"identity-thumbprint.txt"),"FAKE-NOT-A-CERTIFICATE");
 File.WriteAllText(Path.Combine(source,"NekoBridge.exe"),"not-real-program");
 var history=Path.Combine(source,"ClipboardHistory");var copies=Path.Combine(history,"copies","test","files");Directory.CreateDirectory(copies);var file=Path.Combine(copies,"sample.txt");File.WriteAllText(file,"temporary sample");
 var external=Path.Combine(root,"original.txt");File.WriteAllText(external,"not history owned");
 var database=Path.Combine(history,"history.sqlite3");
 using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=database,Pooling=false}.ToString()))
 {
  db.Open();using var command=db.CreateCommand();command.CommandText="PRAGMA journal_mode=WAL; CREATE TABLE history(id TEXT,saved TEXT); INSERT INTO history VALUES ('1',$paths)";command.Parameters.AddWithValue("$paths",JsonSerializer.Serialize(new[]{file,external}));command.ExecuteNonQuery();
  // Backup API must include committed WAL while a reader is open.
  DataMigration.Prepare(source,target,install);
 }
 Require(File.Exists(file),"source copy retained");Require(File.Exists(Path.Combine(source,"settings.json")),"old settings retained");
 Require(!File.Exists(Path.Combine(target,"NekoBridge.exe")),"legacy binaries not migrated");
 var newFile=Path.Combine(target,Path.GetRelativePath(source,file));Require(File.ReadAllText(newFile)=="temporary sample","saved files copied");
 using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(target,"ClipboardHistory","history.sqlite3"),Pooling=false}.ToString()))
 {
  db.Open();using var command=db.CreateCommand();command.CommandText="SELECT saved FROM history";var paths=JsonSerializer.Deserialize<string[]>((string)command.ExecuteScalar()!)!;Require(paths.SequenceEqual(new[]{newFile,external}),"rewrite only history-owned paths");
 }
 Reject(()=>DataMigration.Validate(source,target,install),"nonempty destination");
 Reject(()=>DataMigration.Validate(source,Path.Combine(source,"child"),install),"nested source destination");
 Reject(()=>DataMigration.Validate(source,source,source),"data install collision");
 Reject(()=>DataLocation.Normalize(Path.GetPathRoot(root)!),"drive root");
 var fresh=Path.Combine(root,"fresh");DataMigration.Prepare(Path.Combine(root,"no-old-data"),fresh,install);Require(Directory.Exists(fresh),"fresh data directory");
 DataMigration.Prepare(source,source,install);Require(File.Exists(file),"same data location untouched");
 Console.WriteLine("PASS installer data migration, SQLite WAL backup, owned paths rewrite, no source deletion, collision validation and first install");
 Console.WriteLine("No real settings, registry entries, certificates or installed applications accessed or modified.");
}
finally{Directory.Delete(root,true);}
static void Require(bool condition,string name){if(!condition)throw new Exception(name);}
static void Reject(Action action,string name){try{action();}catch(IOException){return;}catch(ArgumentException){return;}throw new Exception("Expected rejection: "+name);}
