using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Sink.Services.LanSync;

namespace Sink.Dialogs;

internal sealed class LanSyncWindow : SinkDialog
{
    private readonly LanSyncStore _store;
    private readonly LanNetwork _network;
    private readonly Func<LanSnapshot> _capture;
    private readonly Action<LanSyncStore, LanTrust, LanSnapshot, JsonObject> _apply;
    private readonly CancellationTokenSource _closed = new();
    private readonly DispatcherTimer _scan = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly ListBox _peers = new() { Height = 150 };
    private readonly TextBox _code = new() { MaxLength = 30 };
    private readonly TextBox _shownCode = new() { IsReadOnly = true, Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private string? _pin;
    private DateTime _pinExpires;
    private int _pinFailures;
    private bool _busy;
    private bool _scanning;

    public LanSyncWindow(Func<LanSnapshot> capture, Action<LanSyncStore, LanTrust, LanSnapshot, JsonObject> apply)
    {
        _capture = capture;
        _apply = apply;
        _store = new();
        try { _network = new(_store.Id, Environment.MachineName); }
        catch { _store.Dispose(); throw; }
        Title = "Sync with another PC";
        Width = 670;
        Height = 575;

        var body = new StackPanel();
        body.Children.Add(Label("Other Sink installs"));
        body.Children.Add(_peers);
        var tools = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        tools.Children.Add(Button("Refresh", async (_, _) => await FindAsync()));
        tools.Children.Add(Button("Show pairing code", (_, _) => ShowPairingCode()));
        tools.Children.Add(Button("Forget selected pairing", (_, _) => Forget()));
        body.Children.Add(tools);
        body.Children.Add(_shownCode);
        body.Children.Add(Label("Pairing code from the other PC (first sync only)"));
        body.Children.Add(_code);
        _status.Margin = new Thickness(0, 12, 0, 0);
        _status.Foreground = Hex("#ADB4C2");
        body.Children.Add(_status);

        Compose("Local network", "Sync with another PC",
            "Open this window in Sink on both PCs. Select the other computer here and start the sync from one PC.",
            body, new("Close", false, (_, _) => Close()), new("Sync selected PC", true, async (_, _) => await StartAsync()));

        _scan.Tick += async (_, _) => await FindAsync();
        Loaded += async (_, _) =>
        {
            _ = ListenAsync();
            _ = AdvertiseAsync();
            _scan.Start();
            Status("Searching your local network…");
            await FindAsync();
        };
        Closed += (_, _) =>
        {
            _scan.Stop();
            _closed.Cancel();
            _network.Dispose();
            _store.Dispose();
        };
    }

    private async Task FindAsync()
    {
        if (_busy || _scanning || _closed.IsCancellationRequested) return;
        _scanning = true;
        try
        {
            var online = await _network.FindAsync(_closed.Token);
            if (_busy || _closed.IsCancellationRequested) return;
            var selected = (_peers.SelectedItem as LanFound)?.Peer.Id;
            var seen = online.Select(found => found.Peer.Id).ToHashSet();
            var all = online.Concat(_store.Peers.Where(peer => !seen.Contains(peer.Id))
                .Select(peer => new LanFound(new(peer.Id, peer.Name, 0), null))).ToList();
            _peers.ItemsSource = all;
            _peers.SelectedItem = all.FirstOrDefault(peer => peer.Peer.Id == selected)
                ?? (selected is null && online.Count == 1 ? online[0] : null);
            if (_status.Text == "Searching your local network…" || _status.Text.StartsWith("No other PCs found", StringComparison.Ordinal))
                Status(online.Count > 0 ? "Select a PC to sync. For a first sync, get its pairing code."
                    : "No other PCs found yet. Open this window on the other PC and allow Sink on your private network if Windows asks.");
        }
        catch (Exception ex) { HandleError(ex); }
        finally { _scanning = false; }
    }

    private async Task AdvertiseAsync()
    {
        try { await _network.AdvertiseAsync(_closed.Token); }
        catch (Exception ex) { HandleError(ex); }
    }

    private async Task ListenAsync()
    {
        try
        {
            while (!_closed.IsCancellationRequested)
            {
                using var client = await _network.AcceptAsync(_closed.Token);
                if (_busy || client.Client.RemoteEndPoint is not IPEndPoint endpoint || !_network.IsLocal(endpoint.Address)) continue;
                try { await ReceiveAsync(client); }
                catch (AuthenticationException) { /* No prompts or data for unauthenticated connections. */ }
                catch (Exception ex) { HandleError(ex); }
            }
        }
        catch (Exception ex) { HandleError(ex); }
    }

    private async Task ReceiveAsync(TcpClient client)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await using var tls = await LanNetwork.ServerTlsAsync(client, _store.Certificate, deadline.Token);
        var hello = await LanWire.ReadAsync<LanHello>(tls, deadline.Token, 4096);
        if (hello is null || hello.Target != _store.Id || hello.Id == _store.Id
            || string.IsNullOrWhiteSpace(hello.Certificate) || !LanNetwork.IsIdentity(hello.Id, hello.Certificate)
            || string.IsNullOrWhiteSpace(hello.Name) || hello.Name.Length > 128) return;

        var trust = _store.Find(hello.Id);
        var paired = trust is not null && trust.Certificate == hello.Certificate && SecretsEqual(trust.Secret, hello.Secret);
        var pairing = !paired && IsPinValid(hello.Pin);
        if (!paired && !pairing)
        {
            await LanWire.SendAsync(tls, new LanOffer("Pairing code or saved pairing was rejected. Get a fresh code on the other PC."), deadline.Token);
            return;
        }
        if (_busy)
        {
            await LanWire.SendAsync(tls, new LanOffer("The other PC is already syncing. Start the sync from just one PC."), deadline.Token);
            return;
        }

        _busy = true;
        var saved = false;
        try
        {
            deadline.CancelAfter(TimeSpan.FromMinutes(15));
            if (MessageBox.Show(this, $"Allow {hello.Name} to connect and see your library, podcasts, and listening history?",
                "PC sync request", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                await LanWire.SendAsync(tls, new LanOffer("The other PC declined the sync request."), deadline.Token);
                return;
            }
            var snapshot = _capture();
            var stamp = Stamp(snapshot);
            if (pairing)
            {
                _pin = null;
                _shownCode.Visibility = Visibility.Collapsed;
                var baseline = trust?.Certificate == hello.Certificate ? trust.Baselines : [];
                trust = new(hello.Id, hello.Name, hello.Certificate, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))
                { Baselines = baseline };
                _store.Remember(trust);
            }
            trust = trust! with { Name = hello.Name };
            await LanWire.SendAsync(tls, new LanOffer(null, pairing ? trust.Secret : null, snapshot,
                trust.Baselines.Select(b => b.Id).ToArray()), deadline.Token);
            Status($"Waiting for {hello.Name} to review the changes…");
            var proposal = await LanWire.ReadAsync<LanProposal?>(tls, deadline.Token);
            if (proposal is null) { Status("Sync canceled by the other PC."); return; }
            if (proposal.Snapshot is null || proposal.Baseline is null) throw new InvalidDataException("The peer sent an incomplete sync plan.");
            proposal.Snapshot.Validate();
            LanMerge.ValidateBaseline(proposal.Baseline);
            if (!Review(hello.Name, snapshot, proposal.Snapshot))
            {
                await LanWire.SendAsync(tls, false, deadline.Token);
                Status("Sync canceled.");
                return;
            }
            CheckUnchanged(stamp);
            await LanWire.SendAsync(tls, true, deadline.Token);
            if (!await LanWire.ReadAsync<bool>(tls, deadline.Token, 32)) { Status("Sync canceled by the other PC."); return; }
            CheckUnchanged(stamp);
            deadline.Token.ThrowIfCancellationRequested();
            _apply(_store, trust, proposal.Snapshot, proposal.Baseline);
            saved = true;
            await LanWire.SendAsync(tls, true, deadline.Token);
            var confirmed = await LanWire.ReadAsync<bool>(tls, deadline.Token, 32);
            Status(confirmed ? $"Synced with {hello.Name}." : "Saved on this PC. The other PC did not confirm its save.");
        }
        catch (Exception ex)
        {
            if (saved) Status("Saved on this PC. Reconnect and sync again to finish on the other PC.");
            else HandleError(ex);
        }
        finally { _busy = false; }
    }

    private async Task StartAsync()
    {
        if (_busy) return;
        if (_peers.SelectedItem is not LanFound { Address: { } address } found)
        {
            Status("Select an online PC with this window open.");
            return;
        }
        var trust = _store.Find(found.Peer.Id);
        var code = _code.Text.Trim().Replace("-", "").Replace(" ", "").ToUpperInvariant();
        var pairing = code.Length > 0 || trust is null;
        if (pairing && (code.Length != 22 || !code[..16].All(Uri.IsHexDigit) || !code[16..].All(char.IsAsciiDigit)))
        {
            Status("Enter the pairing code shown on the other PC.");
            return;
        }

        _busy = true;
        var remoteSaved = false;
        try
        {
            var snapshot = _capture();
            var stamp = Stamp(snapshot);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            using var client = new TcpClient(AddressFamily.InterNetwork);
            var (stream, fingerprint) = await LanNetwork.ClientTlsAsync(client, address, found.Peer,
                pairing ? code[..16] : trust!.Certificate, deadline.Token);
            await using var tls = stream;
            deadline.CancelAfter(TimeSpan.FromMinutes(15));
            Status($"Waiting for {found.Peer.Name} to allow the sync…");
            await LanWire.SendAsync(tls, new LanHello(found.Peer.Id, _store.Id, _network.Self.Name, _store.Fingerprint,
                pairing ? code[16..] : null, pairing ? null : trust!.Secret), deadline.Token);
            var offer = await LanWire.ReadAsync<LanOffer>(tls, deadline.Token);
            if (offer is null || offer.Error is not null || offer.Snapshot is null || offer.Baselines is null)
                throw new InvalidDataException(offer?.Error ?? "The other PC sent an incomplete sync offer.");
            var other = offer.Snapshot;
            other.Validate();
            if (pairing)
            {
                if (!IsSecret(offer.Secret)) throw new AuthenticationException("The other PC did not send a valid pairing key.");
                var baselines = trust?.Certificate == fingerprint ? trust.Baselines : [];
                trust = new(found.Peer.Id, found.Peer.Name, fingerprint, offer.Secret!) { Baselines = baselines };
                _store.Remember(trust);
                _code.Clear();
            }
            var common = trust!.Baselines.FirstOrDefault(b => offer.Baselines.Contains(b.Id))?.Catalog;
            var plan = new LanMerge().Build(snapshot, other, common);
            if (plan.Conflicts.Count > 0)
            {
                var conflictDialog = new LanReviewWindow(found.Peer.Name, plan.Conflicts) { Owner = this };
                if (conflictDialog.ShowDialog() != true)
                {
                    await LanWire.SendAsync<LanProposal?>(tls, null, deadline.Token);
                    Status("Sync canceled.");
                    return;
                }
                plan = new LanMerge(conflictDialog.Choices).Build(snapshot, other, common);
            }
            if (!Review(found.Peer.Name, snapshot, plan.Local, LanReviewWindow.Describe(other, plan.Remote)))
            {
                await LanWire.SendAsync<LanProposal?>(tls, null, deadline.Token);
                Status("Sync canceled.");
                return;
            }
            CheckUnchanged(stamp);
            Status($"Waiting for {found.Peer.Name} to approve the changes…");
            await LanWire.SendAsync(tls, new LanProposal(plan.Remote, plan.Baseline), deadline.Token);
            if (!await LanWire.ReadAsync<bool>(tls, deadline.Token, 32)) { Status("The other PC declined the changes."); return; }
            CheckUnchanged(stamp);
            await LanWire.SendAsync(tls, true, deadline.Token);
            if (!await LanWire.ReadAsync<bool>(tls, deadline.Token, 32)) throw new IOException("The other PC could not save its changes.");
            remoteSaved = true;
            CheckUnchanged(stamp);
            deadline.Token.ThrowIfCancellationRequested();
            _apply(_store, trust, plan.Local, plan.Baseline);
            await LanWire.SendAsync(tls, true, deadline.Token);
            Status($"Synced with {found.Peer.Name}.");
        }
        catch (Exception ex)
        {
            if (remoteSaved) Status($"The other PC saved its changes. Sync again if this PC still needs them. {ex.Message}");
            else HandleError(ex);
        }
        finally { _busy = false; }
    }

    private bool Review(string name, LanSnapshot before, LanSnapshot after, string? other = null) =>
        new LanReviewWindow(name, before, after, other) { Owner = this }.ShowDialog() == true;

    private static byte[] Stamp(LanSnapshot snapshot) => SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot));
    private void CheckUnchanged(byte[] stamp)
    {
        if (!stamp.AsSpan().SequenceEqual(Stamp(_capture())))
            throw new InvalidOperationException("The database changed during review. Start the sync again to review the latest data.");
    }

    private void ShowPairingCode()
    {
        if (_busy) return;
        _pin = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6");
        _pinFailures = 0;
        _pinExpires = DateTime.UtcNow.AddMinutes(5);
        var fingerprint = _store.Fingerprint;
        _shownCode.Text = $"{fingerprint[..4]}-{fingerprint[4..8]}-{fingerprint[8..12]}-{fingerprint[12..16]}-{_pin}";
        _shownCode.Visibility = Visibility.Visible;
        Status("Enter this code on the PC starting the sync. It expires in five minutes.");
    }

    private bool IsPinValid(string? candidate)
    {
        if (_pin is null || _pinExpires <= DateTime.UtcNow || _pinFailures >= 5) return false;
        if (SecretsEqual(_pin, candidate)) return true;
        _pinFailures++;
        return false;
    }

    private static bool SecretsEqual(string expected, string? received) => received is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(received));

    private static bool IsSecret(string? value) => value?.Length == 44
        && Convert.TryFromBase64String(value, stackalloc byte[32], out var count) && count == 32;

    private void Forget()
    {
        if (_busy || _peers.SelectedItem is not LanFound found || _store.Find(found.Peer.Id) is null) return;
        if (MessageBox.Show(this, $"Forget the pairing with {found.Peer.Name}?", "Forget pairing",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            _store.Forget(found.Peer.Id);
            Status("Pairing forgotten. A new code is required to sync again.");
        }
        catch (Exception ex) { HandleError(ex); }
    }

    private void HandleError(Exception ex)
    {
        if (_closed.IsCancellationRequested) return;
        Status(ex is OperationCanceledException ? "The sync timed out. Open this window on both PCs and try again."
            : ex is AuthenticationException ? "The pairing identity didn't match. Get a fresh code from the other PC."
            : ex is EndOfStreamException ? "The other PC disconnected or canceled. Review and sync again if needed."
            : ex.Message);
    }

    private void Status(string message)
    {
        if (!_closed.IsCancellationRequested) _status.Text = message;
    }

    private static TextBlock Label(string text) => new()
    { Text = text, Foreground = Hex("#C7CCD6"), Margin = new Thickness(0, 6, 0, 4) };
    private static Button Button(string title, RoutedEventHandler click)
    {
        var button = new Button
        {
            Content = title, Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(0, 0, 6, 4),
            Background = Hex("#242A34"), Foreground = Hex("#DCE0EA"), BorderThickness = new Thickness(0),
        };
        button.Click += click;
        return button;
    }
}
