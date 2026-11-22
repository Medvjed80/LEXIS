using System.IO.Compression;
using System.Text.Json.Nodes;

namespace AleappReader.Core.Tests;

/// <summary>1.2.11: the gallery's AI no longer crashes on a closed extraction zip, transcriptions can be deleted, the
/// gallery filters by size, and Ollama is asked without thinking and with a capped context. Synthetic data only.</summary>
public sealed class Release1211Tests : IDisposable
{
    readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("lexis-1211-");
    public void Dispose() { try { _dir.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    string Zip(int files = 3, int size = 4096)
    {
        var path = Path.Combine(_dir.FullName, $"x-{Guid.NewGuid():N}.zip");
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        for (var i = 0; i < files; i++)
        {
            using var s = z.CreateEntry($"sdcard/DCIM/img{i}.jpg").Open();
            s.Write(new byte[size].Select((_, j) => (byte)(i + j)).ToArray());
        }
        return path;
    }

    // ---- the zip race ----

    [Fact]
    public void A_closed_zip_extraction_reads_as_unreadable_not_as_ObjectDisposedException()
    {
        var x = ExtractionFiles.Open(Zip())!;
        Assert.NotNull(x.Read("sdcard/DCIM/img0.jpg", 1 << 20));
        x.Dispose();
        x.Dispose(); // twice is harmless
        Assert.True(x.IsDisposed);
        Assert.Null(x.CopyOut("sdcard/DCIM/img1.jpg"));
        Assert.Null(x.Read("sdcard/DCIM/img1.jpg", 1 << 20));
        var e = Assert.Throws<ExtractionClosedException>(() => x.Open("sdcard/DCIM/img1.jpg", s => s.ReadByte()));
        Assert.IsAssignableFrom<IOException>(e); // every reader that skips an unreadable file skips this one
        var failed = new List<string>();
        x.Each(["sdcard/DCIM/img0.jpg", "sdcard/DCIM/img2.jpg"], (_, _) => Assert.Fail("read after close"), failed: failed.Add);
        Assert.Equal(2, failed.Count);
        Assert.Equal(4096, x.SizeOf("sdcard/DCIM/img2.jpg")); // the index stays: sizes, names, media list
    }

    [Fact]
    public void A_copy_made_before_the_close_is_still_served()
    {
        var x = ExtractionFiles.Open(Zip())!;
        var copy = x.CopyOut("sdcard/DCIM/img0.jpg");
        Assert.NotNull(copy);
        x.Dispose();
        Assert.Equal(copy, x.CopyOut("sdcard/DCIM/img0.jpg"));
    }

    [Fact]
    public async Task Closing_while_other_threads_read_never_lets_ObjectDisposedException_out()
    {
        for (var round = 0; round < 10; round++)
        {
            var x = ExtractionFiles.Open(Zip(files: 8, size: 64 * 1024))!;
            var start = new ManualResetEventSlim();
            var errors = new List<Exception>();
            var readers = Enumerable.Range(0, 4).Select(t => Task.Run(() =>
            {
                start.Wait();
                for (var i = 0; i < 200; i++)
                    try
                    {
                        var rel = $"sdcard/DCIM/img{(i + t) % 8}.jpg";
                        x.Read(rel, 1 << 20);
                        x.Read(rel, s => s.ReadByte());
                        x.Open(rel, s => { s.CopyTo(Stream.Null); return 0; });
                    }
                    catch (ExtractionClosedException) { } // the expected, catchable outcome
                    catch (Exception e) { lock (errors) errors.Add(e); }
            })).ToList();
            start.Set();
            await Task.Delay(round % 3);
            x.Dispose();
            await Task.WhenAll(readers);
            Assert.Empty(errors);
        }
    }

    [Fact]
    public void A_folder_extraction_keeps_reading_after_dispose()
    {
        var root = Directory.CreateDirectory(Path.Combine(_dir.FullName, "folder"));
        File.WriteAllText(Path.Combine(root.FullName, "a.jpg"), "abc");
        var x = ExtractionFiles.Open(root.FullName)!;
        x.Dispose();
        Assert.Equal(3, x.Read("a.jpg", 100)!.Length);
    }

    // ---- deleting transcriptions ----

    static readonly AiProvider Whisper = new("whisper.cpp", AiKind.WhisperCpp, "whisper-cli.exe");
    static readonly AiProvider Server = new("speaches", AiKind.OpenAi, "http://localhost:8000/v1");

    static AiResult Transcribe(AiStore store, AiProvider p, string model, string refId, string text)
    {
        var send = store.LogSend(p, model, "Transcription", $"audio {refId}.opus");
        var r = store.Save(p, model, "Transcription", "Speech to text", $"{refId}.opus [media#{refId}]", text, [$"[media#{refId}]"], [], send);
        store.MarkDone(AiStore.TranscribeKey(p.Name, model, refId));
        return r;
    }

    [Fact]
    public void Deleting_transcriptions_removes_rows_revisions_and_done_keys_in_one_go_and_logs_it()
    {
        var store = new AiStore(Path.Combine(_dir.FullName, "_reader_ai.db"));
        var a = Transcribe(store, Whisper, "base", "r1", "first file");
        var b = Transcribe(store, Whisper, "base", "r2", "second file");
        var b2 = Transcribe(store, Server, "large", "r2", "second file, other engine");
        var summary = store.Save(Server, "large", "Summary", "p", "s", "a summary [media#r1]", ["[media#r1]"], [], 0);
        store.Revise(a.Id, AiStore.Edit, "first file, corrected");
        store.MarkDone("batch|something");

        var deleted = store.DeleteTranscriptions([a.Id, b.Id, summary.Id, 999_999]);

        Assert.Equal([b.Id, a.Id], deleted.Select(r => r.Id)); // newest first; the summary is not a transcription
        Assert.Equal([b2.Id], store.Transcriptions().Select(r => r.Id));
        Assert.Contains(store.Results(), r => r.Id == summary.Id);
        Assert.Empty(store.Revisions([a.Id]));
        var done = store.DoneKeys();
        Assert.DoesNotContain(AiStore.TranscribeKey("whisper.cpp", "base", "r1"), done); // r1 can be transcribed again
        Assert.DoesNotContain(AiStore.TranscribeKey("whisper.cpp", "base", "r2"), done);
        Assert.Contains(AiStore.TranscribeKey("speaches", "large", "r2"), done);        // its transcription stays
        Assert.Contains("batch|something", done);
        Assert.Equal(3, store.Sends().Count); // the send log is the record of what left: kept
        Assert.Equal(2, store.DeletedCount());
        Assert.Empty(store.DeleteTranscriptions([a.Id])); // already gone
        Assert.Empty(store.DeleteTranscriptions([]));
    }

    [Fact]
    public void A_deleted_file_is_transcribed_again_and_the_new_transcript_is_kept()
    {
        var store = new AiStore(Path.Combine(_dir.FullName, "_reader_ai.db"));
        var a = Transcribe(store, Whisper, "base", "r1", "old words");
        store.DeleteTranscriptions([a.Id]);
        Assert.DoesNotContain(AiStore.TranscribeKey("whisper.cpp", "base", "r1"), store.DoneKeys());
        var again = Transcribe(store, Whisper, "base", "r1", "new words");
        Assert.Equal("new words", store.Transcriptions().Single().Output);
        Assert.Contains(AiStore.TranscribeKey("whisper.cpp", "base", "r1"), store.DoneKeys());
        Assert.Equal(1, store.DeletedCount());
        Assert.NotEqual(a.Id, again.Id);
    }

    [Fact]
    public void Deleting_takes_the_transcript_out_of_the_note_and_keeps_what_the_examiner_wrote()
    {
        var store = new AiStore(Path.Combine(_dir.FullName, "_reader_ai.db"));
        var tags = new TagStore(_dir.FullName);
        var m1 = new MediaRef("r1", "a.opus", "audio/ogg", "/data/a.opus", "media/a.opus", null, "chat", "Chat");
        var m2 = new MediaRef("r2", "b.opus", "audio/ogg", "/data/b.opus", "media/b.opus", null, "chat", "Chat");
        var m3 = new MediaRef("r3", "c.opus", "audio/ogg", "/data/c.opus", "media/c.opus", null, "chat", "Chat");
        var a = Transcribe(store, Whisper, "base", "r1", "hello from a");
        var old2 = Transcribe(store, Server, "large", "r2", "older take of b");
        var b = Transcribe(store, Whisper, "base", "r2", "newest take of b");
        var c = Transcribe(store, Whisper, "base", "r3", "untouched c");
        foreach (var (m, t) in new[] { (m1, "hello from a"), (m2, "newest take of b"), (m3, "untouched c") }) TranscriptNotes.Write(tags, m, t);
        tags.SetNote(TagStore.FilePrefix + "media/a.opus", 0, "check the sender\n\n" + TagStore.AutoTranscriptHeader + "\nhello from a");

        var (deleted, changed) = TranscriptNotes.Delete(tags, store, [a.Id, b.Id],
            new Dictionary<string, MediaRef> { ["r1"] = m1, ["r2"] = m2, ["r3"] = m3 });

        Assert.Equal(2, deleted);
        Assert.Equal(4, changed); // two keys each for a and b
        var notes = tags.FileNotes();
        Assert.Equal("check the sender", notes["media/a.opus"]);  // the examiner's words stay, the transcript goes
        Assert.False(notes.ContainsKey("data/a.opus")); // only the transcript was there: emptied
        Assert.Equal(TagStore.AutoTranscriptHeader + "\nolder take of b", notes["media/b.opus"]); // the one left takes its place
        Assert.Equal(TagStore.AutoTranscriptHeader + "\nuntouched c", notes["media/c.opus"]);
        Assert.Equal([c.Id, old2.Id], store.Transcriptions().Select(r => r.Id));
    }

    [Fact]
    public void A_file_not_resolved_is_cleaned_by_its_text_only_when_that_text_is_its_own()
    {
        var store = new AiStore(Path.Combine(_dir.FullName, "_reader_ai.db"));
        var tags = new TagStore(_dir.FullName);
        var a = Transcribe(store, Whisper, "base", "x-1", "a unique sentence");
        var n1 = Transcribe(store, Whisper, "base", "x-2", "(no speech recognised)");
        Transcribe(store, Whisper, "base", "x-3", "(no speech recognised)");
        tags.WriteFileTranscript("sdcard/a.opus", "a unique sentence");
        tags.WriteFileTranscript("sdcard/silent2.opus", "(no speech recognised)");
        tags.WriteFileTranscript("sdcard/silent3.opus", "(no speech recognised)");

        var (deleted, _) = TranscriptNotes.Delete(tags, store, [a.Id, n1.Id], new Dictionary<string, MediaRef>());

        Assert.Equal(2, deleted);
        var notes = tags.FileNotes();
        Assert.False(notes.ContainsKey("sdcard/a.opus"));            // emptied
        Assert.True(notes.ContainsKey("sdcard/silent2.opus"));       // the same text as another file's: not guessed
        Assert.True(notes.ContainsKey("sdcard/silent3.opus"));
    }

    // ---- the size filter ----

    [Theory]
    [InlineData(MediaSizeFilter.Any, 0, true)]
    [InlineData(MediaSizeFilter.Any, -1, true)]
    [InlineData(MediaSizeFilter.Under100K, 100 * 1024 - 1, true)]
    [InlineData(MediaSizeFilter.Under100K, 100 * 1024, false)]
    [InlineData(MediaSizeFilter.K100To1M, 100 * 1024, true)]
    [InlineData(MediaSizeFilter.K100To1M, 1024 * 1024, false)]
    [InlineData(MediaSizeFilter.M1To10, 1024 * 1024, true)]
    [InlineData(MediaSizeFilter.M1To10, 10L * 1024 * 1024 - 1, true)]
    [InlineData(MediaSizeFilter.M10To100, 10L * 1024 * 1024, true)]
    [InlineData(MediaSizeFilter.M10To100, 100L * 1024 * 1024, false)]
    [InlineData(MediaSizeFilter.Over100M, 100L * 1024 * 1024, true)]
    [InlineData(MediaSizeFilter.Over100M, 5_000_000_000L, true)]
    [InlineData(MediaSizeFilter.Over100M, -1, false)]
    [InlineData(MediaSizeFilter.UnknownOnly, -1, true)]
    [InlineData(MediaSizeFilter.UnknownOnly, 0, false)]
    public void Presets_keep_the_lower_bound_and_leave_out_the_upper(string preset, long size, bool kept) =>
        Assert.Equal(kept, new MediaSizeFilter(preset).Matches(size));

    [Fact]
    public void A_custom_range_reads_units_and_decimals_and_includes_its_maximum()
    {
        var f = new MediaSizeFilter(MediaSizeFilter.Custom, "1,5", "2", "MB");
        Assert.False(f.Matches(1_572_863));
        Assert.True(f.Matches(1_572_864));           // 1.5 MB
        Assert.True(f.Matches(2 * 1024 * 1024));     // the maximum, included
        Assert.False(f.Matches(2 * 1024 * 1024 + 1));
        Assert.True(new MediaSizeFilter(MediaSizeFilter.Custom, "", "10", "KB").Matches(0));
        Assert.True(new MediaSizeFilter(MediaSizeFilter.Custom, "1", null, "GB").Matches(1L << 40));
        Assert.False(new MediaSizeFilter(MediaSizeFilter.Custom, "1", null, "GB").Matches((1L << 30) - 1));
        var none = new MediaSizeFilter(MediaSizeFilter.Custom, " ", "abc", "MB"); // nothing usable typed: no filter
        Assert.False(none.IsActive);
        Assert.True(none.Matches(-1));
        Assert.Null(MediaSizeFilter.Parse("-3", "MB"));
    }

    [Fact]
    public void Unknown_sizes_are_kept_only_when_asked_while_a_size_filter_is_set()
    {
        Assert.False(new MediaSizeFilter(MediaSizeFilter.M1To10).Matches(-1));
        Assert.True(new MediaSizeFilter(MediaSizeFilter.M1To10, KeepUnknown: true).Matches(-1));
        Assert.False(new MediaSizeFilter(MediaSizeFilter.M1To10, KeepUnknown: true).Matches(5));
        Assert.Equal("unknown size", MediaSizeFilter.Format(-1));
        Assert.Equal("1.5 MB", MediaSizeFilter.Format(1_572_864));
        Assert.Equal("512 B", MediaSizeFilter.Format(512));
    }

    // ---- Ollama: no thinking, capped context ----

    [Fact]
    public void Ollama_is_asked_without_thinking_and_num_ctx_never_passes_the_cap()
    {
        var body = AiClient.OllamaChatBody("qwen3:8b", "sys", new string('x', 300_000), null, modelContext: 262_144, cap: 16_384);
        Assert.False((bool)body["think"]!);
        Assert.Equal(16_384, (int)body["options"]!["num_ctx"]!);
        Assert.Equal(0, (int)body["options"]!["temperature"]!);

        Assert.Equal(6144, AiClient.OllamaNumCtx(3000, 0, 0, 262_144, 16_384));   // a small request asks for what it needs
        Assert.Equal(32_768, AiClient.OllamaNumCtx(0, 900_000, 0, null, null)); // nothing known: the old 32K ceiling
        Assert.Equal(8192, AiClient.OllamaNumCtx(0, 900_000, 0, 8192, 16_384)); // the model's own context is lower
        Assert.Equal(16_384, AiClient.Capped(262_144, 16_384));
        Assert.Equal(16_384, AiClient.Capped(null, 16_384));
        Assert.Equal(262_144, AiClient.Capped(262_144, null)); // 0 / null in settings: no cap
        Assert.Equal(262_144, AiClient.Capped(262_144, 0));
        var images = AiClient.OllamaChatBody("m", "s", "u", [new AiImage("a.jpg", "image/jpeg", [1, 2, 3])], null, 16_384);
        Assert.Single((JsonArray)images["messages"]![1]!["images"]!);
    }
}
