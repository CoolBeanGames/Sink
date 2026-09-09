using System.Windows;

namespace Sink;

public partial class TextPromptWindow : Window
{
    public string Answer => NameBox.Text.Trim();

    public TextPromptWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => NameBox.Focus();
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Answer)) return;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
