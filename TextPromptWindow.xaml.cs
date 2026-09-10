using System.Windows;
using Sink.Controls;

namespace Sink;

public partial class TextPromptWindow : Window
{
    public string Answer => NameBox.Text.Trim();

    public TextPromptWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => NameBox.Focus();
    }

    /// <summary>Turns the input into an autocomplete field for the given metadata kind.</summary>
    public SuggestionField Suggestions
    {
        get => NameBox.SuggestionField;
        set => NameBox.SuggestionField = value;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Answer)) return;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
