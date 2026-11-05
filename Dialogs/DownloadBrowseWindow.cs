using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using Sink.Services.Download;

namespace Sink.Dialogs;

/// <summary>Browse a catalog selection before handing an independent copy to the download queue.</summary>
public sealed class DownloadBrowseWindow : SinkDialog
{
    private readonly Func<CancellationToken, Task<DownloadNode>> _load;
    private readonly Func<DownloadNode, CancellationToken, Task> _loadAlbum;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ListBox _items = new()
    {
        Name = "BrowseItems", Height = 350, Background = Hex("#0F1218"),
        BorderBrush = Hex("#2A303C"), BorderThickness = new Thickness(1),
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };
    private readonly TextBlock _status = new()
    {
        Name = "BrowseStatus", Foreground = Hex("#A7ADBA"), TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 10, 0, 0),
    };
    private readonly CheckBox _selectAll = new()
    {
        Name = "BrowseSelectAll", Content = "Select all", Foreground = Hex("#C7CCD6"),
        IsEnabled = false, Margin = new Thickness(0, 0, 16, 10),
    };
    private readonly Button _download;
    private readonly Button _retry;
    private DownloadNode? _root;
    private bool _closed;

    public DownloadNode? RequestedDownload { get; private set; }

    public DownloadBrowseWindow(string title, bool artist,
        Func<CancellationToken, Task<DownloadNode>> load,
        Func<DownloadNode, CancellationToken, Task> loadAlbum)
    {
        _load = load; _loadAlbum = loadAlbum;
        Title = artist ? "Choose albums" : "Choose tracks";
        Width = 740; SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height;
        Resources[typeof(Button)] = (Style)XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
                <Setter Property="Template"><Setter.Value>
                    <ControlTemplate TargetType="Button">
                        <Border Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}" CornerRadius="4">
                            <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True"><Setter Property="Opacity" Value="0.8"/></Trigger>
                            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.35"/></Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value></Setter>
            </Style>
            """);
        _items.ItemContainerStyle = (Style)XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ListBoxItem">
                <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
                <Setter Property="Background" Value="Transparent"/>
                <Setter Property="BorderThickness" Value="0"/>
                <Style.Triggers>
                    <Trigger Property="IsSelected" Value="True"><Setter Property="Background" Value="#211E36"/></Trigger>
                    <Trigger Property="IsMouseOver" Value="True"><Setter Property="Background" Value="#181C25"/></Trigger>
                </Style.Triggers>
            </Style>
            """);
        _download = MakeButton("Download selected", true, (_, _) => RequestDownload(_root));
        _download.Name = "BrowseDownload";
        _download.IsEnabled = false;
        _retry = MakeButton("Retry", false, async (_, _) => await LoadAsync());
        _retry.Visibility = Visibility.Collapsed;

        _items.ItemTemplate = CreateRowTemplate();
        ScrollViewer.SetHorizontalScrollBarVisibility(_items, ScrollBarVisibility.Disabled);
        _items.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(RowAction));
        _items.PreviewMouseRightButtonUp += (_, e) =>
        {
            var row = ItemsControl.ContainerFromElement(_items, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (row?.DataContext is not DownloadNode { Kind: DownloadKind.Album } album) return;
            e.Handled = true;
            OpenAlbum(album);
        };

        var body = new StackPanel();
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(_selectAll);
        tools.Children.Add(_retry);
        body.Children.Add(tools);
        body.Children.Add(_items);
        body.Children.Add(_status);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        actions.Children.Add(MakeButton("Close", false, (_, _) => Close()));
        actions.Children.Add(_download);
        body.Children.Add(actions);
        Compose(artist ? "ALBUMS" : "TRACKS", title,
            artist ? "Choose albums to download. Click an album title or right-click its row to choose tracks."
                   : "Choose the tracks to download. Your selections stay in place when you close this window.", body);

        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            if (_root is not null) _root.PropertyChanged -= SelectionChanged;
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }

    internal async Task LoadAsync()
    {
        if (_root is not null || _closed) return;
        _retry.Visibility = Visibility.Collapsed;
        _status.Text = "Loading catalog…";
        try
        {
            var node = await _load(_lifetime.Token);
            if (_closed) return;
            _root = node;
            _root.PropertyChanged += SelectionChanged;
            _items.ItemsSource = node.Children;
            _selectAll.DataContext = node;
            _selectAll.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(DownloadNode.Enabled))
            { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            _selectAll.IsEnabled = node.Children.Count > 0;
            RefreshSelection();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_closed) return;
            _status.Text = $"Couldn't load the list: {ex.Message}";
            _retry.Visibility = Visibility.Visible;
        }
    }

    private void SelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadNode.Enabled)) RefreshSelection();
    }

    private void RefreshSelection()
    {
        if (_root is null) return;
        var selected = _root.Children.Count(n => n.Enabled != false);
        var label = _root.Kind == DownloadKind.Artist ? "albums" : "tracks";
        _status.Text = _root.Children.Count == 0 ? $"No {label} found for this result."
            : $"{selected} of {_root.Children.Count} {label} selected";
        _download.IsEnabled = selected > 0;
    }

    private void RowAction(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not Button { DataContext: DownloadNode album, Tag: string action }) return;
        e.Handled = true;
        if (action == "Browse") OpenAlbum(album);
        else if (action == "Download") RequestDownload(album);
    }

    private void OpenAlbum(DownloadNode album)
    {
        var dialog = new DownloadBrowseWindow(album.Name, false,
            async token => { await _loadAlbum(album, token); return album; }, _loadAlbum) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.RequestedDownload is { } requested)
            RequestDownload(requested);
    }

    private void RequestDownload(DownloadNode? node)
    {
        if (node is null || DownloadSelection.PendingUnits(node).Count == 0) return;
        RequestedDownload = node;
        DialogResult = true;
    }

    private static Button MakeButton(string label, bool primary, RoutedEventHandler onClick)
    {
        var button = new Button
        {
            Content = label, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(8, 0, 0, 0),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
            Background = Hex(primary ? "#6559CE" : "#242A34"), Foreground = Hex("#F4F6FA"),
        };
        button.Click += onClick;
        return button;
    }

    private static DataTemplate CreateRowTemplate() => (DataTemplate)XamlReader.Parse("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Grid Margin="8,6" MinHeight="32">
                <Grid.Resources><BooleanToVisibilityConverter x:Key="B2V"/></Grid.Resources>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="30"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>
                <CheckBox IsChecked="{Binding Enabled, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
                          VerticalAlignment="Center" ToolTip="Include in download"/>
                <StackPanel Grid.Column="1" VerticalAlignment="Center" Margin="0,0,12,0">
                    <TextBlock Text="{Binding Name}" Foreground="#F4F6FA" TextWrapping="Wrap">
                        <TextBlock.Style>
                            <Style TargetType="TextBlock">
                                <Style.Triggers>
                                    <DataTrigger Binding="{Binding CanHaveChildren}" Value="True">
                                        <Setter Property="Visibility" Value="Collapsed"/>
                                    </DataTrigger>
                                </Style.Triggers>
                            </Style>
                        </TextBlock.Style>
                    </TextBlock>
                    <Button Content="{Binding Name}" Tag="Browse" HorizontalContentAlignment="Left"
                            Foreground="#BEB7FF" Background="Transparent" BorderThickness="0" Padding="0,4"
                            Cursor="Hand" ToolTip="Choose tracks"
                            Visibility="{Binding CanHaveChildren, Converter={StaticResource B2V}}"/>
                </StackPanel>
                <Button Grid.Column="2" Content="Download" Tag="Download" Padding="10,5" Cursor="Hand"
                        Background="#6559CE" Foreground="White" BorderThickness="0" VerticalAlignment="Center"
                        IsEnabled="{Binding Enabled, TargetNullValue=True}"
                        Visibility="{Binding CanHaveChildren, Converter={StaticResource B2V}}"/>
            </Grid>
        </DataTemplate>
        """);
}
