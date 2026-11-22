using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using AleappReader.App.Views;
using AleappReader.Core;
using Microsoft.Win32;

namespace AleappReader.App.ViewModels;

/// <summary>Export and import of the analysis state (tags and notes) of the selected device, see Core.AnalysisState.
/// The .lexisstate file is case data: it is written only where the examiner saves it, and never sent anywhere.</summary>
public partial class MainViewModel
{
    static string AppVersion => typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "";
    static string T(string it, string en) => StateText.T(it, en);

    [RelayCommand(CanExecute = nameof(HasCase))]
    async Task ExportAnalysisState()
    {
        if ((SelectedNode?.Session ?? Session) is not { } s) return;
        var dev = DeviceOf(s)?.Label is { Length: > 0 } l ? l : EvidenceOrigin.ExtractionName(s);
        var dlg = new SaveFileDialog
        {
            Title = StateText.ExportHeader.TrimEnd('…'),
            Filter = T("Stato analisi LEXIS", "LEXIS analysis state") + $" (*{AnalysisState.Extension})|*{AnalysisState.Extension}",
            FileName = Safe($"{dev}_{DateTime.Now:yyyyMMdd-HHmm}") + AnalysisState.Extension,
        };
        if (dlg.ShowDialog() != true) return;
        var defs = Settings.Current.Tags.Select(t => new TagDefinition(t, (TagColorConverter.Of(t) as SolidColorBrush)?.Color is { } c ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : null)).ToList();
        Busy = true;
        Status = T("Esportazione dello stato analisi (calcolo degli hash)…", "Exporting the analysis state (hashing)…");
        try
        {
            var state = await Task.Run(() => AnalysisState.Export(s, defs, AppVersion));
            await Task.Run(() => AnalysisState.Save(state, dlg.FileName));
            var files = state.Items.Count(i => i.Kind == "file");
            var unhashed = state.Items.Count(i => i.Kind == "file" && i.File!.Sha256 is null);
            var items = $"{state.Items.Count - files:N0} record, {files:N0} file";
            ExportLog.Add("Analysis state (.lexisstate)", dlg.FileName, items, s);
            Status = T("Stato analisi esportato: ", "Analysis state exported: ") + items;
            MessageBox.Show(T($"Esportati {items} taggati in\n{dlg.FileName}", $"Exported {items} tagged to\n{dlg.FileName}") +
                (unhashed > 0 ? T($"\n\n{unhashed:N0} file non leggibili ora: saranno riabbinati solo per percorso e dimensione.",
                                  $"\n\n{unhashed:N0} files cannot be read now: they will be matched by path and size only.") : "") +
                T("\n\nIl file contiene tag, note, metadati e hash (nessun contenuto delle prove), ma è comunque un dato del caso: conservarlo come tale.",
                  "\n\nThe file holds tags, notes, metadata and hashes (no evidence content), but it is case data all the same: keep it as such."),
                StateText.Menu, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Status = T("Esportazione non riuscita.", "Export failed.");
            MessageBox.Show(e.Message, StateText.Menu, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { Busy = false; }
    }

    [RelayCommand(CanExecute = nameof(HasCase))]
    async Task ImportAnalysisState()
    {
        if ((SelectedNode?.Session ?? Session) is not { } s) return;
        var dlg = new OpenFileDialog
        {
            Title = StateText.ImportHeader.TrimEnd('…'),
            Filter = T("Stato analisi LEXIS", "LEXIS analysis state") + $" (*{AnalysisState.Extension})|*{AnalysisState.Extension}",
        };
        if (dlg.ShowDialog() != true) return;
        AnalysisStateFile state;
        try { state = AnalysisState.Load(dlg.FileName); }
        catch (Exception e) when (e is AnalysisStateException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(e.Message, StateText.Menu, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Busy = true;
        Status = T("Abbinamento degli elementi taggati al nuovo parsing (calcolo degli hash)…", "Matching the tagged items to this parsing (hashing)…");
        ImportPlan plan;
        try { plan = await Task.Run(() => AnalysisState.Plan(s, state)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Status = T("Importazione non riuscita.", "Import failed.");
            MessageBox.Show(e.Message, StateText.Menu, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally { Busy = false; }

        var it = StateText.Italian;
        var head = T($"Stato di LEXIS {state.AppVersion} del {state.ExportedUtc.ToLocalTime():yyyy-MM-dd HH:mm} ({state.Case.ParsedBy}).\n" +
                     "L'importazione aggiunge tag e note: non cancella né sostituisce i tag già presenti.\n\n",
                     $"State of LEXIS {state.AppVersion}, {state.ExportedUtc.ToLocalTime():yyyy-MM-dd HH:mm} ({state.Case.ParsedBy}).\n" +
                     "The import adds tags and notes: it never deletes or replaces the tags already there.\n\n");
        var preview = new AnalysisStateWindow(StateText.ImportHeader.TrimEnd('…'), head + plan.Summary(it), true, () => SaveReport(plan));
        if (preview.ShowDialog() != true || preview.Result != AnalysisStateWindow.Choice.Import) { Status = T("Importazione annullata.", "Import cancelled."); return; }

        try { await Task.Run(() => AnalysisState.Apply(s, plan)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            MessageBox.Show(e.Message, StateText.Menu, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // Tags of the state the settings do not list yet are added, so menus and shortcuts offer them.
        var added = state.TagDefinitions.Select(d => d.Name).Where(n => n != "" && !Settings.Current.Tags.Contains(n)).ToList();
        if (added.Count > 0) { Settings.Current.Tags.AddRange(added); Settings.Current.Save(); }
        ExportLog.Add("Analysis state imported", dlg.FileName,
            $"{plan.ByHash} hash, {plan.BySecondary} secondary, {plan.ByPath} path, {plan.Ambiguous} ambiguous, {plan.NotFound} not found", s);
        OnFileTagChanged(s);
        // Open views of the records just tagged: the same refresh a tag set from a menu gives, one call per tag.
        foreach (var table in plan.Matches.SelectMany(m => m.Targets).Select(x => x.Table)
                     .Where(x => !x.StartsWith(TagStore.FilePrefix, StringComparison.Ordinal)).Distinct())
        {
            if (s.Manifest.Artifacts.FirstOrDefault(a => a.TableName == table) is not { } art) continue;
            foreach (var g in s.Tags.ForTable(table).Values.GroupBy(e => e.Tag))
                OnTagChanged(art, g.Select(e => e.RowId).ToList(), g.Key == "" ? null : g.Key);
        }
        Status = T($"Stato analisi importato: {plan.Tagged:N0} tag scritti, {plan.NotFound:N0} non trovati.",
                   $"Analysis state imported: {plan.Tagged:N0} tags written, {plan.NotFound:N0} not found.");
        new AnalysisStateWindow(StateText.ImportHeader.TrimEnd('…'), plan.ToText(it), false, () => SaveReport(plan)).ShowDialog();
    }

    /// <summary>The import report for the case file: CSV (one line per item) or text (summary and lists).</summary>
    static void SaveReport(ImportPlan plan)
    {
        var it = StateText.Italian;
        var dlg = new SaveFileDialog
        {
            Title = T("Salva il report dell'importazione", "Save the import report"),
            Filter = "CSV (*.csv)|*.csv|" + T("Testo", "Text") + " (*.txt)|*.txt",
            FileName = $"lexis-import-stato_{DateTime.Now:yyyyMMdd-HHmm}.csv",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var csv = dlg.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
            File.WriteAllText(dlg.FileName, csv ? plan.ToCsv(it) : plan.ToText(it), new System.Text.UTF8Encoding(true));
            ExportLog.Add("Analysis state import report", dlg.FileName, $"{plan.Matches.Count} items");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(e.Message, StateText.Menu, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    static string Safe(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
