using Microsoft.Data.Sqlite;
using NekoBridge.Windows.History;

string root = Path.Combine(Path.GetTempPath(), "NekoBridge-HistorySmoke-" + Guid.NewGuid().ToString("N"));
Guid machine = Guid.NewGuid(), textId = Guid.NewGuid(), imageId = Guid.NewGuid();
byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
try
{
    using (var store = new HistoryStore(root))
    {
        await store.AddAsync(new(textId, "text", machine, "Smoke", Text: "持久文本 %_ ' OR 1=1 --"));
        await store.AddAsync(new(imageId, "image", machine, "Smoke", Png: png));
        await store.SetFavoriteAsync(textId, true);
        Check((await store.AddAsync(new(Guid.NewGuid(), "text", machine, "Other", Text: "持久文本 %_ ' OR 1=1 --"))).Id == textId, "Content dedupe failed");
        Check((await store.SearchAsync("%_")).Count == 1, "Literal parameterized search failed");
        Check((await store.SearchPageAsync(kind: "image")).Single().Id == imageId, "Kind search failed");
        Check((await store.SearchPageAsync(favoritesOnly: true, kind: "image")).Count == 0, "Kind/favorite filtering failed");
        try { await store.SearchPageAsync(kind: "invalid"); throw new Exception("Invalid kind accepted"); }
        catch (ArgumentOutOfRangeException) { }
    }
    using (var store = new HistoryStore(root))
    {
        Check((await store.GetAsync(textId))?.IsFavorite == true, "Favorite did not survive restart");
        Check((await store.ReadImageAsync(imageId))!.SequenceEqual(png), "Image did not survive restart");
        var files = await store.AddAsync(new(Guid.NewGuid(), "files", machine, "Smoke", Paths: ["C:\\fake-source.txt"]));
        string saved = Path.Combine(store.GetSaveDirectory(files.Id), "copy.txt");
        await File.WriteAllTextAsync(saved, "fake");
        await store.AttachSavedFilesAsync(files.Id, [saved], 4);
        Check((await store.GetAsync(files.Id))?.SavedPaths?.Length == 1, "Saved paths missing");
        await store.RemoveAsync(files.Id);
        Check(!File.Exists(saved), "Owned saved copy not removed");
        await store.RemoveAsync(imageId);
        Check(await store.GetAsync(imageId) == null, "Remove failed");
        await store.ClearAsync(false);
        Check((await store.SearchAsync()).Count == 1, "Favorite not preserved");
    }
    using (var db = new SqliteConnection($"Data Source={Path.Combine(root, "history.sqlite3")};Pooling=False"))
    {
        db.Open(); using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO history SELECT '00000000-0000-0000-0000-000000000001','old','text',source,source_name,'old',NULL,NULL,NULL,0,0,0,3,NULL,'old',NULL FROM history LIMIT 1";
        command.ExecuteNonQuery();
    }
    using (var store = new HistoryStore(root, new(MaxEntries: 2)))
    {
        Check((await store.SearchAsync("old")).Count == 0, "Age prune failed");
        for (int i = 0; i < 4; i++) await store.AddAsync(new(Guid.NewGuid(), "text", machine, "Smoke", Text: "fake" + i));
        Check((await store.SearchAsync()).Count == 2, "Count prune failed");
        Check((await store.GetAsync(textId))?.IsFavorite == true, "Prune lost favorite");
        await store.ClearAsync();
        Check((await store.SearchAsync()).Count == 0, "Clear failed");
    }
    using (var store = new HistoryStore(Path.Combine(root, "quota"), new(MaxStoredBytes: 8)))
    {
        await store.AddAsync(new(Guid.NewGuid(), "text", machine, "Smoke", Text: "12345678"));
        await store.AddAsync(new(Guid.NewGuid(), "text", machine, "Smoke", Text: "abcdefg"));
        Check((await store.SearchAsync()).Count == 1, "Byte quota failed");
    }
    Check(new HistoryRetention() == new HistoryRetention(1000, 30, 512L * 1024 * 1024), "Retention defaults changed");
    string unlimitedRoot = Path.Combine(root, "unlimited");
    var expected = new HashSet<Guid>();
    using (var store = new HistoryStore(unlimitedRoot, new(MaxEntries: 0)))
    {
        for (int i = 0; i < 1100; i++)
        {
            Guid id = Guid.NewGuid(); expected.Add(id);
            await store.AddAsync(new(id, "text", machine, "Smoke", Text: "page-item-" + i));
        }
        await store.SetFavoriteAsync(expected.First(), true);
        await store.UpdateRetentionAsync(new(MaxEntries: 9999));
        await store.UpdateRetentionAsync(new(MaxEntries: int.MaxValue));
        await store.UpdateRetentionAsync(new(MaxEntries: 0));
        try { await store.UpdateRetentionAsync(new(MaxEntries: -1)); throw new Exception("Negative count accepted"); }
        catch (ArgumentOutOfRangeException) { }
        try { await store.SearchPageAsync(offset: -1); throw new Exception("Negative offset accepted"); }
        catch (ArgumentOutOfRangeException) { }
        try { await store.SearchAsync(limit: 1001); throw new Exception("Unbounded search accepted"); }
        catch (ArgumentOutOfRangeException) { }
        Check((await store.SearchAsync(null, false, 100, CancellationToken.None)).Count == 100, "Legacy positional search failed");
    }
    // Tie every timestamp to verify the stable ID tiebreaker across page boundaries.
    using (var db = new SqliteConnection($"Data Source={Path.Combine(unlimitedRoot, "history.sqlite3")};Pooling=False"))
    {
        db.Open(); using var command = db.CreateCommand();
        command.CommandText = "UPDATE history SET copied=$now";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }
    using (var store = new HistoryStore(unlimitedRoot, new(MaxEntries: 0)))
    {
        var seen = new HashSet<Guid>();
        for (int offset = 0; offset < 1100; offset += 100)
        {
            var page = await store.SearchPageAsync("page-item-", false, "text", 100, offset);
            Check(page.Count == 100, "Unlimited history was pruned or page was incomplete");
            foreach (var entry in page) Check(seen.Add(entry.Id), "Duplicate across pages");
        }
        Check(seen.SetEquals(expected), "Paging omitted history entries");
        Check((await store.SearchPageAsync(offset: 1100)).Count == 0, "End page not empty");
        Check((await store.SearchPageAsync(favoritesOnly: true)).Count == 1, "Favorite paging failed");
        await store.UpdateRetentionAsync(new());
        Check((await store.SearchAsync(limit: 1000)).Count == 1000 && (await store.SearchPageAsync(offset: 1000)).Count == 0, "Default count prune failed");
        Check((await store.GetAsync(expected.First()))?.IsFavorite == true, "Default prune lost favorite");
    }
    using (var store = new HistoryStore(Path.Combine(root, "unlimited-quota"), new(MaxEntries: 0, MaxStoredBytes: 8)))
    {
        var favorite = await store.AddAsync(new(Guid.NewGuid(), "text", machine, "Smoke", Text: "12345678"));
        await store.SetFavoriteAsync(favorite.Id, true);
        try { await store.AddAsync(new(Guid.NewGuid(), "text", machine, "Smoke", Text: "blocked")); throw new Exception("Unlimited count bypassed favorite byte capacity"); }
        catch (IOException) { }
        Check((await store.SearchAsync()).Single().Id == favorite.Id, "Capacity failure lost favorite or committed new entry");
        await store.SetFavoriteAsync(favorite.Id, false);
        await store.AddAsync(new(Guid.NewGuid(), "text", machine, "Smoke", Text: "abcdefg"));
        Check((await store.SearchAsync()).Count == 1, "Unlimited count bypassed byte pruning");
    }
    string recoveryRoot = Path.Combine(root, "recovery");
    Guid pendingId = Guid.NewGuid(), committedId = Guid.NewGuid();
    string unfinished, committed;
    using (var store = new HistoryStore(recoveryRoot))
    {
        await store.AddAsync(new(pendingId, "files", machine, "Smoke", Paths: ["C:\\fake-pending.txt"]));
        unfinished = Path.Combine(store.GetSaveDirectory(pendingId), "unfinished.part");
        await File.WriteAllTextAsync(unfinished, "partial fake data");
        await store.AddAsync(new(committedId, "files", machine, "Smoke", Paths: ["C:\\fake-committed.txt"]));
        Check(File.Exists(unfinished), "Runtime sweep removed an active uncommitted copy");
        committed = Path.Combine(store.GetSaveDirectory(committedId), "complete.txt");
        await File.WriteAllTextAsync(committed, "fake");
        await store.AttachSavedFilesAsync(committedId, [committed], 4);
    }
    using (var store = new HistoryStore(recoveryRoot))
    {
        Check(!File.Exists(unfinished), "Startup did not remove an unfinished copy");
        Check(Directory.GetFileSystemEntries(store.GetSaveDirectory(pendingId)).Length == 0, "Recovered save directory is not empty");
        Check(await store.GetAsync(pendingId) != null, "Recovery removed history metadata");
        Check(File.Exists(committed) && (await store.GetAsync(committedId))?.SavedPaths?.Single() == committed, "Recovery removed an attached copy");
    }
    Console.WriteLine("History smoke passed: restart, text/image, dedupe, literal search, favorite, saved-copy/delete, clear, age/count/byte pruning, unfinished-copy recovery.");
}
finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
