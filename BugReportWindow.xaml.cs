using System.Windows;

namespace Sink;

public partial class BugReportWindow : Window
{
    public string BugTitle => TitleBox.Text.Trim();
    public string Description => DescriptionBox.Text.Trim();

    public BugReportWindow(IReadOnlyList<string> detectedTags)
    {
        InitializeComponent();
        TagsText.Text = detectedTags.Count == 0 ? "task" : string.Join(", ", detectedTags);
        Loaded += (_, _) => TitleBox.Focus();
    }

    private void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(BugTitle)) return;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
