using System.Collections.ObjectModel;
using System.Windows;
using Sink.Models;

namespace Sink;

public partial class DuplicateWindow : Window
{
    private readonly List<Track> _tracks;
    private readonly ObservableCollection<DuplicateCandidate> _candidates = [];
    public HashSet<Guid> SelectedIds => _candidates.Where(candidate => candidate.Remove).Select(candidate => candidate.Track.Id).ToHashSet();

    public DuplicateWindow(IEnumerable<Track> tracks)
    {
        _tracks = tracks.ToList();
        InitializeComponent();
        CandidateList.ItemsSource = _candidates;
        Loaded += (_, _) => Rebuild();
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) Rebuild();
    }

    private void Rebuild()
    {
        var byTitle = TrackNameMode.IsChecked == true;
        var groups = _tracks.GroupBy(track => (byTitle ? track.Title : track.FileName).Trim(), StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).ToList();
        _candidates.Clear();
        foreach (var group in groups)
        {
            var first = true;
            foreach (var track in group)
            {
                _candidates.Add(new DuplicateCandidate(track, !first));
                first = false;
            }
        }
        CandidateList.Visibility = _candidates.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = _candidates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCount();
    }

    private void CandidateCheck_Click(object sender, RoutedEventArgs e)
    {
        CandidateList.Items.Refresh();
        UpdateCount();
    }

    private void UpdateCount()
    {
        var count = SelectedIds.Count;
        SelectedText.Text = $"{count} track{(count == 1 ? "" : "s")} selected";
        RemoveButton.IsEnabled = count > 0;
    }

    private void Remove_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private sealed class DuplicateCandidate(Track track, bool remove)
    {
        public Track Track { get; } = track;
        public bool Remove { get; set; } = remove;
        public string ActionText => Remove ? "REMOVE" : "KEEP";
    }
}
