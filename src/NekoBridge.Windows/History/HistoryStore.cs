using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace NekoBridge.Windows.History;

public sealed record HistoryRetention(int MaxEntries = 1000, int MaxDays = 30, long MaxStoredBytes = 512L * 1024 * 1024);
public sealed record HistoryCapture(Guid Id, string Kind, Guid SourceMachine, string SourceName,
    string? Text = null, byte[]? Png = null, string[]? Paths = null, Guid? OfferId = null, string? FileSummary = null);
public sealed record HistoryEntry(Guid Id, string Kind, Guid SourceMachine, string SourceName,
    string? Text, string[]? Paths, Guid? OfferId, string? FileSummary, DateTimeOffset CreatedAt,
    DateTimeOffset LastCopiedAt, bool IsFavorite, long StoredBytes, string[]? SavedPaths = null)
{
    // File references are not durable copies. The caller must revalidate local paths or the live remote offer.
    public bool RequiresFileAvailabilityCheck => Kind == "files";
}

public sealed class HistoryStore : IDisposable
{
    private readonly object _gate = new();
    private readonly SqliteConnection _db;
    private readonly string _images;
    private readonly string _copies;
    private HistoryRetention _retention;
    private bool _disposed;

    public HistoryStore(string directory, HistoryRetention? retention = null)
    {
        _retention = retention ?? new();
        ValidateRetention(_retention);
        Directory.CreateDirectory(directory);
        _images = Path.Combine(Path.GetFullPath(directory), "images");
        Directory.CreateDirectory(_images);
        _copies = Path.Combine(Path.GetFullPath(directory), "copies");
        Directory.CreateDirectory(_copies);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetFullPath(directory), "history.sqlite3"), Pooling = false }.ToString());
        try
        {
            _db.Open();
            Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA secure_delete=ON;");
            Execute("""
                CREATE TABLE IF NOT EXISTS history (
                  id TEXT PRIMARY KEY, digest TEXT NOT NULL UNIQUE, kind TEXT NOT NULL,
                  source TEXT NOT NULL, source_name TEXT NOT NULL, text TEXT, paths TEXT, offer TEXT, summary TEXT,
                  created INTEGER NOT NULL, copied INTEGER NOT NULL, favorite INTEGER NOT NULL DEFAULT 0,
                  bytes INTEGER NOT NULL, image TEXT, search TEXT NOT NULL, saved TEXT);
                CREATE INDEX IF NOT EXISTS history_copied ON history(copied);
                """);
            RecoverUnattachedCopies();
            PruneCore();
        }
        catch { _db.Dispose(); throw; }
    }

    // Call only after a real local read or successful remote clipboard write, never on an attempted copy.
    public Task<HistoryEntry> AddAsync(HistoryCapture capture, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var snapshot = capture with { Png = capture.Png?.ToArray(), Paths = capture.Paths?.ToArray() };
        return Run(() => AddCore(snapshot), cancellationToken);
    }

    public Task<IReadOnlyList<HistoryEntry>> SearchAsync(string? query = null, bool favoritesOnly = false, int limit = 100, CancellationToken cancellationToken = default) =>
        SearchPageAsync(query, favoritesOnly, null, limit, 0, cancellationToken);

    // Retention count and page size are independent; even unlimited history is read in bounded pages.
    public Task<IReadOnlyList<HistoryEntry>> SearchPageAsync(string? query = null, bool favoritesOnly = false, string? kind = null, int limit = 100, int offset = 0, CancellationToken cancellationToken = default) => Run<IReadOnlyList<HistoryEntry>>(() =>
    {
        if (limit is < 1 or > 1000 || query?.Length > 4096) throw new ArgumentOutOfRangeException(nameof(limit));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (kind is not (null or "text" or "image" or "files")) throw new ArgumentOutOfRangeException(nameof(kind));
        PruneCore();
        using var command = Command("SELECT * FROM history WHERE ($f=0 OR favorite=1) AND ($kind IS NULL OR kind=$kind) AND search LIKE $q ESCAPE '!' ORDER BY favorite DESC,copied DESC,id ASC LIMIT $n OFFSET $offset",
            ("$f", favoritesOnly ? 1 : 0), ("$kind", kind), ("$q", "%" + (query ?? "").Replace("!", "!!").Replace("%", "!%").Replace("_", "!_") + "%"), ("$n", limit), ("$offset", offset));
        using var reader = command.ExecuteReader();
        var entries = new List<HistoryEntry>();
        while (reader.Read()) entries.Add(Read(reader));
        return entries;
    }, cancellationToken);

    public Task<HistoryEntry?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Run(() => Find(id), cancellationToken);
    public Task<byte[]?> ReadImageAsync(Guid id, CancellationToken cancellationToken = default) => Run(() =>
    {
        using var command = Command("SELECT image FROM history WHERE id=$id", ("$id", id.ToString()));
        return command.ExecuteScalar() is string name ? File.ReadAllBytes(ImagePath(name)) : null;
    }, cancellationToken);
    public Task SetFavoriteAsync(Guid id, bool favorite, CancellationToken cancellationToken = default) => Run(() =>
    {
        if (Execute("UPDATE history SET favorite=$f WHERE id=$id", ("$f", favorite ? 1 : 0), ("$id", id.ToString())) == 0) throw new KeyNotFoundException("History entry no longer exists.");
        return true;
    }, cancellationToken);
    public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default) => Run(() =>
    { Execute("DELETE FROM history WHERE id=$id", ("$id", id.ToString())); SweepImages(); return true; }, cancellationToken);
    public Task ClearAsync(bool includeFavorites = true, CancellationToken cancellationToken = default) => Run(() =>
    { Execute("DELETE FROM history WHERE $all=1 OR favorite=0", ("$all", includeFavorites ? 1 : 0)); SweepImages(); Execute("PRAGMA wal_checkpoint(TRUNCATE)"); return true; }, cancellationToken);
    public Task PruneAsync(CancellationToken cancellationToken = default) => Run(() => { PruneCore(); return true; }, cancellationToken);

    private static void ValidateRetention(HistoryRetention retention)
    {
        if (retention.MaxEntries < 0 || retention.MaxDays is < 1 or > 30 || retention.MaxStoredBytes is < 1 or > 512L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(retention));
    }
    public Task UpdateRetentionAsync(HistoryRetention retention, CancellationToken cancellationToken = default) => Run(() =>
    {
        ArgumentNullException.ThrowIfNull(retention); ValidateRetention(retention);
        var previous = _retention; _retention = retention;
        try { PruneCore(); } catch { _retention = previous; throw; }
        return true;
    }, cancellationToken);

    public string GetSaveDirectory(Guid id)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var entry = Find(id) ?? throw new KeyNotFoundException("History entry no longer exists.");
            if (entry.Kind != "files" || entry.SavedPaths != null) throw new InvalidOperationException("Entry cannot accept a saved copy.");
            string directory = Path.Combine(_copies, id.ToString("N"), "files");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }
    public Task AttachSavedFilesAsync(Guid id, string[] paths, long totalBytes, CancellationToken cancellationToken = default)
    {
        string[] snapshot = paths.Select(Path.GetFullPath).ToArray();
        return Run(() =>
        {
            var entry = Find(id) ?? throw new KeyNotFoundException("History entry no longer exists.");
            if (entry.Kind != "files" || entry.SavedPaths != null || snapshot.Length == 0 || totalBytes < 0) throw new ArgumentException("Invalid saved file attachment.");
            string root = Path.Combine(_copies, id.ToString("N"), "files");
            string prefix = root + Path.DirectorySeparatorChar;
            if ((File.GetAttributes(_copies) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(Path.GetDirectoryName(root)!) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked history storage is not supported.");
            long actual = 0;
            int visited = 0;
            void Check(string path, int depth = 0)
            {
                if (++visited > 10000 || depth > 128) throw new IOException("Saved copy contains too many entries.");
                if (!Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Saved files must be inside history storage.");
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked saved files are not supported.");
                if ((attributes & FileAttributes.Directory) != 0) foreach (string child in Directory.EnumerateFileSystemEntries(path)) Check(child, depth + 1);
                else actual = checked(actual + new FileInfo(path).Length);
                if (actual > _retention.MaxStoredBytes) throw new IOException("Saved copy exceeds history capacity.");
            }
            foreach (string path in snapshot) Check(path);
            if (actual != totalBytes) throw new IOException("Saved copy size changed.");
            using var transaction = _db.BeginTransaction();
            using (var command = Command("UPDATE history SET saved=$p,bytes=bytes+$b WHERE id=$id", ("$p", JsonSerializer.Serialize(snapshot)), ("$b", actual), ("$id", id.ToString())))
            { command.Transaction = transaction; command.ExecuteNonQuery(); }
            Trim(transaction, id); transaction.Commit();
            SweepImages(); return true;
        }, cancellationToken);
    }

    private HistoryEntry AddCore(HistoryCapture c)
    {
        if (c.Id == Guid.Empty || c.SourceMachine == Guid.Empty || string.IsNullOrWhiteSpace(c.SourceName) || c.SourceName.Length > 256) throw new ArgumentException("Invalid history identity.");
        if (c.Kind is not ("text" or "image" or "files")) throw new ArgumentException("Unsupported history kind.");
        if (c.Kind == "text" && (c.Text == null || c.Text.Length > 262144 || c.Text.Contains('\0') || c.Png != null || c.Paths != null)) throw new ArgumentException("Invalid history text.");
        if (c.Kind == "image" && (c.Png == null || c.Png.Length is < 8 or > 33554432 || !c.Png.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}) || c.Text != null || c.Paths != null)) throw new ArgumentException("Invalid history image.");
        if (c.Kind == "files" && (c.Text != null || c.Png != null || (c.Paths == null && c.OfferId == null) || c.OfferId == Guid.Empty || c.Paths?.Length > 10000 || c.Paths?.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 32767 || p.Contains('\0')) == true || c.FileSummary?.Length > 262144)) throw new ArgumentException("Invalid history file reference.");
        string? paths = c.Paths == null ? null : JsonSerializer.Serialize(c.Paths);
        if (paths?.Length > 2 * 1024 * 1024) throw new ArgumentException("History file reference is too large.");
        byte[] content = c.Kind == "image" ? c.Png! : Encoding.UTF8.GetBytes(c.Kind == "text" ? c.Text! : JsonSerializer.Serialize(new { c.SourceMachine, c.Paths, c.OfferId, c.FileSummary }));
        string digest = c.Kind + ":" + Convert.ToHexString(SHA256.HashData(content));
        HistoryEntry? sameId = Find(c.Id);
        if (sameId != null)
        {
            using var identity = Command("SELECT digest FROM history WHERE id=$id", ("$id", c.Id.ToString()));
            if (!string.Equals(identity.ExecuteScalar() as string, digest, StringComparison.Ordinal)) throw new InvalidDataException("History identity was reused for different content.");
            return sameId;
        }
        using (var duplicate = Command("SELECT id FROM history WHERE digest=$d", ("$d", digest)))
        {
            if (duplicate.ExecuteScalar() is string existing)
            {
                Execute("UPDATE history SET copied=$now WHERE id=$id", ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("$id", existing));
                return Find(Guid.Parse(existing))!;
            }
        }
        PruneCore();
        long bytes = content.LongLength;
        if (bytes > _retention.MaxStoredBytes) throw new IOException("History storage capacity exceeded.");
        string? image = c.Kind == "image" ? digest[6..] + ".png" : null;
        string? temporary = null;
        try
        {
            if (image != null)
            {
                temporary = Path.Combine(_images, Guid.NewGuid().ToString("N") + ".tmp");
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(content); stream.Flush(true); }
                File.Move(temporary, ImagePath(image), true); temporary = null;
            }
            using var transaction = _db.BeginTransaction();
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using (var command = Command("INSERT INTO history(id,digest,kind,source,source_name,text,paths,offer,summary,created,copied,favorite,bytes,image,search) VALUES($id,$d,$k,$s,$sn,$t,$p,$o,$su,$now,$now,0,$b,$i,$search)",
                ("$id", c.Id.ToString()), ("$d", digest), ("$k", c.Kind), ("$s", c.SourceMachine.ToString()), ("$sn", c.SourceName), ("$t", c.Text), ("$p", paths), ("$o", c.OfferId?.ToString()), ("$su", c.FileSummary), ("$now", now), ("$b", bytes), ("$i", image), ("$search", string.Join("\n", c.SourceName, c.Text, paths, c.FileSummary))))
            { command.Transaction = transaction; command.ExecuteNonQuery(); }
            Trim(transaction, c.Id);
            transaction.Commit();
        }
        finally { if (temporary != null) File.Delete(temporary); SweepImages(); }
        return Find(c.Id)!;
    }

    private void PruneCore()
    {
        using (var transaction = _db.BeginTransaction()) { Trim(transaction, null); transaction.Commit(); }
        SweepImages();
    }
    private void Trim(SqliteTransaction transaction, Guid? protect)
    {
        using (var command = Command("DELETE FROM history WHERE favorite=0 AND copied<$cut", ("$cut", DateTimeOffset.UtcNow.AddDays(-_retention.MaxDays).ToUnixTimeMilliseconds())))
        { command.Transaction = transaction; command.ExecuteNonQuery(); }
        while (true)
        {
            using var count = Command("SELECT COUNT(*),COALESCE(SUM(bytes),0) FROM history"); count.Transaction = transaction;
            long entries, bytes;
            using (var reader = count.ExecuteReader()) { reader.Read(); entries = reader.GetInt64(0); bytes = reader.GetInt64(1); }
            if ((_retention.MaxEntries == 0 || entries <= _retention.MaxEntries) && bytes <= _retention.MaxStoredBytes) break;
            using var remove = Command("DELETE FROM history WHERE id=(SELECT id FROM history WHERE favorite=0 AND id<>$p ORDER BY copied,id LIMIT 1)", ("$p", protect?.ToString() ?? ""));
            remove.Transaction = transaction;
            if (remove.ExecuteNonQuery() == 0) throw new IOException("History capacity is occupied by favorites; remove records before copying more.");
        }
    }
    private void RecoverUnattachedCopies()
    {
        if ((File.GetAttributes(_copies) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked history storage is not supported.");
        var pending = new List<Guid>();
        using (var command = Command("SELECT id FROM history WHERE saved IS NULL"))
        using (var reader = command.ExecuteReader()) while (reader.Read()) pending.Add(Guid.Parse(reader.GetString(0)));
        foreach (Guid id in pending)
        {
            string owner = Path.Combine(_copies, id.ToString("N"));
            if (!Directory.Exists(owner)) continue;
            if ((File.GetAttributes(owner) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked history storage is not supported.");
            string files = Path.Combine(owner, "files");
            if (!Directory.Exists(files)) continue;
            int visited = 0;
            void Validate(string path, int depth)
            {
                if (++visited > 10000 || depth > 128) throw new IOException("Unfinished history copy contains too many entries.");
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked unfinished history copy is not supported.");
                if ((attributes & FileAttributes.Directory) != 0)
                    foreach (string child in Directory.EnumerateFileSystemEntries(path)) Validate(child, depth + 1);
            }
            // Only startup may remove uncommitted copies; runtime sweeps must not race a live download.
            Validate(files, 0);
            Directory.Delete(files, true);
        }
    }

    private void SweepImages()
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var command = Command("SELECT image FROM history WHERE image IS NOT NULL"))
        using (var reader = command.ExecuteReader()) while (reader.Read()) referenced.Add(reader.GetString(0));
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var command = Command("SELECT id FROM history"))
        using (var reader = command.ExecuteReader()) while (reader.Read()) ids.Add(Guid.Parse(reader.GetString(0)).ToString("N"));
        foreach (string directory in Directory.EnumerateDirectories(_copies))
            if (Guid.TryParseExact(Path.GetFileName(directory), "N", out _) && !ids.Contains(Path.GetFileName(directory)))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked history copy directory is not supported.");
                Directory.Delete(directory, true);
            }
        foreach (string path in Directory.EnumerateFiles(_images))
            if ((Path.GetExtension(path) is ".png" or ".tmp") && !referenced.Contains(Path.GetFileName(path))) File.Delete(path);
    }
    private string ImagePath(string name)
    {
        if (name.Length != 68 || !name.EndsWith(".png", StringComparison.Ordinal) || name[..64].Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Invalid history image identity.");
        return Path.Combine(_images, name);
    }
    private HistoryEntry? Find(Guid id)
    {
        using var command = Command("SELECT * FROM history WHERE id=$id", ("$id", id.ToString()));
        using var reader = command.ExecuteReader(); return reader.Read() ? Read(reader) : null;
    }
    private static HistoryEntry Read(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)), r.GetString(2), Guid.Parse(r.GetString(3)), r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : JsonSerializer.Deserialize<string[]>(r.GetString(6)), r.IsDBNull(7) ? null : Guid.Parse(r.GetString(7)), r.IsDBNull(8) ? null : r.GetString(8),
        DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(9)), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(10)), r.GetInt64(11) != 0, r.GetInt64(12), r.IsDBNull(15) ? null : JsonSerializer.Deserialize<string[]>(r.GetString(15)));
    private SqliteCommand Command(string sql, params (string Key, object? Value)[] parameters)
    {
        var command = _db.CreateCommand(); command.CommandText = sql;
        foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }
    private int Execute(string sql, params (string Key, object? Value)[] parameters)
    { using var command = Command(sql, parameters); return command.ExecuteNonQuery(); }
    private Task<T> Run<T>(Func<T> action, CancellationToken token) => Task.Run(() =>
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested(); return action(); }
    }, token);
    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; _db.Dispose(); } }
}
