using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AleappReader.App.Views;

/// <summary>Italian or English for the analysis-state commands, after the Windows display language (LEXIS has no
/// other localisation: the rest of the UI is English).</summary>
public static class StateText
{
    public static bool Italian => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";
    public static string T(string it, string en) => Italian ? it : en;
    public static string Menu => T("Stato analisi", "Analysis state");
    public static string ExportHeader => T("Esporta stato analisi…", "Export analysis state…");
    public static string ImportHeader => T("Importa stato analisi…", "Import analysis state…");
    public static string MenuTip => T("Salva i tag e le note di questo dispositivo in un file .lexisstate, e ricaricali su un nuovo parsing della stessa estrazione (abbinati per hash). Il file resta sul PC.",
        "Save this device's tags and notes to a .lexisstate file, and load them onto a new parsing of the same extraction (matched by hash). The file stays on this PC.");
}

/// <summary>The summary of an analysis-state import: before writing (Import / Save report / Cancel) and after (Save report / Close).</summary>
public sealed class AnalysisStateWindow : Window
{
    public enum Choice { Cancel, Import }
    public Choice Result { get; private set; } = Choice.Cancel;

    public AnalysisStateWindow(string title, string text, bool offerImport, Action saveReport)
    {
        Title = title;
        (Width, Height, ShowInTaskbar, ResizeMode) = (760, 520, false, ResizeMode.CanResizeWithGrip);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = Application.Current.MainWindow;
        var box = new TextBox
        {
            Text = text, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(8),
        };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 0, 8, 8) };
        bar.Children.Add(Button(StateText.T("Salva report…", "Save report…"), saveReport));
        if (offerImport)
        {
            bar.Children.Add(Button(StateText.T("Importa i tag", "Import the tags"), () => { Result = Choice.Import; DialogResult = true; }, true));
            bar.Children.Add(Button(StateText.T("Annulla", "Cancel"), () => DialogResult = false));
        }
        else bar.Children.Add(Button(StateText.T("Chiudi", "Close"), () => DialogResult = true, true));
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        dock.Children.Add(bar);
        dock.Children.Add(box);
        Content = dock;
    }

    static Button Button(string text, Action click, bool isDefault = false)
    {
        var b = new Button { Content = text, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4), IsDefault = isDefault };
        b.Click += (_, _) => click();
        return b;
    }
}
