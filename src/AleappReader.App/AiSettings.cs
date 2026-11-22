using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using AleappReader.Core;

namespace AleappReader.App;

public sealed class AiProviderSetting
{
    public string Name { get; set; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter<AiKind>))] public AiKind Kind { get; set; } = AiKind.OpenAi;
    public string Address { get; set; } = "";
    /// <summary>The API key encrypted with DPAPI for this Windows user (base64), never the key itself.</summary>
    public string? ProtectedKey { get; set; }

    [JsonIgnore] public AiProvider Provider => new(Name, Kind, Address);
    [JsonIgnore] public bool HasKey => !string.IsNullOrEmpty(ProtectedKey);

    public string? Key()
    {
        if (!HasKey) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedKey!), Entropy, DataProtectionScope.CurrentUser)); }
        catch (Exception e) when (e is CryptographicException or FormatException) { return null; } // another user's or machine's settings
    }

    public void SetKey(string? key) =>
        ProtectedKey = string.IsNullOrWhiteSpace(key) ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Entropy, DataProtectionScope.CurrentUser));

    static readonly byte[] Entropy = "LEXIS AI endpoint key"u8.ToArray();

    public AiClient Client() => new(Provider, Key()) { MaxContext = Settings.Current.Ai.MaxContext is > 0 and var m ? m : null };
}

/// <summary>Whether AI is on, and which jobs have a model, for the AI buttons and menus to bind to
/// ({Binding Text, Source={x:Static local:AiState.Instance}}): a job without a model leaves its buttons disabled. An
/// instance, not a static property: WPF's binding to a static property threw "Value cannot be null (key)" when the main window opened.</summary>
public sealed class AiState : System.ComponentModel.INotifyPropertyChanged
{
    public static AiState Instance { get; } = new();
    public bool Enabled => Settings.Current.Ai.Enabled;
    /// <summary>Translate, summarise, explain, questions.</summary>
    public bool Text => Settings.Current.Ai.Ready(Settings.Current.Ai.Text);
    /// <summary>Describe, classify and read (OCR) images.</summary>
    public bool Vision => Settings.Current.Ai.Ready(Settings.Current.Ai.Vision);
    public bool Audio => Settings.Current.Ai.Ready(Settings.Current.Ai.Transcription);
    /// <summary>Analyse videos: frames to the video model (the vision model when none is set).</summary>
    public bool Video => Settings.Current.Ai.Ready(Settings.Current.Ai.VideoRole);
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public static void Changed() => Instance.PropertyChanged?.Invoke(Instance, new(""));
}

/// <summary>Which provider and model does a job (text, vision, transcription).</summary>
public sealed class AiRole
{
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
}

public sealed class AiTheme
{
    public string Name { get; set; } = "";
    public string Indicators { get; set; } = "";
}

/// <summary>AI preferences: off until the examiner turns it on. Ollama and LM Studio are listed by default.</summary>
public sealed class AiSettings
{
    public bool Enabled { get; set; }
    public List<AiProviderSetting> Providers { get; set; } =
    [
        new() { Name = "Ollama", Kind = AiKind.Ollama, Address = "http://localhost:11434" },
        new() { Name = "LM Studio", Kind = AiKind.OpenAi, Address = "http://localhost:1234/v1" },
    ];
    /// <summary>The local engine in use: "Ollama" or "LM Studio" (both kept in <see cref="Providers"/> with their addresses).</summary>
    public string LocalEngine { get; set; } = "";
    public AiRole Text { get; set; } = new();
    public AiRole Vision { get; set; } = new();
    /// <summary>The model the frames of a video go to; empty: the vision model's.</summary>
    public AiRole Video { get; set; } = new();
    [JsonIgnore] public AiRole VideoRole => Video.Provider != "" && Video.Model.Trim() != "" ? Video : Vision;
    /// <summary>A frame every <see cref="VideoPercent"/> % of the duration, else every <see cref="VideoSeconds"/> s; at most <see cref="VideoMaxFrames"/>.</summary>
    public bool VideoByPercent { get; set; } = true;
    public int VideoPercent { get; set; } = 10;
    public int VideoSeconds { get; set; } = 5;
    public int VideoMaxFrames { get; set; } = 30;
    /// <summary>The video's audio track is transcribed too, when audio has an engine.</summary>
    public bool VideoTranscribe { get; set; } = true;
    /// <summary>On opening a case, when whisper.cpp is the transcription engine, transcribe audio that has no transcript
    /// yet and save each transcript in the file's note. A cloud engine is never started for this.</summary>
    public bool AutoTranscribeNotes { get; set; } = true;
    /// <summary>whisper.cpp unless another speech-to-text server is chosen.</summary>
    public AiRole Transcription { get; set; } = new() { Provider = WhisperName };
    /// <summary>The Claude CLI executable; it runs only when allowed here and confirmed at each run.</summary>
    public string ClaudePath { get; set; } = "claude";
    /// <summary>A job uses the Claude CLI (set on Save from the jobs): choosing it is the permission.</summary>
    public bool ClaudeAllowed { get; set; }
    /// <summary>"Ask Claude" and the complete analysis's exploration: when the Claude CLI does the text job.</summary>
    [JsonIgnore] public bool ClaudeExplores => ClaudeAllowed && Text.Provider == ClaudeName;
    /// <summary>whisper.cpp's whisper-cli.exe: transcription on this machine. Empty until the examiner gives it.</summary>
    public string WhisperPath { get; set; } = "";
    /// <summary>The MCP server inside LEXIS is on: other programs (LM Studio, Claude Desktop…) read the case open in
    /// LEXIS through it, read-only, on the loopback only.</summary>
    public bool McpAllowed { get; set; }
    public int McpPort { get; set; } = 47811;
    /// <summary>The secret part of the server's address, made once: the configurations given to other programs keep working.</summary>
    public string McpToken { get; set; } = "";
    [JsonIgnore] public string McpUrl => McpHttpServer.Address(McpPort, McpToken);
    /// <summary>"Explain" may look up on the web what the app's data format means: only the app, artifact, database file
    /// and column names leave the machine, shown and confirmed at every run. Off unless turned on.</summary>
    public bool ExplainWebLookup { get; set; }
    public string WebSearchUrl { get; set; } = WebLookup.DefaultUrl;
    public string TranslateTo { get; set; } = "English";
    /// <summary>Language of the AI's answers (summaries, answers, reasons); the records' own words are quoted as they are.</summary>
    public string AnswerLanguage { get; set; } = "English";
    /// <summary>Most records (messages) sent in one request; a summary over more goes in steps carrying the summary so far.</summary>
    public int MaxRecordsPerRequest { get; set; } = 20;
    /// <summary>Requests sent to the model at once, for the jobs whose requests are independent (images, audio, the
    /// thematic review, Explain, questions). 1: one after the other.</summary>
    public int ParallelRequests { get; set; } = 1;
    /// <summary>The most context (num_ctx) asked of Ollama, whatever the model advertises; 0: no cap. 16,384 tokens keeps
    /// a mid-size model inside an 8 GB graphics card.</summary>
    public int MaxContext { get; set; } = AiClient.DefaultMaxContext;

    // ---- Categorize images (gallery) ----
    public const string VisionLlmCategorizer = "Vision LLM", OnnxCategorizer = "ONNX model";
    /// <summary>The categories "Categorize images" may give, in the examiner's words; editable in AI settings.</summary>
    public List<string> ImageCategories { get; set; } = [.. ImageCategorization.DefaultCategories];
    /// <summary>A category counts (badge, filter, tag) from this confidence on.</summary>
    public double CategoryThreshold { get; set; } = ImageCategorization.DefaultThreshold;
    /// <summary>Tag each untagged image with its best category above the threshold ("AI: Drugs"). Off by default.</summary>
    public bool CategoriesAsTags { get; set; }
    /// <summary><see cref="VisionLlmCategorizer"/> (the vision role) or <see cref="OnnxCategorizer"/> (a local .onnx model).</summary>
    public string Categorizer { get; set; } = VisionLlmCategorizer;
    public string OnnxModelPath { get; set; } = "";
    public string OnnxLabelsPath { get; set; } = "";
    public int OnnxInputSize { get; set; } = 224;
    [JsonConverter(typeof(JsonStringEnumConverter<OnnxActivation>))] public OnnxActivation OnnxActivation { get; set; } = OnnxActivation.Sigmoid;
    /// <summary>Per-channel mean and standard deviation of 0–1 RGB, comma separated; empty: ImageNet's.</summary>
    public string OnnxMean { get; set; } = "";
    public string OnnxStd { get; set; } = "";

    /// <summary>The ONNX preprocessing as set (bad numbers fall back to ImageNet's).</summary>
    [JsonIgnore] public OnnxOptions OnnxOptions => new(Math.Clamp(OnnxInputSize, 32, 1024), Floats(OnnxMean), Floats(OnnxStd), OnnxActivation);

    static float[]? Floats(string s)
    {
        var parts = s.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries);
        var list = new List<float>();
        foreach (var p in parts)
            if (float.TryParse(p, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f)) list.Add(f);
            else return null;
        return list.Count == 3 ? [.. list] : null;
    }
    public string VisionCategories { get; set; } =
        "weapons, drugs, identity documents, cash or payment cards, screenshots, vehicles, people, documents, other";
    public List<AiTheme> Themes { get; set; } =
    [
        new()
        {
            Name = "Drug dealing",
            Indicators = """
                - prices together with quantities or weights
                - names or street names of substances
                - arranging a handover: place, time, "the usual place", "outside"
                - payment for goods: cash, transfers, vouchers, crypto
                - care to avoid detection: deleting messages, moving to another app, code words
                """,
        },
    ];

    public const string ClaudeName = "Claude CLI", WhisperName = "whisper.cpp";

    /// <summary>The provider of a job; the Claude CLI only while it is allowed (it is not one of the stored servers).</summary>
    public AiProviderSetting? ProviderOf(AiRole r) => r.Provider switch
    {
        ClaudeName => ClaudeAllowed ? new AiProviderSetting { Name = ClaudeName, Kind = AiKind.ClaudeCli, Address = ClaudePath } : null,
        WhisperName => WhisperPath != "" ? new AiProviderSetting { Name = WhisperName, Kind = AiKind.WhisperCpp, Address = WhisperPath } : null,
        _ => Providers.FirstOrDefault(p => p.Name == r.Provider),
    };

    /// <summary>AI is on and the job has an engine and a model: its functions are offered.</summary>
    public bool Ready(AiRole r) => Enabled && ProviderOf(r) is not null && r.Model.Trim() != "";
}
