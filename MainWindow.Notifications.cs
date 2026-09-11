using System.Collections.ObjectModel;
using System.Windows;
using Sink.Models;

namespace Sink;

/// <summary>
/// The notification bell in the sidebar header (task 125): a scrollable panel
/// of recent app events. Most kinds cap at one live row — a fresh occurrence
/// replaces the existing one instead of stacking — driven by a (Kind,
/// DedupeKey) match; "new episodes" allows one per show via a per-show key.
/// </summary>
public partial class MainWindow
{
    private readonly ObservableCollection<AppNotification> _notifications = [];

    private void InitNotifications()
    {
        NotificationList.ItemsSource = _notifications;
        _notifications.CollectionChanged += (_, _) => UpdateNotificationChrome();
        UpdateNotificationChrome();
    }

    private void UpdateNotificationChrome()
    {
        NotificationDot.Visibility = _notifications.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NotificationEmptyHint.Visibility = _notifications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearAllNotificationsButton.IsEnabled = _notifications.Count > 0;
    }

    /// <summary>
    /// Posts a notification. A dedupeKey of null means at most one of this
    /// kind can exist — a later call replaces the earlier one (and bumps it
    /// back to the top) instead of adding a second row.
    /// </summary>
    private void PostNotification(string kind, string? dedupeKey, string text)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => PostNotification(kind, dedupeKey, text)); return; }
        var existing = _notifications.FirstOrDefault(n => n.Kind == kind && n.DedupeKey == dedupeKey);
        if (existing is not null) _notifications.Remove(existing);
        _notifications.Insert(0, new AppNotification { Kind = kind, DedupeKey = dedupeKey, Text = text });
    }

    private void NotificationBell_Click(object sender, RoutedEventArgs e) => NotificationPopup.IsOpen = !NotificationPopup.IsOpen;

    private void ClearNotification_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AppNotification n) _notifications.Remove(n);
    }

    private void ClearAllNotifications_Click(object sender, RoutedEventArgs e) => _notifications.Clear();
}
