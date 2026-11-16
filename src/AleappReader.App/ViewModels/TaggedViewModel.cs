using System.Collections;
using System.IO;
using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AleappReader.Core;
using Microsoft.Win32;

namespace AleappReader.App.ViewModels;

public sealed record TaggedItem(TagEntry Entry, ArtifactInfo? Artifact)
{
    /// <summary>The path of a tagged file (gallery or Extraction Files), else null.</summary>
    public string? FilePath => Entry.TableName.StartsWith(TagStore.FilePrefix, StringComparison.Ordinal) ? Entry.TableName[TagStore.FilePrefix.Length..] : null;
    public string ArtifactName => Artifact?.Name ?? FilePath ?? Entry.TableName;
    public string Record => FilePath is null ? Entry.RowId.ToString() : "file";
    public string Created => ValueFormatter.FormatTime(Entry.Created);
    public bool IsFile => FilePath is not null;
}

/// <summary>Every tagged record across the case, exportable as one HTML report. Tagged files can be opened in
/// their original folder, previewed, shown in Explorer when a local copy exists, and have their tags edited or removed.</summary>
public partial class TaggedViewModel : ObservableObject, IRefreshable
{
    readonly MainViewModel _main;
    readonly CaseSession _s;

    public ObservableCollection<TaggedItem> Items { get; } = [];
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoToCommand), nameof(OpenInFolderCommand), nameof(OpenFileCommand),
        nameof(ShowInExplorerCommand), nameof(RemoveTagCommand), nameof(SetFileTagCommand))]
    [NotifyPropertyChangedFor(nameof(CanOpenInFolder), nameof(CanOpenFile), nameof(CanShowInExplorer), nameof(CanRemoveTag))]
    private TaggedItem? selected;

    public bool CanOpenInFolder => Selected?.IsFile == true;
    public bool CanOpenFile => Selected?.IsFile == true;
    public bool CanShowInExplorer => Selected?.FilePath is { } p && LocalCopy(p) is not null;
    public bool CanRemoveTag => Selected is not null;
    public IReadOnlyList<string> TagChoices => Settings.Current.Tags;

    public TaggedViewModel(MainViewModel main, CaseSession s)
    {
        _main = main;
        _s = s;
        Refresh();
    }

    public void Refresh()
    {
        var keep = Selected is { } sel ? (sel.Entry.TableName, sel.Entry.RowId) : ((string, long)?)null;
        var byTable = _s.Manifest.Artifacts.ToDictionary(a => a.TableName);
        Items.Clear();
        foreach (var t in _s.Tags.All()) Items.Add(new TaggedItem(t, byTable.GetValueOrDefault(t.TableName)));
        Selected = keep is { } k ? Items.FirstOrDefault(i => i.Entry.TableName == k.Item1 && i.Entry.RowId == k.Item2) : null;
    }

    partial void OnSelectedChanged(TaggedItem? value)
    {
        if (value is null) return;
        if (value.Artifact is { } a) _main.Details.ShowRecord(_s, a, value.Entry.RowId);
        else if (value.FilePath is { } path) ShowTaggedFile(path);
    }

    void ShowTaggedFile(string path)
    {
        var rel = TagStore.FileKey(path);
        var device = EvidenceOrigin.DevicePath(rel);
        var size = _s.Extraction?.SizeOf(rel) ?? -1;
        var used = EvidenceOrigin.UsedBy(_s);
        var arts = used.GetValueOrDefault(rel) ?? used.GetValueOrDefault(TaggedFileNav.RevealRel(rel)) ?? [];
        _main.Details.ShowFile(_s, rel, device, size >= 0 ? size : 0, arts);
    }

    /// <summary>Double-click: a tagged file opens in its original folder; a tagged record opens in its artifact.</summary>
    [RelayCommand]
    void GoTo(TaggedItem? item)
    {
        var it = item ?? Selected;
        if (it is null) return;
        if (it.FilePath is not null) OpenInFolder(it);
        else if (it.Artifact is { } a) _main.NavigateTo(a, null, it.Entry.RowId);
    }

    [RelayCommand(CanExecute = nameof(CanOpenInFolder))]
    void OpenInFolder(TaggedItem? item)
    {
        var path = (item ?? Selected)?.FilePath;
        if (path is null) return;
        var reveal = TaggedFileNav.RevealRel(path);
        _main.ShowInFiles(reveal, _s);
        _main.Status = $"Opened in original folder: {TaggedFileNav.Device(path)}";
    }

    [RelayCommand(CanExecute = nameof(CanOpenFile))]
    async Task OpenFile(TaggedItem? item)
    {
        var path = (item ?? Selected)?.FilePath;
        if (path is null) return;
        var rel = TagStore.FileKey(path);
        if (_s.Extraction is not { } x)
        {
            _main.Status = "The extraction is not reachable: locate it to open the file.";
            return;
        }
        var copy = await Task.Run(() => x.CopyOut(rel) ?? x.CopyOut(TaggedFileNav.RevealRel(rel)));
        if (copy is null)
        {
            _main.Status = "The file cannot be read from the extraction.";
            return;
        }
        var m = x.ToMedia(rel, copy) with { AudioFolders = SilkAudio.Folders(_s) };
        if ((m.Kind is "image" or "video" or "audio" || DetailsViewModel.IsPdf(m)) && m.Exists)
            Views.RecordWindow.ShowMedia(_main, m, null);
        else
            DetailsViewModel.OpenMedia(m);
    }

    [RelayCommand(CanExecute = nameof(CanShowInExplorer))]
    void ShowInExplorer(TaggedItem? item)
    {
        var path = (item ?? Selected)?.FilePath;
        if (path is null || LocalCopy(path) is not { } file) return;
        try { Process.Start("explorer.exe", ["/select,", file]); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            _main.Status = "Cannot show the file in Explorer: " + e.Message;
        }
    }

    string? LocalCopy(string taggedPath)
    {
        var rel = TagStore.FileKey(taggedPath);
        if (_s.Extraction?.ExistingLocalCopy(rel) is { } existing) return existing;
        if (_s.Extraction?.ExistingLocalCopy(TaggedFileNav.RevealRel(rel)) is { } outer) return outer;
        foreach (var key in new[] { rel, TaggedFileNav.RevealRel(rel) })
        {
            var copy = _s.ResolveInCase(Path.Combine("data", key));
            if (copy is not null && File.Exists(copy)) return copy;
        }
        return null;
    }

    [RelayCommand(CanExecute = nameof(CanRemoveTag))]
    void RemoveTag(object? selected)
    {
        var items = Chosen(selected);
        if (items.Count == 0) return;
        var files = items.Where(i => i.FilePath is not null).Select(i => i.FilePath!).ToList();
        var records = items.Where(i => i.Artifact is not null).Select(i => (i.Artifact!, i.Entry.RowId)).ToList();
        if (files.Count > 0)
        {
            TaggedFileNav.RemoveFiles(_s.Tags, files);
            _main.OnFileTagChanged(_s);
        }
        if (records.Count > 0) _main.SetTag(records, null);
        _main.Status = items.Count == 1
            ? $"Tag removed from {items[0].ArtifactName}"
            : $"Tag removed from {items.Count:N0} items";
    }

    [RelayCommand]
    void SetFileTag(string? tag)
    {
        if (Selected?.FilePath is not { } path || string.IsNullOrWhiteSpace(tag)) return;
        TaggedFileNav.SetFile(_s.Tags, path, tag);
        _main.OnFileTagChanged(_s);
        _main.Status = $"Tagged {TaggedFileNav.Device(path)} as \"{tag}\"";
    }

    List<TaggedItem> Chosen(object? selected)
    {
        var rows = new List<TaggedItem>();
        if (selected is IEnumerable en and not string)
            foreach (var o in en)
                if (o is TaggedItem t) rows.Add(t);
        rows = rows.Distinct().ToList();
        if (rows.Count == 0 && Selected is { } one) rows.Add(one);
        return rows;
    }

    [RelayCommand]
    async Task ExportHtml()
    {
        var dlg = new SaveFileDialog { Filter = "HTML report (*.html)|*.html|JSON (*.json)|*.json", FileName = "Tagged items.html" };
        if (dlg.ShowDialog() != true) return;
        var html = System.IO.Path.GetExtension(dlg.FileName).Equals(".html", StringComparison.OrdinalIgnoreCase);
        bool transcripts = false;
        if (html)
        {
            if (Views.HtmlExportWindow.Ask() is not { } inc) return;
            transcripts = inc;
        }
        await Task.Run(() =>
        {
            using var con = _s.OpenDb();
            var sections = Items.Where(i => i.Artifact is not null).GroupBy(i => i.Artifact!).Select(g =>
            {
                var page = ArtifactQuery.Query(con, g.Key, new PageRequest(0, int.MaxValue, RowIds: g.Select(i => i.Entry.RowId).ToList()));
                return new ExportSection(g.Key.Name, page.Columns, page.Rows, _s.Tags.ForTable(g.Key.TableName), g.Key, "Tagged rows");
            }).ToList();
            var files = Items.Where(i => i.FilePath is not null).Select((i, n) => new RecordRow(n, [i.FilePath, i.Entry.Tag, transcripts ? i.Entry.Note : TagStore.WithoutTranscript(i.Entry.Note), i.Created])).ToList();
            if (files.Count > 0)
                sections.Add(new ExportSection("Tagged files", [new("path", "File", null), new("tag", "Tag", null), new("note", "Note", null), new("created", "Tagged at", null)], files));
            if (System.IO.Path.GetExtension(dlg.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase)) JsonExporter.Write(dlg.FileName, sections);
            else HtmlReportExporter.Write(dlg.FileName, _s, sections, "Tagged items", transcripts);
        });
        _main.Status = "Exported " + dlg.FileName;
        var extra = html ? (transcripts ? ", with audio transcripts" : ", without audio transcripts") : "";
        ExportLog.Add("Tagged items", dlg.FileName, $"{Items.Count:N0} items" + extra, _s);
    }
}
