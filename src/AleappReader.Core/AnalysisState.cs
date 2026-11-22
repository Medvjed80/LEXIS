using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AleappReader.Core;

// ---------------------------------------------------------------------------------------------------------------------
// Analysis state (.lexisstate): the examiner's tags and notes, exported from one parsing of an extraction and imported
// into a newer parsing of the same extraction (a newer LEXIS / ALEAPP / iLEAPP that finds more). The file carries no
// evidence content: only tags, the examiner's notes, metadata (artifact, row id, path, size, a time) and SHA-256 hashes.
// It is case data all the same: it is written where the examiner chooses and nothing here touches the network.
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>A tag as the settings define it: its name and, when the app knows it, its colour (#RRGGBB).</summary>
public sealed record TagDefinition(string Name, string? Color = null);

public sealed class AnalysisStateFile
{
    [JsonPropertyName("format")] public string Format { get; set; } = AnalysisState.FormatName;
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = AnalysisState.SchemaVersion;
    [JsonPropertyName("appVersion")] public string AppVersion { get; set; } = "";
    [JsonPropertyName("exportedUtc")] public DateTimeOffset ExportedUtc { get; set; }
    [JsonPropertyName("case")] public StateCase Case { get; set; } = new();
    [JsonPropertyName("tagDefinitions")] public List<TagDefinition> TagDefinitions { get; set; } = [];
    [JsonPropertyName("inventory")] public StateInventory Inventory { get; set; } = new();
    [JsonPropertyName("items")] public List<StateItem> Items { get; set; } = [];
}

/// <summary>What the state was taken from: the LEAPP and the extraction by name (never the examiner's paths).</summary>
public sealed class StateCase
{
    [JsonPropertyName("parsedBy")] public string ParsedBy { get; set; } = "";
    [JsonPropertyName("leappName")] public string LeappName { get; set; } = "";
    [JsonPropertyName("leappVersion")] public string LeappVersion { get; set; } = "";
    [JsonPropertyName("platform")] public string Platform { get; set; } = "";
    [JsonPropertyName("extractionName")] public string ExtractionName { get; set; } = "";
    /// <summary>The SHA-256 root of a portable case's SHA256SUMS.txt, when the case was opened from one.</summary>
    [JsonPropertyName("packageRoot")] public string? PackageRoot { get; set; }
}

/// <summary>How many records each artifact had and how many files the extraction had, so an import can count what
/// the new parsing found in addition.</summary>
public sealed class StateInventory
{
    [JsonPropertyName("artifacts")] public List<StateArtifact> Artifacts { get; set; } = [];
    /// <summary>Files of the extraction plus media of the case; null when the extraction was not reachable.</summary>
    [JsonPropertyName("files")] public long? Files { get; set; }
}

public sealed class StateArtifact
{
    [JsonPropertyName("table")] public string Table { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("module")] public string Module { get; set; } = "";
    [JsonPropertyName("records")] public long Records { get; set; }
    /// <summary>The artifact's source files as paths on the device, with their SHA-256 when the extraction was reachable.</summary>
    [JsonPropertyName("sources")] public List<StateSource> Sources { get; set; } = [];
}

public sealed record StateSource([property: JsonPropertyName("path")] string Path, [property: JsonPropertyName("sha256")] string? Sha256);

public sealed class StateItem
{
    /// <summary>"record" (a row of an artifact: chat message, timeline or location entry, any ALEAPP/iLEAPP row) or "file".</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("tag")] public string Tag { get; set; } = "";
    [JsonPropertyName("note")] public string Note { get; set; } = "";
    [JsonPropertyName("created")] public DateTimeOffset Created { get; set; }
    [JsonPropertyName("record")] public StateRecord? Record { get; set; }
    [JsonPropertyName("file")] public StateFile? File { get; set; }
}

public sealed class StateRecord
{
    [JsonPropertyName("table")] public string Table { get; set; } = "";
    [JsonPropertyName("artifact")] public string Artifact { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("module")] public string Module { get; set; } = "";
    [JsonPropertyName("rowId")] public long RowId { get; set; }
    /// <summary>The first date of the row as stored, to find an unmatched record by hand.</summary>
    [JsonPropertyName("time")] public string? Time { get; set; }
    /// <summary>SHA-256 over every field of the row but media references, by normalised column name (<see cref="Fields"/>).</summary>
    [JsonPropertyName("contentHash")] public string ContentHash { get; set; } = "";
    [JsonPropertyName("fields")] public List<string> Fields { get; set; } = [];
    /// <summary>SHA-256 over the identity fields only (text, sender, recipient, numbers, names, ids, coordinates…;
    /// no dates or paths, whose formatting a newer parser may change): the secondary key.</summary>
    [JsonPropertyName("coreHash")] public string? CoreHash { get; set; }
    [JsonPropertyName("coreFields")] public List<string> CoreFields { get; set; } = [];
    /// <summary>Each identity field's value hash (first 16 hex digits of its SHA-256), so a field the newer parser renamed
    /// or dropped does not stop the others being compared one by one (row id + unchanged source rule).</summary>
    [JsonPropertyName("coreFieldHashes")] public Dictionary<string, string> CoreFieldHashes { get; set; } = [];
    /// <summary>SHA-256 of the artifact's source files (sorted, joined) when they could be read; with <see cref="RowId"/>
    /// a secondary key for a source database that has not changed.</summary>
    [JsonPropertyName("sourceHash")] public string? SourceHash { get; set; }
}

public sealed class StateFile
{
    /// <summary>The tag key: the path in the extraction, or the device path of a case media file (as the gallery tags it).</summary>
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("size")] public long? Size { get; set; }
    /// <summary>The file's SHA-256, null when it could not be read when exporting.</summary>
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
}

public enum MatchTier { Hash, Secondary, Path, None }

/// <summary>One exported item and where it lands in the open case.</summary>
public sealed record ItemMatch(StateItem Item, MatchTier Tier, string Rule, IReadOnlyList<(string Table, long RowId)> Targets, bool Ambiguous)
{
    public string Describe => Item.Record is { } r ? $"{r.Artifact} #{r.RowId}" : Item.File?.Path ?? "";
}

public sealed class AnalysisStateException(string message) : Exception(message);

/// <summary>The result of matching a state to the open case, before (and after) writing it.</summary>
public sealed class ImportPlan
{
    public required AnalysisStateFile State { get; init; }
    public required IReadOnlyList<ItemMatch> Matches { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    /// <summary>Records the new parsing has beyond the old one: per artifact, the rise in the count (and every record of
    /// an artifact the old parsing did not have).</summary>
    public long NewRecords { get; init; }
    public long NewArtifacts { get; init; }
    /// <summary>Files beyond the old count; null when either side's extraction was not reachable.</summary>
    public long? NewFiles { get; init; }
    public int ByHash => Matches.Count(m => m.Tier == MatchTier.Hash);
    public int BySecondary => Matches.Count(m => m.Tier == MatchTier.Secondary);
    public int ByPath => Matches.Count(m => m.Tier == MatchTier.Path);
    public int Ambiguous => Matches.Count(m => m.Ambiguous);
    public int NotFound => Matches.Count(m => m.Tier == MatchTier.None);
    /// <summary>Set by <see cref="AnalysisState.Apply"/>: targets tagged, targets that already had another tag (kept), notes merged.</summary>
    public int Tagged { get; set; }
    public int KeptExisting { get; set; }
    public int NotesMerged { get; set; }
    public bool Applied { get; set; }

    public string Summary(bool italian)
    {
        var sb = new StringBuilder();
        void L(string it, string en) => sb.AppendLine(italian ? it : en);
        L($"Elementi nello stato: {Matches.Count:N0}", $"Items in the state: {Matches.Count:N0}");
        L($"  abbinati per hash: {ByHash:N0}", $"  matched by hash: {ByHash:N0}");
        L($"  abbinati per chiave secondaria: {BySecondary:N0}", $"  matched by secondary key: {BySecondary:N0}");
        L($"  abbinati per percorso + dimensione: {ByPath:N0}", $"  matched by path + size: {ByPath:N0}");
        L($"  ambigui (taggati su tutti i candidati): {Ambiguous:N0}", $"  ambiguous (tagged on every candidate): {Ambiguous:N0}");
        L($"  non trovati: {NotFound:N0}", $"  not found: {NotFound:N0}");
        L($"Elementi nuovi in questo parsing (non taggati): {NewRecords:N0} record" + (NewArtifacts > 0 ? $" ({NewArtifacts:N0} artefatti nuovi)" : "") +
          (NewFiles is { } f ? $", {f:N0} file" : ""),
          $"New items in this parsing (untagged): {NewRecords:N0} records" + (NewArtifacts > 0 ? $" ({NewArtifacts:N0} new artifacts)" : "") +
          (NewFiles is { } g ? $", {g:N0} files" : ""));
        if (Applied)
        {
            L($"Scritti: {Tagged:N0} tag; tag esistenti diversi mantenuti: {KeptExisting:N0}; note unite: {NotesMerged:N0}",
              $"Written: {Tagged:N0} tags; different existing tags kept: {KeptExisting:N0}; notes merged: {NotesMerged:N0}");
        }
        foreach (var w in Warnings) sb.AppendLine("! " + w);
        return sb.ToString();
    }

    /// <summary>The report for the record: the summary, then one line per item (no evidence content: artifact, row id,
    /// path, hashes, tag, how it was matched and to what).</summary>
    public string ToCsv(bool italian)
    {
        var sb = new StringBuilder();
        static string C(string? v) => v is null ? "" : v.IndexOfAny([',', '"', '\n', '\r', ';']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        sb.AppendLine(italian
            ? "tipo,artefatto_o_percorso,riga_originale,tag,esito,regola,ambiguo,destinazioni,hash"
            : "kind,artifact_or_path,original_row,tag,result,rule,ambiguous,targets,hash");
        foreach (var m in Matches)
        {
            var r = m.Item.Record; var f = m.Item.File;
            var result = m.Tier switch
            {
                MatchTier.Hash => italian ? "hash" : "hash",
                MatchTier.Secondary => italian ? "chiave secondaria" : "secondary key",
                MatchTier.Path => italian ? "percorso+dimensione" : "path+size",
                _ => italian ? "non trovato" : "not found",
            };
            var targets = string.Join(" | ", m.Targets.Select(t => t.Table.StartsWith(TagStore.FilePrefix, StringComparison.Ordinal)
                ? t.Table[TagStore.FilePrefix.Length..] : $"{t.Table}#{t.RowId}"));
            sb.AppendLine(string.Join(",", C(m.Item.Kind), C(r?.Artifact ?? f?.Path), r?.RowId.ToString(CultureInfo.InvariantCulture) ?? "",
                C(m.Item.Tag), C(result), C(m.Rule), m.Ambiguous ? "1" : "0", C(targets), C(r?.ContentHash ?? f?.Sha256)));
        }
        return sb.ToString();
    }

    public string ToText(bool italian)
    {
        var sb = new StringBuilder();
        sb.AppendLine(italian ? "LEXIS — importazione stato analisi" : "LEXIS — analysis state import");
        sb.AppendLine((italian ? "Stato esportato da " : "State exported by ") + $"LEXIS {State.AppVersion}, {State.ExportedUtc:yyyy-MM-dd HH:mm:ss} UTC, {State.Case.ParsedBy}");
        sb.AppendLine((italian ? "Estrazione: " : "Extraction: ") + State.Case.ExtractionName);
        sb.AppendLine();
        sb.Append(Summary(italian));
        void Section(string it, string en, IEnumerable<ItemMatch> list)
        {
            var l = list.ToList();
            if (l.Count == 0) return;
            sb.AppendLine().AppendLine((italian ? it : en) + $" ({l.Count:N0})");
            foreach (var m in l)
                sb.AppendLine($"  [{m.Item.Tag}] {m.Describe}" + (m.Item.Record?.Time is { } t ? $"  {t}" : "") +
                              (m.Targets.Count > 0 ? "  → " + string.Join(", ", m.Targets.Select(x => x.Table.StartsWith(TagStore.FilePrefix, StringComparison.Ordinal)
                                  ? x.Table[TagStore.FilePrefix.Length..] : $"#{x.RowId}")) : "") + (m.Rule != "" ? $"  ({m.Rule})" : ""));
        }
        Section("Non trovati", "Not found", Matches.Where(m => m.Tier == MatchTier.None));
        Section("Ambigui", "Ambiguous", Matches.Where(m => m.Ambiguous));
        Section("Abbinati per chiave secondaria", "Matched by secondary key", Matches.Where(m => m.Tier == MatchTier.Secondary));
        Section("Abbinati per percorso + dimensione (senza hash)", "Matched by path + size (no hash)", Matches.Where(m => m.Tier == MatchTier.Path));
        return sb.ToString();
    }
}

/// <summary>Export and import of the analysis state, see the file header.</summary>
public static class AnalysisState
{
    public const string FormatName = "lexis-analysis-state";
    public const int SchemaVersion = 1;
    public const string Extension = ".lexisstate";

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    // ----------------------------------------------------------------------------------------------------- export

    public static AnalysisStateFile Export(CaseSession s, IReadOnlyList<TagDefinition> tagDefinitions, string appVersion,
        CancellationToken ct = default)
    {
        var files = new CaseFiles(s);
        var state = new AnalysisStateFile
        {
            AppVersion = appVersion,
            ExportedUtc = DateTimeOffset.UtcNow,
            Case = new StateCase
            {
                ParsedBy = s.Manifest.ParsedBy,
                LeappName = s.Manifest.ParserInfo.GetValueOrDefault("leapp_name") ?? "",
                LeappVersion = s.Manifest.ParserInfo.GetValueOrDefault("leapp_version") ?? "",
                Platform = s.Manifest.IsIos ? "iOS" : s.Manifest.IsAndroid ? "Android" : "",
                ExtractionName = EvidenceOrigin.ExtractionName(s),
                PackageRoot = s.Package?.Root,
            },
            TagDefinitions = [.. tagDefinitions],
        };

        var tags = s.Tags.All();
        // Tags used but no longer in the settings travel too, so the import can offer them.
        foreach (var t in tags.Select(t => t.Tag).Where(t => t != "").Distinct(StringComparer.Ordinal))
            if (!state.TagDefinitions.Any(d => d.Name == t)) state.TagDefinitions.Add(new TagDefinition(t));

        var sourceHashes = new Dictionary<string, string?>(StringComparer.Ordinal);
        using var con = s.OpenDb();
        foreach (var a in s.Manifest.Artifacts)
        {
            ct.ThrowIfCancellationRequested();
            var sources = EvidenceOrigin.RecordedSources(a).Select(p => p.Replace('\\', '/').TrimStart('/')).Distinct().ToList();
            state.Inventory.Artifacts.Add(new StateArtifact
            {
                Table = a.TableName, Name = a.Name, Category = a.Category, Module = a.Module, Records = Count(con, a),
                Sources = [.. sources.Select(p => new StateSource(EvidenceOrigin.DevicePath(p), files.Sha256(p)))],
            });
        }
        state.Inventory.Files = files.Count;

        var byTable = s.Manifest.Artifacts.GroupBy(a => a.TableName).ToDictionary(g => g.Key, g => g.First());
        foreach (var g in tags.GroupBy(t => t.TableName))
        {
            ct.ThrowIfCancellationRequested();
            if (g.Key.StartsWith(TagStore.FilePrefix, StringComparison.Ordinal))
            {
                var t = g.First();
                var key = TagStore.FileKey(g.Key[TagStore.FilePrefix.Length..]);
                state.Items.Add(new StateItem
                {
                    Kind = "file", Tag = t.Tag, Note = TagStore.WithoutTranscript(t.Note), Created = t.Created,
                    File = new StateFile { Path = key, Size = files.SizeOf(key), Sha256 = files.Sha256(key) },
                });
                continue;
            }
            if (!byTable.TryGetValue(g.Key, out var a))
            {
                // A tag on a table this report no longer lists: kept, it can only be reported as not found.
                foreach (var t in g)
                    state.Items.Add(new StateItem { Kind = "record", Tag = t.Tag, Note = TagStore.WithoutTranscript(t.Note), Created = t.Created,
                        Record = new StateRecord { Table = g.Key, Artifact = g.Key, RowId = t.RowId } });
                continue;
            }
            var shape = RowShape.Of(con, a);
            var art = state.Inventory.Artifacts.First(x => x.Table == a.TableName);
            var sourceHash = SourcesHash(art.Sources);
            var wanted = g.ToDictionary(t => t.RowId);
            var seen = new HashSet<long>();
            foreach (var row in wanted.Keys.Chunk(5000).SelectMany(ids => ArtifactQuery.All(con, a, rowIds: ids))) // SQL text stays short
            {
                var t = wanted[row.RowId];
                seen.Add(row.RowId);
                state.Items.Add(new StateItem
                {
                    Kind = "record", Tag = t.Tag, Note = TagStore.WithoutTranscript(t.Note), Created = t.Created,
                    Record = new StateRecord
                    {
                        Table = a.TableName, Artifact = a.Name, Category = a.Category, Module = a.Module, RowId = row.RowId,
                        Time = shape.Time(row), ContentHash = shape.ContentHash(row, shape.Fields), Fields = [.. shape.Fields],
                        CoreHash = shape.CoreFields.Count > 0 ? shape.ContentHash(row, shape.CoreFields) : null, CoreFields = [.. shape.CoreFields],
                        CoreFieldHashes = shape.CoreFields.ToDictionary(f => f, f => shape.FieldHash(row, f)),
                        SourceHash = sourceHash,
                    },
                });
            }
            foreach (var t in g.Where(t => !seen.Contains(t.RowId)))
                state.Items.Add(new StateItem { Kind = "record", Tag = t.Tag, Note = TagStore.WithoutTranscript(t.Note), Created = t.Created,
                    Record = new StateRecord { Table = a.TableName, Artifact = a.Name, Category = a.Category, Module = a.Module, RowId = t.RowId } });
        }
        return state;
    }

    public static void Save(AnalysisStateFile state, string path)
    {
        var tmp = path + ".part";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, Json), new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }

    /// <summary>Reads a state file; a file of another format or of a newer schema throws <see cref="AnalysisStateException"/>.</summary>
    public static AnalysisStateFile Load(string path) => Parse(File.ReadAllText(path));

    public static AnalysisStateFile Parse(string json)
    {
        AnalysisStateFile? state;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var f) || f.GetString() != FormatName)
                throw new AnalysisStateException("Il file non è uno stato analisi LEXIS. / The file is not a LEXIS analysis state.");
            if (!root.TryGetProperty("schemaVersion", out var v) || !v.TryGetInt32(out var version) || version < 1)
                throw new AnalysisStateException("Versione dello schema mancante o non valida. / Missing or invalid schema version.");
            if (version > SchemaVersion)
                throw new AnalysisStateException($"Lo stato usa lo schema {version}, questa versione di LEXIS legge fino allo schema {SchemaVersion}: aggiornare LEXIS. / " +
                                                 $"The state uses schema {version}; this LEXIS reads up to schema {SchemaVersion}: update LEXIS.");
            state = root.Deserialize<AnalysisStateFile>(Json);
        }
        catch (JsonException e) { throw new AnalysisStateException("File stato analisi non leggibile. / Unreadable analysis state file: " + e.Message); }
        return state ?? throw new AnalysisStateException("File stato analisi vuoto. / Empty analysis state file.");
    }

    // ----------------------------------------------------------------------------------------------------- import

    /// <summary>Matches every item of <paramref name="state"/> to the open case, writing nothing. Tiers: the hash (file
    /// SHA-256; record content hash), then the secondary key (record identity fields, or row id with an unchanged
    /// source file), then path + size (files whose hash could not be read). Nothing fuzzy: every tier is an equality.</summary>
    public static ImportPlan Plan(CaseSession s, AnalysisStateFile state, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        if (state.Case.Platform != "" && (s.Manifest.IsIos ? "iOS" : s.Manifest.IsAndroid ? "Android" : "") is { Length: > 0 } p && p != state.Case.Platform)
            warnings.Add($"Piattaforma diversa: stato {state.Case.Platform}, caso {p}. / Different platform: state {state.Case.Platform}, case {p}.");
        var name = EvidenceOrigin.ExtractionName(s);
        if (state.Case.ExtractionName != "" && name != "" && !string.Equals(name, state.Case.ExtractionName, StringComparison.OrdinalIgnoreCase))
            warnings.Add($"Nome dell'estrazione diverso: stato «{state.Case.ExtractionName}», caso «{name}». / Different extraction name.");

        var matches = new List<ItemMatch>();
        using var con = s.OpenDb();
        var files = new CaseFiles(s);

        // Records, one pass per target artifact.
        var records = state.Items.Where(i => i.Kind == "record" && i.Record is not null).ToList();
        foreach (var g in records.GroupBy(i => i.Record!.Table))
        {
            ct.ThrowIfCancellationRequested();
            var first = g.First().Record!;
            var a = s.Manifest.Artifacts.FirstOrDefault(x => x.TableName == first.Table)
                    ?? s.Manifest.Artifacts.FirstOrDefault(x => string.Equals(x.Name, first.Artifact, StringComparison.OrdinalIgnoreCase) &&
                                                                string.Equals(x.Category, first.Category, StringComparison.OrdinalIgnoreCase));
            if (a is null)
            {
                matches.AddRange(g.Select(i => new ItemMatch(i, MatchTier.None, "artifact missing", [], false)));
                continue;
            }
            matches.AddRange(MatchRecords(con, a, g.ToList(), files));
        }

        // Files: SHA-256 against every file of the same size, then path + size when a hash is missing.
        var fileItems = state.Items.Where(i => i.Kind == "file" && i.File is not null).ToList();
        if (fileItems.Count > 0)
        {
            var all = files.All();
            var bySize = all.GroupBy(f => f.Size).ToDictionary(g => g.Key, g => g.Select(f => f.Key).ToList());
            var byKey = all.GroupBy(f => f.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var i in fileItems)
            {
                ct.ThrowIfCancellationRequested();
                var f = i.File!;
                if (f.Sha256 is { } sha && f.Size is { } size)
                {
                    var hits = bySize.GetValueOrDefault(size, []).Where(k => files.Sha256(k) == sha).ToList();
                    if (hits.Count > 0) { matches.Add(Pick(i, MatchTier.Hash, "sha256", hits, f.Path)); continue; }
                    if (files.Count is null && !byKey.ContainsKey(f.Path))
                    {   // the extraction is not reachable here: the tag is kept on its path, reported as not verified
                        matches.Add(new ItemMatch(i, MatchTier.Path, "path only: extraction not reachable, not verified", [(TagStore.FilePrefix + f.Path, 0)], false));
                        continue;
                    }
                    matches.Add(new ItemMatch(i, MatchTier.None, "sha256 not found", [], false));
                    continue;
                }
                // No hash in the state (unreadable when exported): only the same path with the same size.
                if (byKey.TryGetValue(f.Path, out var same) && (f.Size is null || same.Size == f.Size))
                    matches.Add(new ItemMatch(i, MatchTier.Path, f.Size is null ? "path (no size, no hash)" : "path+size (no hash)", [(TagStore.FilePrefix + same.Key, 0)], false));
                else if (files.Count is null)
                    matches.Add(new ItemMatch(i, MatchTier.Path, "path only: extraction not reachable, not verified", [(TagStore.FilePrefix + f.Path, 0)], false));
                else matches.Add(new ItemMatch(i, MatchTier.None, "no hash, path not found", [], false));
            }
        }

        // New items: the rise of every artifact's count, every record of an artifact the old parsing did not have.
        long newRecords = 0, newArtifacts = 0;
        var old = state.Inventory.Artifacts.GroupBy(x => x.Table).ToDictionary(g => g.Key, g => g.First());
        foreach (var a in s.Manifest.Artifacts)
        {
            var n = Count(con, a);
            var before = old.GetValueOrDefault(a.TableName)
                         ?? state.Inventory.Artifacts.FirstOrDefault(x => string.Equals(x.Name, a.Name, StringComparison.OrdinalIgnoreCase) &&
                                                                           string.Equals(x.Category, a.Category, StringComparison.OrdinalIgnoreCase));
            if (before is null) { newArtifacts++; newRecords += n; }
            else newRecords += Math.Max(0, n - before.Records);
        }
        long? newFiles = state.Inventory.Files is { } of && files.Count is { } nf ? Math.Max(0, nf - of) : null;
        if (files.Count is null && fileItems.Count > 0)
            warnings.Add("Estrazione non raggiungibile: i file sono confrontati solo con i media del caso. / The extraction is not reachable: files are compared with the case media only.");

        return new ImportPlan { State = state, Matches = matches, Warnings = warnings, NewRecords = newRecords, NewArtifacts = newArtifacts, NewFiles = newFiles };
    }

    /// <summary>Several candidates: the one at the same path (or row id) when there is exactly one, else all, flagged.</summary>
    static ItemMatch Pick(StateItem i, MatchTier tier, string rule, IReadOnlyList<string> keys, string original)
    {
        if (keys.Count == 1) return new ItemMatch(i, tier, rule, [(TagStore.FilePrefix + keys[0], 0)], false);
        var same = keys.Where(k => string.Equals(k, original, StringComparison.OrdinalIgnoreCase)).ToList();
        if (same.Count == 1) return new ItemMatch(i, tier, rule + ", same path among " + keys.Count, [(TagStore.FilePrefix + same[0], 0)], false);
        return new ItemMatch(i, tier, rule + $", {keys.Count} candidates", [.. keys.Select(k => (TagStore.FilePrefix + k, 0L))], true);
    }

    static ItemMatch PickRows(StateItem i, MatchTier tier, string rule, IReadOnlyList<long> rows, string table)
    {
        if (rows.Count == 1) return new ItemMatch(i, tier, rule, [(table, rows[0])], false);
        if (rows.Contains(i.Record!.RowId)) return new ItemMatch(i, tier, rule + ", same row id among " + rows.Count, [(table, i.Record.RowId)], false);
        return new ItemMatch(i, tier, rule + $", {rows.Count} candidates", [.. rows.Select(r => (table, r))], true);
    }

    static IEnumerable<ItemMatch> MatchRecords(SqliteConnection con, ArtifactInfo a, List<StateItem> items, CaseFiles files)
    {
        var shape = RowShape.Of(con, a);
        var have = shape.All.ToHashSet(StringComparer.Ordinal);
        var newSources = SourcesHash(EvidenceOrigin.RecordedSources(a).Select(p => p.Replace('\\', '/').TrimStart('/')).Distinct()
            .Select(p => new StateSource(EvidenceOrigin.DevicePath(p), files.Sha256(p))).ToList());
        // Field sets to hash every row on: each state item names its own (the old parsing's columns).
        var full = items.Where(i => i.Record!.Fields.Count > 0 && i.Record.Fields.All(have.Contains)).ToList();
        var core = items.Where(i => i.Record!.CoreHash is not null && i.Record.CoreFields.All(have.Contains)).ToList();
        var fullSets = full.GroupBy(i => string.Join("\u001F", i.Record!.Fields)).ToDictionary(g => g.Key, g => (Fields: g.First().Record!.Fields,
            Wanted: g.Select(i => i.Record!.ContentHash).ToHashSet(StringComparer.Ordinal), Hits: new Dictionary<string, List<long>>(StringComparer.Ordinal)));
        var coreSets = core.GroupBy(i => string.Join("\u001F", i.Record!.CoreFields)).ToDictionary(g => g.Key, g => (Fields: g.First().Record!.CoreFields,
            Wanted: g.Select(i => i.Record!.CoreHash!).ToHashSet(StringComparer.Ordinal), Hits: new Dictionary<string, List<long>>(StringComparer.Ordinal)));
        var rowIds = items.Select(i => i.Record!.RowId).ToHashSet();
        var byRow = new Dictionary<long, RecordRow>();
        if (shape.All.Count > 0)
            foreach (var row in ArtifactQuery.All(con, a))
            {
                foreach (var set in fullSets.Values)
                    if (shape.ContentHash(row, set.Fields) is var h && set.Wanted.Contains(h)) Add(set.Hits, h, row.RowId);
                foreach (var set in coreSets.Values)
                    if (shape.ContentHash(row, set.Fields) is var h && set.Wanted.Contains(h)) Add(set.Hits, h, row.RowId);
                if (rowIds.Contains(row.RowId)) byRow[row.RowId] = row;
            }

        foreach (var i in items)
        {
            var r = i.Record!;
            if (r.ContentHash == "")
            {
                yield return new ItemMatch(i, MatchTier.None, "row missing when exported", [], false);
                continue;
            }
            if (fullSets.TryGetValue(string.Join("\u001F", r.Fields), out var fs) && fs.Hits.TryGetValue(r.ContentHash, out var rows) && r.Fields.All(have.Contains))
            {
                yield return PickRows(i, MatchTier.Hash, "content hash", rows, a.TableName);
                continue;
            }
            if (r.CoreHash is not null && coreSets.TryGetValue(string.Join("\u001F", r.CoreFields), out var cs) && cs.Hits.TryGetValue(r.CoreHash, out var crows) && r.CoreFields.All(have.Contains))
            {
                yield return PickRows(i, MatchTier.Secondary, $"identity fields ({string.Join(", ", r.CoreFields)})", crows, a.TableName);
                continue;
            }
            // Same row id, the source database unchanged (same SHA-256), and every identity field both parsings have equal.
            if (r.SourceHash is not null && r.SourceHash == newSources && byRow.TryGetValue(r.RowId, out var same))
            {
                var common = r.CoreFieldHashes.Keys.Where(have.Contains).ToList();
                if (common.Count > 0 && common.All(f => shape.FieldHash(same, f) == r.CoreFieldHashes[f]))
                {
                    yield return new ItemMatch(i, MatchTier.Secondary,
                        $"row id + source SHA-256 unchanged, {common.Count}/{r.CoreFieldHashes.Count} identity fields equal", [(a.TableName, r.RowId)], false);
                    continue;
                }
            }
            yield return new ItemMatch(i, MatchTier.None, "no equal hash or key", [], false);
        }

        static void Add(Dictionary<string, List<long>> d, string h, long id)
        {
            if (!d.TryGetValue(h, out var l)) d[h] = l = [];
            l.Add(id);
        }
    }

    /// <summary>Writes the matched tags into the open case, additively: a target with no tag takes the imported tag and
    /// note; a target already tagged keeps its tag, and an imported note it does not already hold is appended. Nothing
    /// is deleted. Fills the counts of <paramref name="plan"/>.</summary>
    public static void Apply(CaseSession s, ImportPlan plan)
    {
        var existing = s.Tags.Rows().GroupBy(t => (t.TableName, t.RowId)).ToDictionary(g => g.Key, g => g.First());
        var writes = new Dictionary<(string, long), (string Tag, string Note)>();
        int tagged = 0, kept = 0, merged = 0;
        foreach (var m in plan.Matches.Where(m => m.Tier != MatchTier.None))
            foreach (var t in m.Targets)
            {
                var cur = writes.TryGetValue(t, out var w) ? new TagEntry(t.Table, t.RowId, w.Tag, w.Note, DateTimeOffset.UtcNow)
                    : existing.GetValueOrDefault(t);
                var note = m.Item.Note.Trim();
                if (cur is null || string.IsNullOrWhiteSpace(cur.Tag))
                {
                    var keep = cur?.Note ?? "";
                    var n = note == "" ? keep : keep == "" ? note : keep.Contains(note, StringComparison.Ordinal) ? keep : note + "\n\n" + keep;
                    writes[t] = (m.Item.Tag == "" ? cur?.Tag ?? "" : m.Item.Tag, n);
                    tagged++;
                    continue;
                }
                var differs = m.Item.Tag != "" && m.Item.Tag != cur.Tag;
                if (differs) kept++;
                var add = differs ? $"[Stato importato / imported state: {m.Item.Tag}]" + (note == "" ? "" : " " + note) : note;
                if (add == "" || cur.Note.Contains(add, StringComparison.Ordinal)) continue;
                writes[t] = (cur.Tag, cur.Note == "" ? add : cur.Note.TrimEnd() + "\n\n" + add);
                merged++;
            }
        s.Tags.SetImported(writes.Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value.Tag, kv.Value.Note)).ToList());
        (plan.Tagged, plan.KeptExisting, plan.NotesMerged, plan.Applied) = (tagged, kept, merged, true);
    }

    // ----------------------------------------------------------------------------------------------------- helpers

    static long Count(SqliteConnection con, ArtifactInfo a)
    {
        try
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {ArtifactQuery.Q(a.TableName)}";
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        catch (SqliteException) { return 0; }
    }

    static string? SourcesHash(IReadOnlyList<StateSource> sources) =>
        sources.Count == 0 || sources.Any(x => x.Sha256 is null) ? null
            : Hex(string.Join("\n", sources.Select(x => x.Sha256).Order(StringComparer.Ordinal)));

    internal static string Hex(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    /// <summary>A column name as compared across parsings: the header, lower case, spaces collapsed.</summary>
    internal static string NormName(string display) => Regex.Replace(display.Trim().ToLowerInvariant(), @"\s+", " ");

    static readonly Regex CoreName = new(
        @"\b(message|msg|body|text|content|sender|recipient|from|to|author|participant|phone|number|address|e-?mail|name|title|subject|url|" +
        @"latitude|longitude|lat|lon|lng|id|guid|uuid|account|contact|handle|username|user|call|duration|bundle|package|app|ssid|bssid|mac|imei|iccid|imsi|amount|hash)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex PathName = new(@"\b(source|path|file|location of|located|report)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex DateName = new(@"\b(time|date|timestamp|created|modified|updated|start|end)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The columns of one artifact as the matching sees them: every non-media column by normalised name, the
    /// identity ("core") columns among them, and how to turn a row into canonical text.</summary>
    sealed class RowShape
    {
        public required IReadOnlyList<ColumnInfo> Columns { get; init; }   // as ArtifactQuery returns them (existing, manifest order)
        public required Dictionary<string, int> Index { get; init; }       // normalised name → value index (first wins)
        public required HashSet<string> PathFields { get; init; }
        public required List<string> Fields { get; init; }
        public required List<string> CoreFields { get; init; }
        public IReadOnlyList<string> All => Fields;

        public static RowShape Of(SqliteConnection con, ArtifactInfo a)
        {
            var cols = ArtifactQuery.ExistingColumns(con, a);
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            var paths = new HashSet<string>(StringComparer.Ordinal);
            var core = new List<string>();
            var conv = a.Conversation;
            var convCols = conv is null ? new HashSet<string>() : new[] { conv.Text, conv.Sender, conv.Discriminator, conv.Direction }.OfType<string>().ToHashSet();
            for (int i = 0; i < cols.Count; i++)
            {
                var c = cols[i];
                if (c.IsMedia) continue; // a media reference id is made up by each run
                var n = NormName(c.Display);
                if (n == "" || index.ContainsKey(n)) continue;
                index[n] = i;
                var isPath = PathName.IsMatch(n) && !convCols.Contains(c.Sql);
                if (isPath) paths.Add(n);
                if (!c.IsDateTime && !isPath && (convCols.Contains(c.Sql) || (CoreName.IsMatch(n) && !DateName.IsMatch(n)))) core.Add(n);
            }
            var fields = index.Keys.Order(StringComparer.Ordinal).ToList();
            core.Sort(StringComparer.Ordinal);
            return new RowShape { Columns = cols, Index = index, PathFields = paths, Fields = fields, CoreFields = core };
        }

        public string? FieldValue(RecordRow row, string field) =>
            Index.TryGetValue(field, out var i) && i < row.Values.Length ? Norm(row.Values[i], PathFields.Contains(field)) : null;

        public string FieldHash(RecordRow row, string field) => Hex(FieldValue(row, field) ?? "")[..16];

        public string ContentHash(RecordRow row, IReadOnlyList<string> fields)
        {
            var sb = new StringBuilder();
            foreach (var f in fields) sb.Append(f).Append('\u001F').Append(FieldValue(row, f) ?? "").Append('\u001E');
            return Hex(sb.ToString());
        }

        public string? Time(RecordRow row)
        {
            for (int i = 0; i < Columns.Count && i < row.Values.Length; i++)
                if (Columns[i].IsDateTime && row.Values[i] is { } v && Convert.ToString(v, CultureInfo.InvariantCulture) is { Length: > 0 } t) return t;
            return null;
        }
    }

    /// <summary>A value as canonical text: null and empty alike, numbers in invariant form (5, "5" and 5.0 alike),
    /// text NFC with \n line ends and trimmed, a blob by its SHA-256, a path from its device root on.</summary>
    internal static string Norm(object? v, bool path = false)
    {
        switch (v)
        {
            case null or DBNull: return "";
            case byte[] b: return "blob:" + Convert.ToHexStringLower(SHA256.HashData(b));
            case long or int or short or byte: return Convert.ToString(v, CultureInfo.InvariantCulture)!;
            case double d: return NormNumber(d);
            case float f: return NormNumber(f);
            case decimal m: return NormNumber((double)m);
        }
        var s = (Convert.ToString(v, CultureInfo.InvariantCulture) ?? "").Normalize(NormalizationForm.FormC).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (s.Length is > 0 and < 40 && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && !s.StartsWith('+') && !(s.Length > 1 && s[0] == '0' && s[1] != '.'))
            return NormNumber(n);
        return path ? NormPath(s) : s;
    }

    static string NormNumber(double d) => d == Math.Floor(d) && Math.Abs(d) < 1e15 ? ((long)d).ToString(CultureInfo.InvariantCulture) : d.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A path from the device's root on ("/data/data/x/db" whatever drive, temp folder or extraction folder
    /// it was recorded under); several paths in one value are each normalised.</summary>
    internal static string NormPath(string s)
    {
        if (!s.Contains('/') && !s.Contains('\\')) return s;
        var parts = s.Split('\n');
        for (int k = 0; k < parts.Length; k++)
        {
            var p = parts[k].Trim().Replace('\\', '/');
            var segs = p.Split('/');
            for (int i = 0; i < segs.Length; i++)
                if (Roots.Contains(segs[i]) || segs[i].EndsWith("Domain", StringComparison.Ordinal) || segs[i].Contains("Domain-", StringComparison.Ordinal))
                {
                    p = "/" + string.Join('/', segs[i..]);
                    break;
                }
            parts[k] = p;
        }
        return string.Join('\n', parts);
    }

    static readonly HashSet<string> Roots = ["data", "sdcard", "storage", "private", "var", "system"];

    /// <summary>The files an item can name: the extraction's files by their path in it, and the case's media by the
    /// device path the gallery tags them under. Hashes are computed once per file, on demand.</summary>
    sealed class CaseFiles
    {
        readonly CaseSession _s;
        readonly ExtractionFiles? _x;
        Dictionary<string, MediaRef>? _media;
        List<(string Key, long Size)>? _all;
        readonly Dictionary<string, string?> _hashes = new(StringComparer.Ordinal);

        public CaseFiles(CaseSession s)
        {
            _s = s;
            try { _x = s.Extraction is { Locked: false } x ? x : null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }

        Dictionary<string, MediaRef> Media()
        {
            if (_media is not null) return _media;
            _media = new Dictionary<string, MediaRef>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var con = _s.OpenDb();
                foreach (var m in new MediaResolver(_s).All(con, int.MaxValue))
                    if (m.Exists && TagStore.FileKey(m.SourcePath ?? m.ExtractionPath ?? m.Name) is { Length: > 0 } k) _media.TryAdd(k, m);
            }
            catch (SqliteException) { }
            return _media;
        }

        /// <summary>Extraction files plus case media; null when the extraction is not reachable.</summary>
        public long? Count => _x is null ? null : All().Count;

        public List<(string Key, long Size)> All()
        {
            if (_all is not null) return _all;
            var list = new List<(string, long)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_x is not null)
                foreach (var (p, size) in _x.Files())
                    if (seen.Add(TagStore.FileKey(p))) list.Add((TagStore.FileKey(p), size));
            foreach (var (k, m) in Media())
                if (seen.Add(k)) list.Add((k, m.Size));
            return _all = list;
        }

        public long? SizeOf(string key)
        {
            if (_x is not null && _x.Contains(key) && _x.SizeOf(key) is >= 0 and var n) return n;
            return Media().TryGetValue(key, out var m) ? m.Size : null;
        }

        public string? Sha256(string key)
        {
            if (_hashes.TryGetValue(key, out var h)) return h;
            h = null;
            if (_x is not null && _x.Contains(key)) h = _x.Read(key, st => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(st)));
            if (h is null && Media().TryGetValue(key, out var m))
                try { using var st = m.OpenRead(); h = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(st)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { }
            return _hashes[key] = h;
        }
    }
}
