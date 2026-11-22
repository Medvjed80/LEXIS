using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AleappReader.Core;

/// <summary>One category the classifier gave an image, with its confidence (0–1).</summary>
public sealed record CategoryScore(string Name, double Confidence);

/// <summary>What a categorizer said of one image: its scores (only names from the examiner's list), a short reason,
/// whether the model refused, or the error that stopped this image (the batch goes on).</summary>
public sealed record CategorizationOutcome(IReadOnlyList<CategoryScore> Scores, string Reason, bool Refused = false, string? Error = null, string Raw = "")
{
    public const string Ok = "ok", RefusedStatus = "refused", ErrorStatus = "error";
    public string Status => Error is not null ? ErrorStatus : Refused ? RefusedStatus : Ok;
    public static CategorizationOutcome Failed(string error) => new([], "", Error: error);
}

/// <summary>
/// An image classifier behind the gallery's "Categorize images": the configured vision LLM today
/// (<see cref="VisionLlmCategorizer"/>), a local ONNX model (<see cref="OnnxCategorizer"/>, e.g. a fine-tuned CLIP or ViT
/// exported to ONNX) beside it. <see cref="Engine"/>, <see cref="Model"/> and <see cref="PromptVersion"/> name the run, so
/// an image already categorized by the same model and prompt version is skipped when the run resumes.
/// </summary>
public interface IImageCategorizer : IDisposable
{
    /// <summary>"Vision LLM (Ollama)", "ONNX"…</summary>
    string Engine { get; }
    string Model { get; }
    /// <summary>Changes with the prompt and the category list: a new list categorizes again.</summary>
    string PromptVersion { get; }
    /// <summary>Whether the image leaves this machine (a remote server): the examiner confirms before.</summary>
    bool Remote { get; }
    Task<CategorizationOutcome> Categorize(AiImage image, CancellationToken ct);
}

/// <summary>The prompt, the JSON schema and the parsing of the answer, kept apart from any server so they can be tested.</summary>
public static class ImageCategorization
{
    /// <summary>Bump when the wording or the schema changes: earlier results then no longer count as done.</summary>
    public const string PromptRevision = "cat-v1";
    public const string TagPrefix = "AI: ";
    public const double DefaultThreshold = 0.6;

    public static readonly string[] DefaultCategories =
        ["Drugs", "Money/Cash", "Weapons", "Documents/IDs", "Vehicles", "Screenshots/Chats", "Nudity/Sexual", "Minors present", "Alcohol", "Other/None"];

    /// <summary>The list as typed: one per line (or comma separated), trimmed, without blanks or repeats.</summary>
    public static List<string> ParseList(string text) =>
        [.. text.Split(['\n', '\r', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The prompt revision with a hash of the list: editing the list makes a new version.</summary>
    public static string VersionOf(IReadOnlyList<string> categories) =>
        PromptRevision + "-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", categories))))[..8];

    public const string System =
        "You are an image triage assistant for a forensic examiner. You only label what is visible in the image. " +
        "You never identify people. You answer with JSON only.";

    public static string Prompt(IReadOnlyList<string> categories) =>
        "Label this image with the categories from this list that apply, each with a confidence from 0 to 1:\n" +
        string.Join("\n", categories.Select(c => "- " + c)) + "\n\n" +
        "Use only names from the list, exactly as written. Leave out categories that do not apply. " +
        "Give a short reason (one sentence) describing what is visible. " +
        "Answer with JSON only, in this form: {\"categories\":[{\"name\":\"...\",\"confidence\":0.0}],\"reason\":\"...\"}. " +
        "If you will not label this image, answer {\"categories\":[],\"reason\":\"refused: <why>\"}.";

    /// <summary>The JSON schema the answer must follow (Ollama's <c>format</c>, OpenAI's <c>response_format</c>): names
    /// restricted to the list.</summary>
    public static JsonObject Schema(IReadOnlyList<string> categories) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["categories"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["name"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray([.. categories.Select(c => (JsonNode)c)]) },
                        ["confidence"] = new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 },
                    },
                    ["required"] = new JsonArray("name", "confidence"),
                },
            },
            ["reason"] = new JsonObject { ["type"] = "string" },
        },
        ["required"] = new JsonArray("categories", "reason"),
    };

    static readonly Regex RefusalWords = new(@"\b(I can(no|')t|I cannot|I'm (not able|unable)|I am (not able|unable)|I won't|I will not|sorry|cannot (help|assist)|not able to (help|assist|comply)|refus)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads the model's answer: the JSON object (also inside ```json fences or after text), names matched to the list
    /// without regard to case (others dropped, the best confidence kept for a name given twice), confidences clamped to
    /// 0–1 (a percentage such as 85 read as 0.85; a value just over 1 taken as 1). A refusal is an answer with no categories whose reason starts with
    /// "refused", or a plain-text answer that says so; text that is neither is an error.
    /// </summary>
    public static CategorizationOutcome Parse(string raw, IReadOnlyList<string> categories)
    {
        var text = (raw ?? "").Trim();
        var json = ExtractJson(text);
        if (json is null)
            return RefusalWords.IsMatch(text) ? new([], Short(text), Refused: true, Raw: raw ?? "")
                : CategorizationOutcome.Failed("The answer is not JSON: " + Short(text)) with { Raw = raw ?? "" };
        var byName = categories.ToDictionary(c => c.Trim(), c => c, StringComparer.OrdinalIgnoreCase);
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        if (json["categories"] is JsonArray arr)
            foreach (var item in arr)
            {
                string? name; double conf;
                if (item is JsonValue v && v.TryGetValue<string>(out var s)) (name, conf) = (s, 1.0); // ["Drugs"]: no confidence given
                else
                {
                    name = (item?["name"] as JsonValue)?.TryGetValue<string>(out var n) == true ? n : null;
                    conf = Number(item?["confidence"]) ?? 1.0;
                }
                if (name is null || !byName.TryGetValue(name.Trim(), out var canon)) continue;
                if (conf > 1.5 && conf <= 100) conf /= 100; // 85 is a percentage; 1.2 an over-confident 1
                conf = Math.Clamp(double.IsNaN(conf) ? 0 : conf, 0, 1);
                scores[canon] = Math.Max(scores.GetValueOrDefault(canon), conf);
            }
        var reason = (json["reason"] as JsonValue)?.TryGetValue<string>(out var r) == true ? r.Trim() : "";
        var refused = scores.Count == 0 && (reason.StartsWith("refus", StringComparison.OrdinalIgnoreCase) || json["refused"] is JsonValue rv && rv.TryGetValue<bool>(out var b) && b);
        return new([.. scores.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new CategoryScore(kv.Key, Math.Round(kv.Value, 3)))],
                   reason, refused, Raw: raw ?? "");
    }

    static double? Number(JsonNode? n) => n is JsonValue v
        ? v.TryGetValue<double>(out var d) ? d
        : v.TryGetValue<string>(out var s) && double.TryParse(s.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? (s.Contains('%') ? p / 100 : p)
        : null : null;

    static JsonObject? ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        while (start >= 0)
        {
            for (var end = text.LastIndexOf('}'); end > start; end = text.LastIndexOf('}', end - 1))
                try { if (JsonNode.Parse(text[start..(end + 1)]) is JsonObject o) return o; }
                catch (JsonException) { }
            start = text.IndexOf('{', start + 1);
        }
        return null;
    }

    static string Short(string s) => s.Length > 200 ? s[..200] + "…" : s;

    /// <summary>The categories at or above the threshold, best first.</summary>
    public static IReadOnlyList<CategoryScore> Above(IEnumerable<CategoryScore> scores, double threshold) =>
        [.. scores.Where(s => s.Confidence >= threshold - 1e-9).OrderByDescending(s => s.Confidence)];

    /// <summary>The images still to do: those with no result for this engine, model and prompt version (an error is
    /// tried again; a refusal is a result). Duplicates by media id once.</summary>
    public static List<MediaRef> Pending(IEnumerable<MediaRef> images, IReadOnlySet<string> done) =>
        [.. images.DistinctBy(m => m.RefId).Where(m => !done.Contains(m.RefId))];

    /// <summary>The tag an AI category becomes: "AI: Drugs". Marked AI-generated by its prefix.</summary>
    public static string TagOf(string category) => TagPrefix + category;
}

/// <summary>One image's stored categorization (the newest for that image).</summary>
public sealed record ImageCategoryResult(long Id, DateTimeOffset At, string RefId, string Name, string Path, string Sha256, string Engine, string Model,
    string PromptVersion, IReadOnlyList<CategoryScore> Scores, string Reason, string Status, string? Error)
{
    public bool Refused => Status == CategorizationOutcome.RefusedStatus;
}

/// <summary>The categorizations in the case's AI base (<see cref="AiStore"/>'s _reader_ai.db, table image_categories):
/// one row per image, engine, model and prompt version, with the image's SHA-256 and when.</summary>
public sealed class ImageCategoryStore(string path)
{
    public string Path => path;
    bool _schema;

    public static ImageCategoryStore For(CaseSession s) => new(AiStore.For(s).Path);

    SqliteConnection Open()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        con.Open();
        if (_schema) return con;
        _schema = true;
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS image_categories(id INTEGER PRIMARY KEY, at INTEGER, ref_id TEXT, name TEXT, path TEXT, sha256 TEXT,
              engine TEXT, model TEXT, prompt_version TEXT, categories TEXT, reason TEXT, status TEXT, error TEXT, send_id INTEGER);
            CREATE UNIQUE INDEX IF NOT EXISTS image_categories_run ON image_categories(ref_id, engine, model, prompt_version);
            """;
        cmd.ExecuteNonQuery();
        return con;
    }

    /// <summary>Stores (or replaces) the result of this image for this engine, model and prompt version.</summary>
    public void Save(MediaRef m, string sha256, IImageCategorizer c, CategorizationOutcome o, long sendId = 0) =>
        Save(m.RefId, m.Name, m.SourcePath ?? m.ExtractionPath ?? m.Name, sha256, c.Engine, c.Model, c.PromptVersion, o, sendId);

    public void Save(string refId, string name, string devicePath, string sha256, string engine, string model, string version, CategorizationOutcome o, long sendId = 0)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            INSERT INTO image_categories(at,ref_id,name,path,sha256,engine,model,prompt_version,categories,reason,status,error,send_id)
            VALUES(@at,@r,@n,@p,@h,@e,@m,@v,@c,@why,@s,@err,@send)
            ON CONFLICT(ref_id,engine,model,prompt_version) DO UPDATE SET at=excluded.at, name=excluded.name, path=excluded.path,
              sha256=excluded.sha256, categories=excluded.categories, reason=excluded.reason, status=excluded.status, error=excluded.error, send_id=excluded.send_id
            """;
        cmd.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("@r", refId);
        cmd.Parameters.AddWithValue("@n", name);
        cmd.Parameters.AddWithValue("@p", devicePath);
        cmd.Parameters.AddWithValue("@h", sha256);
        cmd.Parameters.AddWithValue("@e", engine);
        cmd.Parameters.AddWithValue("@m", model);
        cmd.Parameters.AddWithValue("@v", version);
        cmd.Parameters.AddWithValue("@c", JsonSerializer.Serialize(o.Scores.Select(s => new { name = s.Name, confidence = s.Confidence })));
        cmd.Parameters.AddWithValue("@why", o.Reason);
        cmd.Parameters.AddWithValue("@s", o.Status);
        cmd.Parameters.AddWithValue("@err", (object?)o.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@send", sendId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>The media ids already categorized (or refused) by this engine, model and prompt version; errors are not.</summary>
    public HashSet<string> Done(string engine, string model, string version)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return set;
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT ref_id FROM image_categories WHERE engine=@e AND model=@m AND prompt_version=@v AND status <> 'error'";
        cmd.Parameters.AddWithValue("@e", engine);
        cmd.Parameters.AddWithValue("@m", model);
        cmd.Parameters.AddWithValue("@v", version);
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    /// <summary>Every stored result, newest first.</summary>
    public List<ImageCategoryResult> All()
    {
        var list = new List<ImageCategoryResult>();
        if (!File.Exists(path)) return list;
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT id,at,ref_id,name,path,sha256,engine,model,prompt_version,categories,reason,status,error FROM image_categories ORDER BY at DESC, id DESC";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new(r.GetInt64(0), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
                r.GetString(6), r.GetString(7), r.GetString(8), ReadScores(r.GetString(9)), r.GetString(10), r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12)));
        return list;
    }

    /// <summary>The newest result of each image that is not an error (an error does not hide an earlier result).</summary>
    public Dictionary<string, ImageCategoryResult> Latest()
    {
        var d = new Dictionary<string, ImageCategoryResult>(StringComparer.Ordinal);
        foreach (var x in All()) if (x.Status != CategorizationOutcome.ErrorStatus) d.TryAdd(x.RefId, x);
        return d;
    }

    static List<CategoryScore> ReadScores(string json)
    {
        try
        {
            return [.. (JsonNode.Parse(json) as JsonArray ?? []).Select(n => new CategoryScore((string?)n?["name"] ?? "", (double?)n?["confidence"] ?? 0)).Where(s => s.Name != "")];
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException) { return []; }
    }

    /// <summary>Every result as CSV (UTF-8 with BOM, for Excel): the image, its SHA-256, engine, model, prompt version, when
    /// (UTC), status, the categories at or above <paramref name="threshold"/>, every score, the reason, the error.
    /// Labelled AI-generated and unverified in its first line.</summary>
    public static string Csv(IEnumerable<ImageCategoryResult> rows, double threshold)
    {
        static string Q(string? s) => s is null ? "" : s.IndexOfAny([',', '"', '\n', '\r']) >= 0 || s.StartsWith('=') || s.StartsWith('+') || s.StartsWith('-') || s.StartsWith('@')
            ? "\"" + (s.StartsWith('=') || s.StartsWith('+') || s.StartsWith('-') || s.StartsWith('@') ? "'" : "") + s.Replace("\"", "\"\"") + "\"" : s;
        var sb = new StringBuilder();
        sb.Append("# AI-generated image categories: review aid, not verified by the examiner. Threshold ")
          .Append(threshold.ToString("0.##", CultureInfo.InvariantCulture)).Append("\r\n");
        sb.Append("file,device_path,sha256,engine,model,prompt_version,categorized_utc,status,categories_above_threshold,all_scores,reason,error\r\n");
        foreach (var x in rows)
            sb.Append(string.Join(",", Q(x.Name), Q(x.Path), Q(x.Sha256), Q(x.Engine), Q(x.Model), Q(x.PromptVersion),
                    Q(x.At.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)), Q(x.Status),
                    Q(string.Join("; ", ImageCategorization.Above(x.Scores, threshold).Select(s => s.Name))),
                    Q(string.Join("; ", x.Scores.Select(s => $"{s.Name}={s.Confidence.ToString("0.###", CultureInfo.InvariantCulture)}"))),
                    Q(x.Reason), Q(x.Error)))
              .Append("\r\n");
        return sb.ToString();
    }
}

/// <summary>The vision role of AI settings as a categorizer: one image per request, the answer held to the JSON schema
/// (Ollama <c>format</c>, OpenAI-compatible <c>response_format</c>), temperature 0, Ollama without thinking.</summary>
public sealed class VisionLlmCategorizer(AiClient client, string model, IReadOnlyList<string> categories, int? context = null) : IImageCategorizer
{
    public string Engine => $"Vision LLM ({client.Provider.Name})";
    public string Model => model;
    public string PromptVersion { get; } = ImageCategorization.VersionOf(categories);
    public bool Remote => client.Provider.Where == "remote";
    public string Prompt { get; } = ImageCategorization.Prompt(categories);

    public async Task<CategorizationOutcome> Categorize(AiImage image, CancellationToken ct)
    {
        string raw;
        try { raw = await client.ChatJson(model, ImageCategorization.System, Prompt, [image], ImageCategorization.Schema(categories), ct, context); }
        catch (AiException e) { return CategorizationOutcome.Failed(e.Message); }
        return ImageCategorization.Parse(raw, categories);
    }

    public void Dispose() { }
}
