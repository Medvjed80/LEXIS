namespace AleappReader.Core;

/// <summary>Copies saved speech-to-text results into the notes of the audio files they cite. The same file key is what
/// the gallery, Extraction Files, the details pane and a chat bubble look up. Applying it again changes nothing when
/// the note already holds that transcript.</summary>
public static class TranscriptNotes
{
    /// <summary>Every path this file is known by, as <see cref="TagStore.FileKey"/>, extraction path then source path
    /// then the file name. A path that climbs out with <c>..</c> is left out. Empty when the file has no usable name.</summary>
    public static IReadOnlyList<string> Keys(MediaRef m)
    {
        var list = new List<string>();
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var key = TagStore.FileKey(path);
            if (key == "" || key.Split('/').Contains("..")) return;
            if (!list.Contains(key, StringComparer.OrdinalIgnoreCase)) list.Add(key);
        }
        Add(m.ExtractionPath);
        Add(m.SourcePath);
        // the bare file name only when the file has no safe path: two voice notes often share a name
        if (list.Count == 0) Add(m.Name);
        return list;
    }

    /// <summary>The note stored under any of <see cref="Keys"/>, or null. A note the examiner wrote is preferred to one
    /// that is only an automatic transcript, so a second path for the same file does not hide it.</summary>
    public static string? Lookup(IReadOnlyDictionary<string, string> notes, MediaRef m)
    {
        string? automatic = null;
        foreach (var key in Keys(m))
        {
            if (!notes.TryGetValue(key, out var note) || note == "") continue;
            if (ExaminerText(note) != "") return note;
            automatic ??= note;
        }
        return automatic;
    }

    /// <summary>What the examiner wrote, above an automatic transcript. Empty when the note is only that transcript.</summary>
    public static string ExaminerText(string note)
    {
        var n = note.Replace("\r\n", "\n");
        var at = n.IndexOf(TagStore.AutoTranscriptHeader, StringComparison.Ordinal);
        return (at < 0 ? n : n[..at]).Trim();
    }

    /// <summary>The automatic transcript in a note, or empty when the note has none.</summary>
    public static string Body(string note)
    {
        var n = note.Replace("\r\n", "\n");
        var at = n.IndexOf(TagStore.AutoTranscriptHeader, StringComparison.Ordinal);
        return at < 0 ? "" : n[(at + TagStore.AutoTranscriptHeader.Length)..].Trim();
    }

    /// <summary>Writes <paramref name="transcript"/> under every key of <paramref name="m"/>. An examiner's note found on
    /// any of those keys is kept on the others too, so the gallery and Extraction Files show the same note. Returns
    /// how many notes changed. This is the per-file write: it does not read the rest of the AI base.</summary>
    public static int Write(TagStore tags, MediaRef m, string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return 0;
        var notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in Keys(m))
            if (tags.NoteOf(TagStore.FilePrefix + key, 0) is { } note) notes[key] = note;
        var writes = new List<(string Table, string Note)>();
        var n = Plan(notes, m, transcript, writes);
        tags.SetFileNotes(writes);
        return n;
    }

    /// <summary>One file already transcribed: the newest saved transcription that cites it (as the examiner revised it),
    /// written into that file's note. One cite lookup. Does not call <see cref="Apply(TagStore, AiStore, IReadOnlyDictionary{string, MediaRef})"/>.</summary>
    public static int WriteSaved(TagStore tags, AiStore store, MediaRef media)
    {
        var cite = AiText.Key("media", media.RefId);
        var result = store.Citing(cite).FirstOrDefault(r => r.Kind == "Transcription");
        if (result is null) return 0;
        var revised = AiText.AsRevised(store, [result]).FirstOrDefault();
        if (revised is null) return 0; // the examiner excluded the newest one
        var text = AiText.Notes([revised], cite).FirstOrDefault(n => n.Name == "Transcription")?.Text;
        return string.IsNullOrWhiteSpace(text) ? 0 : Write(tags, media, text);
    }

    /// <summary>How many times <see cref="Apply(TagStore, AiStore, IReadOnlyDictionary{string, MediaRef})"/> has run.
    /// The per-file path (<see cref="Write"/>, <see cref="WriteSaved"/>) must not add to it.</summary>
    internal static int ApplyRuns { get; private set; }

    /// <summary>The latest transcription of each media file cited in the case, as the examiner left it, written into
    /// that file's note. Reads the transcriptions once and writes the notes in one transaction.</summary>
    public static int Apply(CaseSession session)
    {
        Dictionary<string, MediaRef> media;
        try
        {
            using var con = session.OpenDb();
            media = new MediaResolver(session).All(con, int.MaxValue)
                .GroupBy(m => m.RefId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { return 0; }
        return Apply(session.Tags, AiStore.For(session), media);
    }

    public static int Apply(TagStore tags, AiStore store, IReadOnlyDictionary<string, MediaRef> mediaByRef)
    {
        ApplyRuns++;
        var rows = store.Transcriptions(); // one query, kind = Transcription, newest first
        if (rows.Count == 0 || mediaByRef.Count == 0) return 0;
        var revisions = store.Revisions(rows.Select(r => r.Id).ToList()); // one query for every revision of those rows
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new List<(MediaRef Media, string Text)>();
        foreach (var result in rows)
        {
            var revised = Revised(result, revisions);
            foreach (var cite in result.Cited)
            {
                if (!cite.StartsWith("[media#", StringComparison.Ordinal) || !cite.EndsWith(']')) continue;
                if (!seen.Add(cite)) continue; // a newer result for this file was already taken (even an excluded one)
                if (revised is null) continue;
                var text = AiText.Notes([revised], cite).FirstOrDefault(n => n.Name == "Transcription")?.Text;
                if (string.IsNullOrWhiteSpace(text)) continue;
                var id = cite["[media#".Length..^1];
                if (!mediaByRef.TryGetValue(id, out var media)) continue;
                pending.Add((media, text));
            }
        }
        if (pending.Count == 0) return 0;
        var notes = tags.FileNotes();
        var writes = new List<(string Table, string Note)>();
        var changed = 0;
        foreach (var (media, text) in pending)
            changed += Plan(notes, media, text, writes);
        tags.SetFileNotes(writes);
        return changed;
    }

    /// <summary>The result as the examiner left it, or null when they excluded it. Same rule as <see cref="AiText.AsRevised"/>.</summary>
    static AiResult? Revised(AiResult result, IReadOnlyDictionary<long, AiRevision> revisions)
    {
        if (!revisions.TryGetValue(result.Id, out var v)) return result;
        if (v.Excluded) return null;
        return v.Text is { } t && result.Cited.Count == 1
            ? result with { Output = $"{result.Cited[0]}: {string.Join(" ", t.Split('\n', StringSplitOptions.TrimEntries))}" }
            : result;
    }

    /// <summary>The notes <paramref name="transcript"/> would change, recorded in <paramref name="writes"/> and in
    /// <paramref name="notes"/> so a later file sees them. An examiner's note on any key of the file is kept on the others.</summary>
    static int Plan(Dictionary<string, string> notes, MediaRef media, string transcript, List<(string Table, string Note)> writes)
    {
        var keys = Keys(media);
        string? examiner = null;
        foreach (var key in keys)
            if (notes.TryGetValue(key, out var note) && ExaminerText(note) is { Length: > 0 } head)
            { examiner = head; break; }
        var n = 0;
        foreach (var key in keys)
        {
            notes.TryGetValue(key, out var existing);
            if (examiner is not null && ExaminerText(existing ?? "") == "")
                existing = string.IsNullOrWhiteSpace(existing) ? examiner : examiner + "\n\n" + existing.Trim();
            var merged = TagStore.MergeTranscript(existing, transcript);
            if (existing is not null && existing.Replace("\r\n", "\n").Trim() == merged) continue;
            notes[key] = merged;
            writes.Add((TagStore.FilePrefix + key, merged));
            n++;
        }
        return n;
    }
}
