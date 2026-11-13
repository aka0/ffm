using System.Windows;

namespace FFM.App;

public partial class ConfigIssuesWindow : Window
{
    public ConfigIssuesWindow(IEnumerable<string> issues)
    {
        InitializeComponent();
        IssuesText.Text = string.Join(Environment.NewLine, issues);
        IssuesText.CaretIndex = 0;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(IssuesText.Text); CopyStatus.Text = "Config details copied."; }
        catch (Exception ex) { CopyStatus.Text = FriendlyErrors.Message(ex); }
    }
}
