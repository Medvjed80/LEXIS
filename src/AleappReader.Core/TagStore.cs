using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace AleappReader.Core;

public sealed record TagEntry(string TableName, long RowId, string Tag, string Note, DateTimeOffset Created);

/// <summary>
/// Examiner tags and notes, kept in _reader_tags.db beside the manifest, never in the evidence database.
/// A read-only case folder falls back to %LOCALAPPDATA%\AleappReader\tags.
/// </summary>
public sealed class TagStore
{
    public const string FileName = "_reader_tags.db";
    public static readonly string[] DefaultTags = ["Evidence", "Important", "Suspicious", "Follow up", "Irrelevant"];
    public string Path { get; private init; }

    public TagStore(string caseRoot)
    {
        var local = System.IO.Path.Combine(caseRoot, FileName);
        Path = File.Exists(local) || CanWrite(caseRoot) ? local : FallbackPath(caseRoot);
    }

    TagStore() => Path = "";

    /// <summary>Tags of a case opened from a zip: kept outside the temp copy (deleted on close), seeded once from the tags
    /// shipped inside the package. Keyed by the package's content (<paramref name="contentRoot"/>, the SHA-256 of its
    /// SHA256SUMS.txt) so a moved or renamed package keeps them; by the zip's path when it has none.</summary>
    public static TagStore ForPackage(string zipPath, string extractedRoot, string? contentRoot = null)
    {
        var byPath = FallbackPath(System.IO.Path.GetFullPath(zipPath));
        var store = new TagStore { Path = contentRoot is null ? byPath : FallbackPath("content:" + contentRoot) };
        var shipped = System.IO.Path.Combine(extractedRoot, FileName);
        if (!File.Exists(store.Path) && (File.Exists(byPath) || File.Exists(shipped)))
            File.Copy(File.Exists(byPath) ? byPath : shipped, store.Path); // tags kept by path before they were kept by content
        return store;
    }

    static bool CanWrite(string dir)
    {
        try
        {
            var probe = System.IO.Path.Combine(dir, ".reader_probe_" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    static string FallbackPath(string caseRoot) => LocalPath("tags", caseRoot);

    /// <summary>%LOCALAPPDATA%\AleappReader\<paramref name="folder"/>, created when missing.</summary>
    public static string LocalFolder(string folder)
    {
        var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AleappReader", folder);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A per-case database under <see cref="LocalFolder"/>, named by a hash of <paramref name="key"/>.</summary>
    internal static string LocalPath(string folder, string key) =>
        System.IO.Path.Combine(LocalFolder(folder), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToLowerInvariant())))[..16] + ".db");

    SqliteConnection Open()
    {
        var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString());
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS tags(table_name TEXT NOT NULL, row_id INTEGER NOT NULL, tag TEXT NOT NULL,
              note TEXT NOT NULL DEFAULT '', created INTEGER NOT NULL, PRIMARY KEY(table_name, row_id))
            """;
        cmd.ExecuteNonQuery();
        return con;
    }

    /// <summary>Tags a record; a null note keeps the note it already has (tagging from a menu or shortcut).</summary>
    public void Set(string table, long rowId, string tag, string? note = null)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tags(table_name,row_id,tag,note,created) VALUES(@t,@r,@g,COALESCE(@n,''),@c)
            ON CONFLICT(table_name,row_id) DO UPDATE SET tag=excluded.tag, note=COALESCE(@n,note)
            """;
        cmd.Parameters.AddWithValue("@t", table);
        cmd.Parameters.AddWithValue("@r", rowId);
        cmd.Parameters.AddWithValue("@g", tag);
        cmd.Parameters.AddWithValue("@n", (object?)note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@c", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }

    /// <summary>Tags (or with null untags) many records of one table in one transaction, keeping their notes.</summary>
    public void SetMany(string table, IEnumerable<long> rowIds, string? tag)
    {
        if (tag is null && !File.Exists(Path)) return;
        using var con = Open();
        using var tx = con.BeginTransaction();
        using var cmd = con.CreateCommand();
        cmd.CommandText = tag is null ? "DELETE FROM tags WHERE table_name=@t AND row_id=@r" : """
            INSERT INTO tags(table_name,row_id,tag,note,created) VALUES(@t,@r,@g,'',@c)
            ON CONFLICT(table_name,row_id) DO UPDATE SET tag=excluded.tag
            """;
        cmd.Parameters.AddWithValue("@t", table);
        var r = cmd.Parameters.Add("@r", SqliteType.Integer);
        if (tag is not null)
        {
            cmd.Parameters.AddWithValue("@g", tag);
            cmd.Parameters.AddWithValue("@c", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }
        foreach (var id in rowIds) { r.Value = id; cmd.ExecuteNonQuery(); }
        tx.Commit();
    }

    public void Remove(string table, long rowId)
    {
        if (!File.Exists(Path)) return;
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM tags WHERE table_name=@t AND row_id=@r";
        cmd.Parameters.AddWithValue("@t", table);
        cmd.Parameters.AddWithValue("@r", rowId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Whether a tags-table row is a tag or a note the examiner wrote. An automatic transcript stored
    /// with an empty tag does not count: the text stays on the file, but the file is not a tagged item.</summary>
    public static bool IsTagged(TagEntry t) =>
        !string.IsNullOrWhiteSpace(t.Tag) || TranscriptNotes.ExaminerText(t.Note).Length > 0;

    /// <summary>The note with the automatic transcript removed, so an export can keep the examiner's text only.</summary>
    public static string WithoutTranscript(string note)
    {
        var head = TranscriptNotes.ExaminerText(note);
        return head;
    }

    /// <summary>Every row in the tags table, including notes that only hold an automatic transcript.</summary>
    public IReadOnlyList<TagEntry> Rows(string? table = null)
    {
        if (!File.Exists(Path)) return [];
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT table_name,row_id,tag,note,created FROM tags" +
                          (table is null ? "" : " WHERE table_name=@t") + " ORDER BY table_name,row_id";
        if (table is not null) cmd.Parameters.AddWithValue("@t", table);
        var list = new List<TagEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new TagEntry(r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetString(3),
                DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(4))));
        return list;
    }

    /// <summary>Tagged records and files: an explicit tag, or a note the examiner wrote. Automatic transcripts alone
    /// are omitted (existing cases included) without deleting their text.</summary>
    public IReadOnlyList<TagEntry> All(string? table = null) => [.. Rows(table).Where(IsTagged)];

    public Dictionary<long, TagEntry> ForTable(string table) => All(table).ToDictionary(t => t.RowId);

    /// <summary>Files are tagged by their path in the device (a gallery file no record names): kept as table
    /// "file:&lt;path&gt;", row 0, so counts, the Tagged view and its export see them with the records.</summary>
    public const string FilePrefix = "file:";

    /// <summary>A file's key: its path in the extraction with forward slashes and no leading one, as Extraction Files
    /// lists it, so the gallery and Extraction Files tag the same file alike.</summary>
    public static string FileKey(string path) => path.Replace('\\', '/').TrimStart('/');

    public void SetFiles(IEnumerable<string> paths, string? tag)
    {
        foreach (var p in paths)
        {
            var key = FileKey(p);
            if (key != p) SetMany(FilePrefix + p, [0], null); // a tag kept under the path as first written
            SetMany(FilePrefix + key, [0], tag);
        }
    }

    public Dictionary<string, string> FileTags()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Rows().Where(t => t.TableName.StartsWith(FilePrefix, StringComparison.Ordinal) && IsTagged(t)))
            d[FileKey(t.TableName[FilePrefix.Length..])] = t.Tag;
        return d;
    }

    /// <summary>Notes of tagged files, keyed like <see cref="FileTags"/>, skipping files with an empty note.</summary>
    public Dictionary<string, string> FileNotes()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Rows().Where(t => t.TableName.StartsWith(FilePrefix, StringComparison.Ordinal) && t.Note != ""))
            d[FileKey(t.TableName[FilePrefix.Length..])] = t.Note;
        return d;
    }

    /// <summary>The note of one row, or null when it has never been tagged.</summary>
    public string? NoteOf(string table, long rowId)
    {
        if (!File.Exists(Path)) return null;
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT note FROM tags WHERE table_name=@t AND row_id=@r";
        cmd.Parameters.AddWithValue("@t", table);
        cmd.Parameters.AddWithValue("@r", rowId);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Sets the note and leaves the tag as it is. A row that does not exist yet is stored with an empty tag,
    /// so a transcript is a note, not a tag the examiner chose.</summary>
    public void SetNote(string table, long rowId, string note)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tags(table_name,row_id,tag,note,created) VALUES(@t,@r,'',@n,@c)
            ON CONFLICT(table_name,row_id) DO UPDATE SET note=excluded.note
            """;
        cmd.Parameters.AddWithValue("@t", table);
        cmd.Parameters.AddWithValue("@r", rowId);
        cmd.Parameters.AddWithValue("@n", note);
        cmd.Parameters.AddWithValue("@c", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }

    /// <summary>The first line of a note written by automatic transcription. An examiner's own note never starts a
    /// replacement: it is kept, and a new transcript is appended under this line. A note that is only a previous
    /// automatic transcript (or has one at the end) has that part replaced.</summary>
    public const string AutoTranscriptHeader = "Transcript (automatic):";

    /// <summary>The note to store for a new transcript: the examiner's text, then the transcript. A previous automatic
    /// transcript is replaced; anything written before it stays.</summary>
    public static string MergeTranscript(string? existing, string transcript)
    {
        var block = AutoTranscriptHeader + "\n" + transcript.Trim();
        if (string.IsNullOrWhiteSpace(existing)) return block;
        var note = existing.Replace("\r\n", "\n").Trim();
        var at = note.IndexOf(AutoTranscriptHeader, StringComparison.Ordinal);
        if (at < 0) return note + "\n\n" + block;
        var head = note[..at].TrimEnd();
        return head.Length == 0 ? block : head + "\n\n" + block;
    }

    /// <summary>Writes a transcription into the note of a file (gallery and Extraction Files share <see cref="FileKey"/>).
    /// Does nothing for a blank transcript, or when the note already reads the same. Keeps an examiner's note and any tag
    /// the file already has. Returns whether the note was written.</summary>
    public bool WriteFileTranscript(string path, string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return false;
        var table = FilePrefix + FileKey(path);
        var existing = NoteOf(table, 0);
        var merged = MergeTranscript(existing, transcript);
        if (existing is not null && existing.Replace("\r\n", "\n").Trim() == merged) return false;
        SetNote(table, 0, merged);
        return true;
    }

    /// <summary>Writes many file notes in one transaction. An existing tag is left as it is. An empty list writes nothing.</summary>
    public void SetFileNotes(IReadOnlyList<(string Table, string Note)> rows)
    {
        if (rows.Count == 0) return;
        using var con = Open();
        using var tx = con.BeginTransaction();
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO tags(table_name,row_id,tag,note,created) VALUES(@t,0,'',@n,@c)
            ON CONFLICT(table_name,row_id) DO UPDATE SET note=excluded.note
            """;
        var table = cmd.Parameters.Add("@t", SqliteType.Text);
        var note = cmd.Parameters.Add("@n", SqliteType.Text);
        cmd.Parameters.AddWithValue("@c", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        foreach (var row in rows)
        {
            table.Value = row.Table;
            note.Value = row.Note;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
