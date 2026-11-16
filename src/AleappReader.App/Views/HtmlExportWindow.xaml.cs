using System.Windows;

namespace AleappReader.App.Views;

/// <summary>Asks whether automatic audio transcripts go into an HTML report: null when cancelled, otherwise the
/// examiner's choice (off unless they tick the box).</summary>
public partial class HtmlExportWindow : Window
{
    HtmlExportWindow() => InitializeComponent();

    void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>True when transcripts should be included, false when they should not, null when the examiner cancelled.</summary>
    public static bool? Ask()
    {
        var w = new HtmlExportWindow { Owner = Application.Current.MainWindow };
        return w.ShowDialog() == true ? w.Transcripts.IsChecked == true : null;
    }
}
