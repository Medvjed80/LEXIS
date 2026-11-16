using AleappReader.Core;

namespace AleappReader.Core.Tests;

public class TranscriptNoteTests
{
    [Fact]
    public void A_transcript_is_the_note_until_the_examiner_writes_one()
    {
        var dir = Directory.CreateTempSubdirectory("lexis-notes-");
        try
        {
            var tags = new TagStore(dir.FullName);
            tags.WriteFileTranscript(@"sdcard\Voice Messages\a.opus", "  hello there  ");
            var table = TagStore.FilePrefix + "sdcard/Voice Messages/a.opus";
            Assert.Equal("Transcript (automatic):\nhello there", tags.NoteOf(table, 0));
            Assert.Empty(tags.All()); // a transcript is not a tagged item
            Assert.Empty(tags.FileTags());
            Assert.Equal("Transcript (automatic):\nhello there", tags.Rows().Single().Note);

            tags.WriteFileTranscript("sdcard/Voice Messages/a.opus", "hello again");
            Assert.Equal("Transcript (automatic):\nhello again", tags.NoteOf(table, 0));

            tags.SetFiles(["sdcard/Voice Messages/a.opus"], "Evidence"); // tagging keeps the note
            tags.WriteFileTranscript("sdcard/Voice Messages/a.opus", "third take");
            var entry = tags.All().Single();
            Assert.Equal("Evidence", entry.Tag);
            Assert.Equal("Transcript (automatic):\nthird take", entry.Note);
            Assert.Equal("third take", tags.FileNotes()["SDCARD/voice messages/a.opus"].Split('\n')[^1]);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void An_examiner_note_is_kept_and_the_transcript_is_appended()
    {
        Assert.Equal("Transcript (automatic):\nhello", TagStore.MergeTranscript(null, "hello"));
        Assert.Equal("Transcript (automatic):\nhello", TagStore.MergeTranscript("   ", "hello"));
        Assert.Equal("check the sender\n\nTranscript (automatic):\nhello", TagStore.MergeTranscript("check the sender", "hello"));
        Assert.Equal("check the sender\n\nTranscript (automatic):\nrevised",
            TagStore.MergeTranscript("check the sender\n\nTranscript (automatic):\nold words\n\nLanguage: it", "revised"));
        Assert.Equal("Transcript (automatic):\nrevised", TagStore.MergeTranscript("Transcript (automatic):\nold words", "revised"));
    }

    [Fact]
    public void A_blank_transcript_does_not_touch_the_note()
    {
        var dir = Directory.CreateTempSubdirectory("lexis-notes-");
        try
        {
            var tags = new TagStore(dir.FullName);
            tags.SetFiles(["a.mp3"], "Evidence");
            tags.SetNote(TagStore.FilePrefix + "a.mp3", 0, "leave this");
            tags.WriteFileTranscript("a.mp3", "  ");
            Assert.Equal("leave this", tags.NoteOf(TagStore.FilePrefix + "a.mp3", 0));
            Assert.Equal("Evidence", tags.All().Single().Tag);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Whisper_json_reads_the_text_the_language_and_the_offset()
    {
        var t = WhisperCpp.ReadTranscript("""
            {"result":{"language":"en"},"transcription":[{"text":" hi ","offsets":{"from":1200}},{"text":" "},{"text":"there","offsets":{"from":1800}}]}
            """);
        Assert.Equal(("hi there", "en"), (t.Text, t.Language));
        Assert.Equal([(TimeSpan.FromMilliseconds(1200), "hi"), (TimeSpan.FromMilliseconds(1800), "there")], t.Segments);
    }

    static string GalleryKey(MediaRef m) => TagStore.FileKey(m.SourcePath ?? m.ExtractionPath ?? m.Name);

    [Fact]
    public void The_same_file_key_reaches_the_gallery_the_extraction_and_a_chat_attachment()
    {
        var both = new MediaRef("ref1", "a.opus", "audio/ogg", "/data/a.opus", "media/aaa.opus", null, "chat", "Chat");
        var keys = TranscriptNotes.Keys(both);
        Assert.Equal(["media/aaa.opus", "data/a.opus"], keys);
        Assert.Contains(GalleryKey(both), keys);
        Assert.DoesNotContain("a.opus", keys); // the bare name would collide with another voice note

        var noSource = both with { SourcePath = null };
        Assert.Equal(["media/aaa.opus"], TranscriptNotes.Keys(noSource));
        Assert.Equal("media/aaa.opus", GalleryKey(noSource));

        var nameOnly = new MediaRef("ref9", "voice.opus", "audio/ogg", null, null, null, "", "");
        Assert.Equal(["voice.opus"], TranscriptNotes.Keys(nameOnly));
        Assert.Equal(GalleryKey(nameOnly), TranscriptNotes.Keys(nameOnly).Single());

        var climb = new MediaRef("ref3", "win.ini", null, "/data/ok.opus", "../../../../Windows/win.ini", null, "", "");
        Assert.Equal(["data/ok.opus"], TranscriptNotes.Keys(climb));
        Assert.DoesNotContain(TranscriptNotes.Keys(climb), k => k.Split('/').Contains(".."));
    }

    [Fact]
    public void A_saved_transcription_is_copied_into_every_path_of_the_file_and_a_second_copy_changes_nothing()
    {
        var dir = Directory.CreateTempSubdirectory("lexis-notes-");
        try
        {
            var tags = new TagStore(dir.FullName);
            var store = new AiStore(Path.Combine(dir.FullName, "_reader_ai.db"));
            var whisper = new AiProvider("whisper.cpp", AiKind.WhisperCpp, "whisper-cli.exe");
            var file = new MediaRef("ref1", "a.opus", "audio/ogg", "/data/a.opus", "media/aaa.opus", null, "chat", "Chat");
            var cite = AiText.Key("media", "ref1");
            store.Save(whisper, "ggml", "Transcription", "Speech to text", "a.opus " + cite, "hello there\n\nLanguage: en", [cite], [], 0);
            tags.SetNote(TagStore.FilePrefix + "data/a.opus", 0, "check the sender");
            Assert.Equal("check the sender", tags.NoteOf(TagStore.FilePrefix + "data/a.opus", 0));

            var media = new Dictionary<string, MediaRef> { ["ref1"] = file };
            Assert.Equal(2, TranscriptNotes.Apply(tags, store, media));
            var notes = tags.FileNotes();
            Assert.Equal("check the sender\n\nTranscript (automatic):\nhello there\n\nLanguage: en", notes["data/a.opus"]);
            Assert.Equal(notes["data/a.opus"], notes["media/aaa.opus"]);
            Assert.Equal(notes["data/a.opus"], TranscriptNotes.Lookup(notes, file));
            // a chat attachment with no source path, or a different one, still finds that note
            Assert.Equal(notes["media/aaa.opus"], TranscriptNotes.Lookup(notes, file with { SourcePath = null }));
            Assert.Equal(notes["media/aaa.opus"], TranscriptNotes.Lookup(notes, file with { SourcePath = "/sdcard/other/a.opus" }));
            Assert.Equal(0, TranscriptNotes.Apply(tags, store, media));

            var newer = store.Save(whisper, "ggml", "Transcription", "Speech to text", "a.opus " + cite, "goodbye\n\nLanguage: en", [cite], [], 0);
            Assert.Equal(2, TranscriptNotes.Apply(tags, store, media));
            Assert.Equal("check the sender\n\nTranscript (automatic):\ngoodbye\n\nLanguage: en", tags.FileNotes()["data/a.opus"]);
            store.Revise(newer.Id, AiStore.Edit, "the words I heard");
            Assert.Equal(2, TranscriptNotes.Apply(tags, store, media));
            Assert.EndsWith("the words I heard", tags.FileNotes()["media/aaa.opus"].Replace("\r\n", "\n"));
            Assert.Equal(0, TranscriptNotes.Apply(tags, store, media));

            store.Revise(newer.Id, AiStore.Exclude);
            var other = new MediaRef("ref2", "b.opus", "audio/ogg", null, "media/bbb.opus", null, "", "");
            store.Save(whisper, "ggml", "Transcription", "Speech to text", "b.opus " + AiText.Key("media", "ref2"),
                "should not be written", [AiText.Key("media", "ref2")], [], 0);
            // the excluded result is the newest for ref1, so its note is left as the examiner last had it; ref2 is excluded below
            store.Revise(store.Results("Transcription").First(r => r.Cited.Contains(AiText.Key("media", "ref2"))).Id, AiStore.Exclude);
            var kept = tags.FileNotes()["data/a.opus"];
            Assert.Equal(0, TranscriptNotes.Apply(tags, store, new Dictionary<string, MediaRef> { ["ref1"] = file, ["ref2"] = other }));
            Assert.Equal(kept, tags.FileNotes()["data/a.opus"]);
            Assert.False(tags.FileNotes().ContainsKey("media/bbb.opus"));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Ffmpeg_is_told_the_wav_and_jpeg_formats_the_bundled_build_has()
    {
        var wav = WhisperCpp.WavArguments("in.opus", "out.preview.wav.1.tmp");
        var format = wav.ToList().IndexOf("-f");
        Assert.Equal("wav", wav[format + 1]);
        Assert.Equal("pcm_s16le", wav[wav.ToList().IndexOf("-c:a") + 1]);
        Assert.Equal("out.preview.wav.1.tmp", wav[^1]); // the output stays last, and -f names the muxer whatever the extension is

        var jpeg = VideoFrames.JpegArguments("clip.mp4", TimeSpan.FromSeconds(1.5), "frame_000.jpg");
        Assert.Equal("image2", jpeg[jpeg.ToList().IndexOf("-f") + 1]);
        Assert.Equal("mjpeg", jpeg[jpeg.ToList().IndexOf("-c:v") + 1]);
        Assert.Equal("frame_000.jpg", jpeg[^1]);
    }

    [Fact]
    public void The_per_file_path_does_not_call_Apply()
    {
        var dir = Directory.CreateTempSubdirectory("lexis-notes-");
        try
        {
            var tags = new TagStore(dir.FullName);
            var store = new AiStore(Path.Combine(dir.FullName, "_reader_ai.db"));
            var whisper = new AiProvider("whisper.cpp", AiKind.WhisperCpp, "whisper-cli.exe");
            foreach (var id in new[] { "a", "b", "c" })
            {
                var cite = AiText.Key("media", id);
                store.Save(whisper, "ggml", "Transcription", "Speech to text", id + " " + cite, "words " + id, [cite], [], 0);
            }
            var file = new MediaRef("b", "b.opus", "audio/ogg", null, "media/b.opus", null, "", "");
            var runs = TranscriptNotes.ApplyRuns;
            Assert.Equal(1, TranscriptNotes.WriteSaved(tags, store, file));
            Assert.Equal(runs, TranscriptNotes.ApplyRuns);
            Assert.Equal("Transcript (automatic):\nwords b", tags.FileNotes()["media/b.opus"]);
            Assert.False(tags.FileNotes().ContainsKey("media/a.opus"));
            Assert.False(tags.FileNotes().ContainsKey("media/c.opus"));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Apply_copies_five_thousand_transcriptions_in_one_pass()
    {
        var dir = Directory.CreateTempSubdirectory("lexis-notes-");
        try
        {
            var tags = new TagStore(dir.FullName);
            var store = new AiStore(Path.Combine(dir.FullName, "_reader_ai.db"));
            var whisper = new AiProvider("whisper.cpp", AiKind.WhisperCpp, "whisper-cli.exe");
            const int n = 5000;
            var cite0 = AiText.Key("media", "m0");
            store.Save(whisper, "ggml", "Transcription", "Speech to text", "m0 " + cite0, "words 0", [cite0], [], 0);
            using (var con = new Microsoft.Data.Sqlite.SqliteConnection(
                       new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = store.Path, Pooling = false }.ToString()))
            {
                con.Open();
                using var tx = con.BeginTransaction();
                using var cmd = con.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO results(at,kind,provider,location,model,prompt,scope,output,cited,unknown,send_id)
                    VALUES(0,'Transcription','whisper.cpp','local','ggml','Speech to text',@s,@o,@c,'[]',0)
                    """;
                var scope = cmd.Parameters.Add("@s", Microsoft.Data.Sqlite.SqliteType.Text);
                var output = cmd.Parameters.Add("@o", Microsoft.Data.Sqlite.SqliteType.Text);
                var cited = cmd.Parameters.Add("@c", Microsoft.Data.Sqlite.SqliteType.Text);
                for (var i = 1; i < n; i++)
                {
                    var cite = AiText.Key("media", "m" + i);
                    scope.Value = "m" + i + " " + cite;
                    output.Value = "words " + i;
                    cited.Value = System.Text.Json.JsonSerializer.Serialize(new[] { cite });
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            var media = new Dictionary<string, MediaRef>(n);
            for (var i = 0; i < n; i++)
                media["m" + i] = new MediaRef("m" + i, "m" + i + ".opus", "audio/ogg", null, "media/m" + i + ".opus", null, "", "");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var changed = TranscriptNotes.Apply(tags, store, media);
            sw.Stop();
            Assert.Equal(n, changed);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"Apply took {sw.Elapsed.TotalSeconds:0.0}s");
            Assert.Equal("Transcript (automatic):\nwords 0", tags.FileNotes()["media/m0.opus"]);
            Assert.Equal("Transcript (automatic):\nwords 4999", tags.FileNotes()["media/m4999.opus"]);

            var again = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal(0, TranscriptNotes.Apply(tags, store, media));
            again.Stop();
            Assert.True(again.Elapsed < TimeSpan.FromSeconds(10), $"second Apply took {again.Elapsed.TotalSeconds:0.0}s");
        }
        finally { dir.Delete(true); }
    }

    [Theory]
    [InlineData("voice.mp3", true)]
    [InlineData("voice.M4A", true)]
    [InlineData("voice.wav", true)]
    [InlineData("voice.aac", true)]
    [InlineData("voice.wma", true)]
    [InlineData("voice.opus", false)]
    [InlineData("voice.amr", false)]
    [InlineData("voice.ogg", false)]
    [InlineData("voice.silk", false)]
    public void The_player_converts_only_what_it_cannot_read(string name, bool native) =>
        Assert.Equal(native, AudioPlayback.PlaysNatively(name));

    [Fact]
    public void An_automatic_transcript_is_not_a_tagged_item_unless_the_examiner_tags_or_notes_the_file()
    {
        var dir = Directory.CreateTempSubdirectory("lexis-notes-");
        try
        {
            var tags = new TagStore(dir.FullName);
            tags.WriteFileTranscript("media/a.opus", "hello <script> &");
            var table = TagStore.FilePrefix + "media/a.opus";
            Assert.Equal("Transcript (automatic):\nhello <script> &", tags.NoteOf(table, 0));
            Assert.Empty(tags.All());
            Assert.Empty(tags.FileTags());
            Assert.False(TagStore.IsTagged(tags.Rows().Single()));
            Assert.Equal("hello <script> &", TranscriptNotes.Body(tags.NoteOf(table, 0)!));

            // a note the examiner writes themselves counts as tagged, even without a tag label
            tags.SetNote(table, 0, "listen again\n\nTranscript (automatic):\nhello <script> &");
            var noted = Assert.Single(tags.All());
            Assert.Equal("", noted.Tag);
            Assert.Equal("listen again", TranscriptNotes.ExaminerText(noted.Note));
            Assert.True(tags.FileTags().ContainsKey("media/a.opus"));

            // an explicit tag on a file that only had a transcript also counts, and the transcript stays
            tags.SetNote(TagStore.FilePrefix + "media/b.opus", 0, "Transcript (automatic):\nother");
            Assert.Single(tags.All());
            tags.SetFiles(["media/b.opus"], "Evidence");
            Assert.Equal(2, tags.All().Count);
            Assert.Equal("Evidence", tags.FileTags()["media/b.opus"]);
            Assert.Equal("Transcript (automatic):\nother", tags.NoteOf(TagStore.FilePrefix + "media/b.opus", 0));
        }
        finally { dir.Delete(true); }
    }
}
