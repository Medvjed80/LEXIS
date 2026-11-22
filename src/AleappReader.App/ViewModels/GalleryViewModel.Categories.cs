using System.IO;
using System.Security.Cryptography;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AleappReader.Core;

namespace AleappReader.App.ViewModels;

/// <summary>"Categorize images": each picture labelled with the examiner's categories by the vision LLM of AI settings or
/// a local ONNX model, behind <see cref="IImageCategorizer"/>. Results go to the case's AI base (table image_categories),
/// show as badges and a filter, and may become "AI: …" tags. AI-generated and unverified: a review aid, not a finding.</summary>
public partial class GalleryViewModel
{
    public const string AllCategories = "All categories", AnyCategory = "Any AI category", NotCategorized = "Not categorized", Refused = "Model refused";
    static readonly HashSet<string> VisionMimes = new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png", "image/gif", "image/webp" };

    Dictionary<string, ImageCategoryResult> _categories = new(StringComparer.Ordinal);
    [ObservableProperty] private IReadOnlyList<string> categoryFilters = [AllCategories];
    [ObservableProperty] private string categoryFilter = AllCategories;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(NotCategorizing))] private bool categorizing;
    public bool NotCategorizing => !Categorizing;
    CancellationTokenSource? _categorizeCts;

    static double Threshold => Math.Clamp(Settings.Current.Ai.CategoryThreshold, 0, 1);

    Dictionary<string, ImageCategoryResult> ReadCategories()
    {
        try { return ImageCategoryStore.For(_s).Latest(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { return new(StringComparer.Ordinal); }
    }

    void RefreshCategoryFilters()
    {
        var names = Settings.Current.Ai.ImageCategories.Concat(_categories.Values.SelectMany(c => c.Scores.Select(s => s.Name)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var keep = CategoryFilter;
        CategoryFilters = [AllCategories, AnyCategory, NotCategorized, Refused, .. names];
        if (!CategoryFilters.Contains(keep)) CategoryFilter = AllCategories;
    }

    bool OfCategory(MediaRef m, double threshold)
    {
        if (CategoryFilter == AllCategories) return true;
        var c = _categories.GetValueOrDefault(m.RefId);
        return CategoryFilter switch
        {
            NotCategorized => c is null && m.Kind == "image",
            Refused => c is { Refused: true },
            AnyCategory => c is not null && ImageCategorization.Above(c.Scores, threshold).Count > 0,
            _ => c is not null && ImageCategorization.Above(c.Scores, threshold).Any(s => string.Equals(s.Name, CategoryFilter, StringComparison.OrdinalIgnoreCase)),
        };
    }

    GalleryItem WithBadge(GalleryItem i, double threshold)
    {
        if (_categories.GetValueOrDefault(i.Media.RefId) is not { } c) return i;
        var above = ImageCategorization.Above(c.Scores, threshold);
        i.Categories = c.Refused ? "AI: model refused" : above.Count == 0 ? "" : "AI: " + string.Join(" · ", above.Select(s => $"{s.Name} {s.Confidence:0.00}"));
        i.CategoriesTip = $"AI-generated, unverified: review aid, not a finding.\n{c.Engine} · {c.Model} · {c.PromptVersion} · {ValueFormatter.FormatTime(c.At)}\n" +
                          (c.Scores.Count > 0 ? string.Join(", ", c.Scores.Select(s => $"{s.Name} {s.Confidence:0.00}")) + "\n" : "") + c.Reason;
        return i;
    }

    /// <summary>Images among the tiles selected in the gallery.</summary>
    [RelayCommand]
    Task CategorizeSelected(object? selected) => Guarded(() =>
    {
        var items = Chosen(selected);
        if (items.Count == 0) { _main.Status = "Select one or more images first (click, Ctrl+click, Shift+click)."; return Task.CompletedTask; }
        return Categorize(items.Select(i => i.Media), "the selected images");
    });

    /// <summary>Every image the gallery's filters keep, on every page, checked or not.</summary>
    [RelayCommand] Task CategorizeFiltered() => Guarded(() => Categorize(Filtered().Select(i => i.Media), "the images the filters keep"));

    /// <summary>Every image of the gallery (its current source), whatever the filters.</summary>
    [RelayCommand] Task CategorizeAll() => Guarded(() => Categorize(_all.Select(e => e.M), "every image of the gallery"));

    [RelayCommand] void CancelCategorize() => _categorizeCts?.Cancel();

    /// <summary>The categorizer AI settings choose, or null with the reason in the status bar.</summary>
    async Task<(IImageCategorizer C, AiProvider? Provider, string Prompt)?> Categorizer()
    {
        var ai = Settings.Current.Ai;
        if (ai.Categorizer == AiSettings.OnnxCategorizer)
        {
            if (ai.OnnxModelPath == "" || ai.OnnxLabelsPath == "") { _main.Status = "Categorize images: choose the ONNX model and its labels.txt in AI settings."; return null; }
            var onnx = new OnnxCategorizer(ai.OnnxModelPath, ai.OnnxLabelsPath, ai.OnnxOptions, ImageDecode.Rgb);
            try { await Task.Run(onnx.Load); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException
                                           or DllNotFoundException or TypeInitializationException or InvalidOperationException)
            {
                onnx.Dispose();
                _main.Status = "Categorize images: the ONNX model cannot be opened: " + e.Message;
                return null;
            }
            return (onnx, null, "");
        }
        if (!ai.Ready(ai.Vision) || ai.ProviderOf(ai.Vision) is not { } p)
        {
            _main.Status = "Categorize images: turn AI on and choose a vision model in AI settings (or choose an ONNX model as categorizer).";
            return null;
        }
        if (p.Kind is AiKind.WhisperCpp) { _main.Status = "Categorize images: whisper.cpp only transcribes audio; choose a vision model."; return null; }
        var client = p.Client();
        int? context = null;
        try { context = await client.ContextOf(ai.Vision.Model); } catch (AiException) { }
        var cats = ai.ImageCategories.Count > 0 ? ai.ImageCategories : [.. ImageCategorization.DefaultCategories];
        var llm = new VisionLlmCategorizer(client, ai.Vision.Model, cats, context);
        return (llm, p.Provider, llm.Prompt);
    }

    /// <summary>The file's bytes: the case copy, else read from the extraction (read-only); null when unreadable.</summary>
    byte[]? Bytes(MediaRef m)
    {
        try
        {
            if (m.Exists) return m.ReadAllBytes();
            if (m.SourcePath is { } p && _s.Extraction is { } x) return x.Read(p, 64L << 20);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ObjectDisposedException) { }
        return null;
    }

    static string MimeOf(MediaRef m) => m.MimeType is { Length: > 0 } t ? t.ToLowerInvariant() : Path.GetExtension(m.Name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".gif" => "image/gif", ".webp" => "image/webp", _ => "",
    };

    async Task Categorize(IEnumerable<MediaRef> media, string what)
    {
        if (Categorizing) { _main.Status = "Categorize images is already running: stop it first."; return; }
        var images = media.Where(m => m.Kind == "image").DistinctBy(m => m.RefId).ToList();
        if (images.Count == 0) { _main.Status = $"No images in {what}."; return; }
        var made = await Categorizer();
        if (made is null) return;
        var (c, provider, prompt) = made.Value;
        using var _ = c;
        var store = ImageCategoryStore.For(_s);
        var aiStore = AiStore.For(_s);
        var done = await Task.Run(() => store.Done(c.Engine, c.Model, c.PromptVersion));
        var pending = ImageCategorization.Pending(images, done);
        var resumed = images.Count - pending.Count;
        if (pending.Count == 0) { _main.Status = $"All {images.Count:N0} images of {what} are already categorized by {c.Model} ({c.PromptVersion})."; return; }
        var where = provider is null ? "on this computer (ONNX)" : $"to {c.Model} on {provider.Name} ({provider.WhereText})";
        if (c.Remote && MessageBox.Show(Application.Current.MainWindow,
                $"{pending.Count:N0} images will be sent {where}: they leave this computer.\n\nContinue?",
                "Categorize images", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        var ai = Settings.Current.Ai;
        var threshold = Threshold;
        _categorizeCts = new CancellationTokenSource();
        var ct = _categorizeCts.Token;
        Categorizing = true;
        int n = 0, ok = 0, refused = 0, failed = 0;
        var toTag = new List<(MediaRef M, string Tag)>();
        try
        {
            foreach (var m in pending)
            {
                ct.ThrowIfCancellationRequested();
                n++;
                Status = $"Categorizing image {n:N0} of {pending.Count:N0} {where}" + (resumed > 0 ? $" ({resumed:N0} already done)" : "") +
                         (failed > 0 ? $" · {failed:N0} failed" : "") + " · Stop to pause; a new run resumes.";
                CategorizationOutcome o;
                var sha = "";
                var stored = false;
                try
                {
                    var bytes = await Task.Run(() => Bytes(m), ct);
                    if (bytes is null) o = CategorizationOutcome.Failed("The image cannot be read (not in the case, or the extraction is not reachable).");
                    else
                    {
                        sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
                        var (data, mime) = (bytes, MimeOf(m));
                        if (provider is not null && (!VisionMimes.Contains(mime) || data.Length > 15_000_000))
                        {
                            // a format or size the vision model does not take: a JPEG copy in memory, the evidence untouched
                            data = await Task.Run(() => ImageDecode.Jpeg(bytes), ct) ?? [];
                            mime = "image/jpeg";
                        }
                        if (data.Length == 0) o = CategorizationOutcome.Failed("The image cannot be decoded (no codec for this format?).");
                        else
                        {
                            var img = new AiImage(m.Name, mime, data);
                            var sendId = provider is null ? 0 : aiStore.LogSend(provider, c.Model, "Image categorization", prompt + "\n" + img.Describe());
                            o = await c.Categorize(img, ct);
                            var answer = o;
                            await Task.Run(() => store.Save(m, sha, c, answer, sendId), ct);
                            stored = true;
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ObjectDisposedException or AiException
                                               or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or ArgumentException or System.Net.Http.HttpRequestException)
                {
                    o = CategorizationOutcome.Failed(e.Message);
                }
                if (!stored)
                {
                    var failure = o;
                    try { await Task.Run(() => store.Save(m, sha, c, failure), ct); }
                    catch (Microsoft.Data.Sqlite.SqliteException) { }
                }
                switch (o.Status)
                {
                    case CategorizationOutcome.ErrorStatus: failed++; continue;
                    case CategorizationOutcome.RefusedStatus: refused++; break;
                    default: ok++; break;
                }
                var result = new ImageCategoryResult(0, DateTimeOffset.UtcNow, m.RefId, m.Name, m.SourcePath ?? m.Name, sha, c.Engine, c.Model, c.PromptVersion,
                                                     o.Scores, o.Reason, o.Status, null);
                _categories[m.RefId] = result;
                foreach (var tile in Items.Where(t => t.Media.RefId == m.RefId)) WithBadge(tile, threshold);
                if (ai.CategoriesAsTags && ImageCategorization.Above(o.Scores, threshold).FirstOrDefault() is { } best && !best.Name.StartsWith("Other", StringComparison.OrdinalIgnoreCase))
                    toTag.Add((m, ImageCategorization.TagOf(best.Name)));
            }
            Status = "";
            _main.Status = $"Categorize images: {ok:N0} categorized, {refused:N0} refused by the model, {failed:N0} failed" +
                           (resumed > 0 ? $", {resumed:N0} done before" : "") + ". AI-generated, unverified: a review aid, not a finding.";
        }
        catch (OperationCanceledException)
        {
            Status = "";
            _main.Status = $"Categorize images stopped after {n - 1:N0} of {pending.Count:N0}: results so far are kept; a new run resumes.";
        }
        finally
        {
            Categorizing = false;
            _categorizeCts.Dispose();
            _categorizeCts = null;
            if (toTag.Count > 0) ApplyAiTags(toTag);
            RefreshCategoryFilters();
            Show();
        }
    }

    /// <summary>"AI: …" tags on the images that have no tag yet: a tag the examiner set (on the file or its record) is never replaced.</summary>
    void ApplyAiTags(List<(MediaRef M, string Tag)> toTag)
    {
        _fileTags = _s.Tags.FileTags();
        _recordTags.Clear();
        var uses = _all.ToDictionary(e => e.M.RefId, e => e.U, StringComparer.Ordinal);
        var tagged = 0;
        foreach (var g in toTag.Where(t => TagOf(t.M, uses.GetValueOrDefault(t.M.RefId) ?? []) is null).GroupBy(t => t.Tag))
        {
            var keys = g.Select(t => TagStore.FileKey(t.M.SourcePath ?? t.M.ExtractionPath ?? t.M.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _s.Tags.SetFiles(keys, g.Key);
            tagged += keys.Count;
        }
        if (tagged == 0) return;
        _main.RefreshTagCount();
        foreach (var tab in _main.Tabs) if (tab.Content is TaggedViewModel tv) tv.Refresh();
        _main.Status += $" {tagged:N0} untagged images tagged 'AI: …' (AI-generated, unverified).";
    }

    /// <summary>Every categorization of this case as CSV, labelled AI-generated and unverified.</summary>
    [RelayCommand]
    async Task ExportCategories()
    {
        var rows = await Task.Run(() =>
        {
            try { return ImageCategoryStore.For(_s).All(); }
            catch (Exception e) when (e is IOException or Microsoft.Data.Sqlite.SqliteException) { return new List<ImageCategoryResult>(); }
        });
        if (rows.Count == 0) { _main.Status = "No image categories to export yet: run Categorize images first."; return; }
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Export image categories", Filter = "CSV (*.csv)|*.csv", FileName = "image-categories.csv" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            await File.WriteAllTextAsync(dlg.FileName, ImageCategoryStore.Csv(rows, Threshold), new System.Text.UTF8Encoding(true));
            _main.Status = $"{rows.Count:N0} image categorizations exported (AI-generated, unverified).";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _main.Status = "The CSV could not be written: " + e.Message; }
    }
}
