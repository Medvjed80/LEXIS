using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AleappReader.Core.Tests;

/// <summary>Analysis state export/import on synthetic parsings: an "old" report and a "new" report of the same
/// extraction made by a newer parser (an extra column, a renamed column, more rows, a new artifact, rows in another order).</summary>
public sealed class AnalysisStateTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "AleappReaderTests", "state-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(_root, true); } catch (IOException) { } }

    static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];
    static readonly byte[] Other = [0xFF, 0xD8, 0xFF, 0xE0, 9, 9, 9, 9];

    string Extraction(string name, params (string Path, byte[] Bytes)[] files)
    {
        var dir = Path.Combine(_root, name);
        foreach (var (p, b) in files)
        {
            var full = Path.Combine(dir, p);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, b);
        }
        return dir;
    }

    string Report(string name, string extraction, string artifactsJson, string sql)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "_lava_data.lava"),
            "{\"parser_info\": {\"leapp_name\": \"ALEAPP\", \"leapp_version\": \"" + name + "\"}, \"processing_status\": \"Complete\"," +
            "\"param_input\": " + JsonSerializer.Serialize(extraction) + ", \"lava_db_name\": \"_lava_artifacts.db\", \"modules\": []," +
            "\"artifacts\": {" + artifactsJson + "}}");
        using var con = new SqliteConnection($"Data Source={Path.Combine(dir, "_lava_artifacts.db")};Pooling=False");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        return dir;
    }

    const string ChatOld = """
        "Messages": [{"name": "Chat Messages", "tablename": "chat", "module": "chat", "source_path": "Dump/data/data/com.x/databases/msg.db",
          "column_map": {"ts": "Timestamp", "thread": "Thread ID", "body": "Body", "sender": "Sender", "src": "Source File"},
          "object_columns": [{"name": "ts", "type": "datetime"}]}],
        "Contacts": [{"name": "Contacts", "tablename": "contacts", "module": "contacts",
          "column_map": {"display_name": "Display Name", "phone": "Phone Number", "ts": "Created"},
          "object_columns": [{"name": "ts", "type": "datetime"}]}],
        "Dropped": [{"name": "Old Only", "tablename": "old_only", "module": "x", "column_map": {"v": "Value"}}]
        """;

    // A newer parser: an extra column (Sender Name), Source File recorded under another machine's temp folder, rows in
    // another order plus new ones; Contacts has its date column renamed; old_only is gone; a new artifact appears.
    const string ChatNew = """
        "Messages": [{"name": "Chat Messages", "tablename": "chat", "module": "chat", "source_path": "Dump/data/data/com.x/databases/msg.db",
          "column_map": {"ts": "Timestamp", "thread": "Thread ID", "body": "Body", "sender": "Sender", "sender_name": "Sender Name", "src": "Source File"},
          "object_columns": [{"name": "ts", "type": "datetime"}]}],
        "Contacts": [{"name": "Contacts", "tablename": "contacts", "module": "contacts",
          "column_map": {"display_name": "Display Name", "phone": "Phone Number", "ts": "Creation Time (UTC)"},
          "object_columns": [{"name": "ts", "type": "datetime"}]}],
        "Calls": [{"name": "Call Log", "tablename": "calls", "module": "calls", "column_map": {"n": "Number"}}]
        """;

    (string Old, string New) Build()
    {
        var src = Extraction("src", ("Dump/data/data/com.x/databases/msg.db", "sqlite source"u8.ToArray()));
        var oldX = Extraction("x-old", ("Dump/data/data/com.x/databases/msg.db", "sqlite source"u8.ToArray()),
            ("Dump/sdcard/DCIM/a.jpg", Jpeg), ("Dump/sdcard/DCIM/b.jpg", Jpeg), ("Dump/sdcard/x.jpg", Other), ("Dump/sdcard/gone.txt", "bye"u8.ToArray()));
        var newX = Extraction("x-new", ("Dump/data/data/com.x/databases/msg.db", "sqlite source"u8.ToArray()),
            ("Dump/sdcard/DCIM/a.jpg", Jpeg), ("Dump/sdcard/DCIM/b.jpg", Jpeg), ("Dump/sdcard/moved1/x.jpg", Other), ("Dump/sdcard/moved2/x.jpg", Other),
            ("Dump/sdcard/new1.txt", "n1"u8.ToArray()), ("Dump/sdcard/new2.txt", "n2"u8.ToArray()));
        _ = src;
        var old = Report("old", oldX, ChatOld, """
            CREATE TABLE chat (ts INTEGER, thread TEXT, body TEXT, sender TEXT, src TEXT);
            INSERT INTO chat VALUES (1714557600,'1','Hello secret body','+391111','C:\Temp\run1\Dump\data\data\com.x\databases\msg.db');
            INSERT INTO chat VALUES (1714557660,'1','Second','+392222','C:\Temp\run1\Dump\data\data\com.x\databases\msg.db');
            INSERT INTO chat VALUES (1714557720,'2','Dup me','+393333','C:\Temp\run1\Dump\data\data\com.x\databases\msg.db');
            INSERT INTO chat VALUES (1714557780,'2','Deleted later','+394444','C:\Temp\run1\Dump\data\data\com.x\databases\msg.db');
            CREATE TABLE contacts (display_name TEXT, phone TEXT, ts TEXT);
            INSERT INTO contacts VALUES ('Anna Example','3331112222','2024-05-01 10:00:00');
            INSERT INTO contacts VALUES ('Bruno Example','3334445555','2024-05-02 10:00:00');
            CREATE TABLE old_only (v TEXT);
            INSERT INTO old_only VALUES ('x');
            """);
        var neu = Report("new", newX, ChatNew, """
            CREATE TABLE chat (ts INTEGER, thread TEXT, body TEXT, sender TEXT, sender_name TEXT, src TEXT);
            INSERT INTO chat VALUES (1714557500,'9','Recovered by the new parser','+395555','Eve','/tmp/run2/Dump/data/data/com.x/databases/msg.db');
            INSERT INTO chat VALUES (1714557660,'1','Second','+392222','Bob','/tmp/run2/Dump/data/data/com.x/databases/msg.db');
            INSERT INTO chat VALUES (1714557600,'1','Hello secret body','+391111','Al','/tmp/run2/Dump/data/data/com.x/databases/msg.db');
            INSERT INTO chat VALUES (1714557720,'2','Dup me','+393333',NULL,'/tmp/run2/Dump/data/data/com.x/databases/msg.db');
            INSERT INTO chat VALUES (1714557720,'2','Dup me','+393333',NULL,'/tmp/run2/Dump/data/data/com.x/databases/msg.db');
            INSERT INTO chat VALUES (1714557900,'3','Another new one','+396666','Zed','/tmp/run2/Dump/data/data/com.x/databases/msg.db');
            CREATE TABLE contacts (display_name TEXT, phone TEXT, ts TEXT);
            INSERT INTO contacts VALUES ('Bruno Example','3334445555','2024-05-02T10:00:00Z');
            INSERT INTO contacts VALUES ('Anna Example','3331112222','2024-05-01T10:00:00Z');
            INSERT INTO contacts VALUES ('Carla Example','3337778888','2024-05-03T10:00:00Z');
            CREATE TABLE calls (n TEXT);
            INSERT INTO calls VALUES ('123'); INSERT INTO calls VALUES ('456');
            """);
        return (old, neu);
    }

    [Fact]
    public void Export_then_import_into_a_newer_parsing_matches_by_hash_and_reports_ambiguous_missing_and_new()
    {
        var (oldDir, newDir) = Build();
        string json;
        using (var s = CaseSession.Open(oldDir))
        {
            s.Tags.SetMany("chat", [1, 2, 3, 4], "Evidence");
            s.Tags.Set("chat", 1, "Evidence", "note one");
            s.Tags.SetMany("contacts", [1], "Important");
            s.Tags.SetMany("old_only", [1], "Suspicious");
            s.Tags.SetFiles(["Dump/sdcard/DCIM/a.jpg", "Dump/sdcard/x.jpg", "Dump/sdcard/gone.txt"], "Evidence");
            var state = AnalysisState.Export(s, [new TagDefinition("Evidence", "#D93025"), new TagDefinition("Important")], "1.2.10");
            var path = Path.Combine(_root, "case" + AnalysisState.Extension);
            AnalysisState.Save(state, path);
            json = File.ReadAllText(path);
            Assert.Equal(1, state.SchemaVersion);
            Assert.Equal(9, state.Items.Count);
            Assert.Equal(5, state.Inventory.Files);
        }
        // Metadata and hashes only: no message text, no phone number, no file bytes, no examiner path.
        Assert.DoesNotContain("Hello secret body", json);
        Assert.DoesNotContain("3331112222", json);
        Assert.DoesNotContain("+391111", json);
        Assert.DoesNotContain(_root, json);
        Assert.Contains("\"note one\"", json);

        var loaded = AnalysisState.Parse(json);
        using var n = CaseSession.Open(newDir);
        n.Tags.Set("chat", 2, "Irrelevant", "examiner already tagged this in the new parsing");
        var plan = AnalysisState.Plan(n, loaded);
        string Rule(string artifact, long row) => plan.Matches.Single(m => m.Item.Record?.Table == artifact && m.Item.Record.RowId == row).Rule;
        MatchTier Tier(string artifact, long row) => plan.Matches.Single(m => m.Item.Record?.Table == artifact && m.Item.Record.RowId == row).Tier;
        ItemMatch File_(string path) => plan.Matches.Single(m => m.Item.File?.Path == path);

        // chat #1 "Hello", #2 "Second": an extra column and another source path prefix do not change the content hash.
        Assert.Equal(MatchTier.Hash, Tier("chat", 1));
        Assert.Equal([("chat", 3L)], plan.Matches.Single(m => m.Item.Record?.RowId == 1 && m.Item.Record.Table == "chat").Targets);
        Assert.Equal(MatchTier.Hash, Tier("chat", 2));
        // chat #3 "Dup me" is in the new parsing twice, neither at row 3: both tagged, flagged ambiguous.
        var dup = plan.Matches.Single(m => m.Item.Record?.Table == "chat" && m.Item.Record.RowId == 3);
        Assert.True(dup.Ambiguous);
        Assert.Equal([4L, 5L], dup.Targets.Select(t => t.RowId).Order().ToArray());
        // chat #4 is gone, old_only is gone.
        Assert.Equal(MatchTier.None, Tier("chat", 4));
        Assert.Equal(MatchTier.None, Tier("old_only", 1));
        Assert.Equal("artifact missing", Rule("old_only", 1));
        // Contacts: the date column was renamed and reformatted: the identity fields (name, phone) still match.
        Assert.Equal(MatchTier.Secondary, Tier("contacts", 1));
        Assert.Equal([("contacts", 2L)], plan.Matches.Single(m => m.Item.Record?.Table == "contacts").Targets);
        // Files: a.jpg by hash at the same path (b.jpg has the same bytes but a.jpg's path wins), x.jpg moved to two
        // folders (ambiguous), gone.txt not found.
        Assert.Equal(MatchTier.Hash, File_("Dump/sdcard/DCIM/a.jpg").Tier);
        Assert.False(File_("Dump/sdcard/DCIM/a.jpg").Ambiguous);
        Assert.Equal([("file:Dump/sdcard/DCIM/a.jpg", 0L)], File_("Dump/sdcard/DCIM/a.jpg").Targets);
        Assert.True(File_("Dump/sdcard/x.jpg").Ambiguous);
        Assert.Equal(2, File_("Dump/sdcard/x.jpg").Targets.Count);
        Assert.Equal(MatchTier.None, File_("Dump/sdcard/gone.txt").Tier);

        Assert.Equal(5, plan.ByHash);       // chat 1, 2, 3 (ambiguous), a.jpg, x.jpg (ambiguous)
        Assert.Equal(1, plan.BySecondary);  // contacts
        Assert.Equal(0, plan.ByPath);
        Assert.Equal(2, plan.Ambiguous);
        Assert.Equal(3, plan.NotFound);     // chat 4, old_only, gone.txt
        Assert.Equal(2 + 1 + 2, plan.NewRecords); // chat 4→6, contacts 2→3, calls (new artifact) 2
        Assert.Equal(1, plan.NewArtifacts);
        Assert.Equal(2, plan.NewFiles);     // 5 → 7 files (gone.txt went, x.jpg doubled, two new)

        AnalysisState.Apply(n, plan);
        var tags = n.Tags.All();
        TagEntry T(string table, long row) => tags.Single(t => t.TableName == table && t.RowId == row);
        Assert.Equal("Evidence", T("chat", 3).Tag);
        Assert.Equal("note one", T("chat", 3).Note);
        Assert.Equal("Evidence", T("chat", 4).Tag);
        Assert.Equal("Evidence", T("chat", 5).Tag);
        Assert.Equal("Important", T("contacts", 2).Tag);
        // Additive: the examiner's own tag in the new parsing stays, the imported one is noted beside it.
        Assert.Equal("Irrelevant", T("chat", 2).Tag);
        Assert.Contains("examiner already tagged", T("chat", 2).Note);
        Assert.Contains("Evidence", T("chat", 2).Note);
        Assert.Equal(1, plan.KeptExisting);
        Assert.Equal("Evidence", n.Tags.FileTags()["Dump/sdcard/DCIM/a.jpg"]);
        Assert.Equal("Evidence", n.Tags.FileTags()["Dump/sdcard/moved1/x.jpg"]);
        Assert.Equal("Evidence", n.Tags.FileTags()["Dump/sdcard/moved2/x.jpg"]);
        Assert.False(n.Tags.FileTags().ContainsKey("Dump/sdcard/DCIM/b.jpg"));
        Assert.DoesNotContain(tags, t => t.TableName == "chat" && t.RowId is 1 or 6); // new records stay untagged

        // Importing twice changes nothing more.
        var before = n.Tags.Rows().Select(t => (t.TableName, t.RowId, t.Tag, t.Note)).ToList();
        AnalysisState.Apply(n, AnalysisState.Plan(n, loaded));
        Assert.Equal(before, n.Tags.Rows().Select(t => (t.TableName, t.RowId, t.Tag, t.Note)).ToList());

        // The report lists the unmatched and ambiguous items, without content.
        var text = plan.ToText();
        Assert.Contains("Not found (3)", text);
        Assert.Contains("Ambiguous (2)", text);
        var csv = plan.ToCsv();
        Assert.Equal(10, csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain("Hello secret body", csv + text);
    }

    [Fact]
    public void Unchanged_source_and_row_id_match_when_only_identity_columns_were_renamed()
    {
        var x = Extraction("x", ("Dump/data/data/com.y/db", "same source"u8.ToArray()));
        var art = "\"Notes\": [{\"name\": \"Notes\", \"tablename\": \"notes\", \"module\": \"n\", \"source_path\": \"Dump/data/data/com.y/db\", " +
                  "\"column_map\": {\"title\": \"Title\", \"body\": \"Body\", \"acc\": \"%ACC%\"}}]";
        var oldDir = Report("o", x, art.Replace("%ACC%", "Account"), "CREATE TABLE notes (title TEXT, body TEXT, acc TEXT); INSERT INTO notes VALUES ('t1','b1','me');");
        var newDir = Report("n", x, art.Replace("%ACC%", "Account Name"), "CREATE TABLE notes (title TEXT, body TEXT, acc TEXT); INSERT INTO notes VALUES ('t1','b1','me (renamed column)');");
        AnalysisStateFile state;
        using (var s = CaseSession.Open(oldDir)) { s.Tags.SetMany("notes", [1], "Evidence"); state = AnalysisState.Export(s, [], "1.2.10"); }
        using var n = CaseSession.Open(newDir);
        var m = AnalysisState.Plan(n, state).Matches.Single();
        Assert.Equal(MatchTier.Secondary, m.Tier);
        Assert.StartsWith("row id + source SHA-256 unchanged, 2/3", m.Rule);
    }

    [Fact]
    public void The_schema_version_is_checked()
    {
        var ok = JsonSerializer.Serialize(new AnalysisStateFile { AppVersion = "1.2.10" });
        Assert.Equal(1, AnalysisState.Parse(ok).SchemaVersion);
        var newer = Assert.Throws<AnalysisStateException>(() => AnalysisState.Parse(ok.Replace("\"schemaVersion\":1", "\"schemaVersion\":2")));
        Assert.Contains("schema 2", newer.Message);
        Assert.Throws<AnalysisStateException>(() => AnalysisState.Parse(ok.Replace("\"schemaVersion\":1", "\"schemaVersion\":0")));
        Assert.Throws<AnalysisStateException>(() => AnalysisState.Parse(ok.Replace(AnalysisState.FormatName, "something-else")));
        Assert.Throws<AnalysisStateException>(() => AnalysisState.Parse("{\"format\":\"lexis-analysis-state\"}"));
        Assert.Throws<AnalysisStateException>(() => AnalysisState.Parse("not json"));
    }

    [Theory]
    [InlineData("C:\\Temp\\run1\\Dump\\data\\data\\com.x\\db", "/data/data/com.x/db")]
    [InlineData("/tmp/run2/Dump/data/data/com.x/db", "/data/data/com.x/db")]
    [InlineData("HomeDomain/Library/SMS/sms.db", "/HomeDomain/Library/SMS/sms.db")]
    [InlineData("plain", "plain")]
    public void Paths_are_compared_from_the_device_root(string value, string expected) => Assert.Equal(expected, AnalysisState.NormPath(value));

    [Theory]
    [InlineData(5L, "5")]
    [InlineData("5", "5")]
    [InlineData(5.0, "5")]
    [InlineData("+39333", "+39333")]
    [InlineData("0333", "0333")]
    [InlineData(" a\r\nb ", "a\nb")]
    public void Values_are_normalised(object v, string expected) => Assert.Equal(expected, AnalysisState.Norm(v));
}
