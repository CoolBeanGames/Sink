using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sink.Services.LanSync;

internal sealed record LanPeer(Guid Id, string Name, int Port)
{
    public override string ToString() => Name;
}
internal sealed record LanFound(LanPeer Peer, IPAddress? Address)
{
    public override string ToString() => Address is null ? $"{Peer.Name} · paired, offline" : $"{Peer.Name} · {Address}";
}
internal sealed record LanHello(Guid Target, Guid Id, string Name, string Certificate, string? Pin, string? Secret);
internal sealed record LanOffer(string? Error, string? Secret = null, LanSnapshot? Snapshot = null, string[]? Baselines = null);
internal sealed record LanProposal(LanSnapshot Snapshot, JsonObject Baseline);

internal sealed class LanNetwork : IDisposable
{
    private const int DiscoveryPort = 45873;
    private static readonly byte[] Query = "Sink PC sync 1"u8.ToArray();
    private readonly TcpListener _listener = new(IPAddress.Any, 0);
    private readonly UdpClient _discovery = new(AddressFamily.InterNetwork);
    private readonly (uint Address, uint Mask)[] _networks;
    public LanPeer Self { get; }

    public LanNetwork(Guid id, string name)
    {
        _networks = NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork && address.IPv4Mask is not null)
            .Select(address => (Number(address.Address), Number(address.IPv4Mask))).ToArray();
        try
        {
            _listener.Start(4);
            Self = new(id, name, ((IPEndPoint)_listener.LocalEndpoint).Port);
            _discovery.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool IsLocal(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        if (IPAddress.IsLoopback(address)) return true;
        var value = Number(address);
        return _networks.Any(net => net.Mask != 0 && (net.Address & net.Mask) == (value & net.Mask));
    }

    public async Task AdvertiseAsync(CancellationToken token)
    {
        var reply = JsonSerializer.SerializeToUtf8Bytes(Self);
        while (!token.IsCancellationRequested)
        {
            try
            {
                var request = await _discovery.ReceiveAsync(token);
                if (request.Buffer.AsSpan().SequenceEqual(Query) && IsLocal(request.RemoteEndPoint.Address))
                    await _discovery.SendAsync(reply, request.RemoteEndPoint, token);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset) { }
        }
    }

    public async Task<IReadOnlyList<LanFound>> FindAsync(CancellationToken token)
    {
        using var socket = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        var addresses = _networks.Where(net => net.Mask != uint.MaxValue)
            .Select(net => (net.Address & net.Mask) | ~net.Mask)
            .Distinct().Select(value =>
            {
                var bytes = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
                return new IPAddress(bytes);
            }).Append(IPAddress.Broadcast);
        foreach (var address in addresses)
        {
            try { await socket.SendAsync(Query, new IPEndPoint(address, DiscoveryPort), token); }
            catch (SocketException) { /* An interface can disappear while we scan. */ }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        var peers = new Dictionary<Guid, LanFound>();
        try
        {
            while (true)
            {
                var packet = await socket.ReceiveAsync(timeout.Token);
                if (packet.RemoteEndPoint.Port != DiscoveryPort || packet.Buffer.Length > 1024 || !IsLocal(packet.RemoteEndPoint.Address)) continue;
                try
                {
                    var peer = JsonSerializer.Deserialize<LanPeer>(packet.Buffer);
                    if (peer is { Port: > 0 and <= 65535, Name.Length: > 0 and <= 128 }
                        && peer.Id != Guid.Empty && peer.Id != Self.Id && peers.Count < 100)
                        peers.TryAdd(peer.Id, new(peer, packet.RemoteEndPoint.Address));
                }
                catch (JsonException) { }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        return peers.Values.OrderBy(peer => peer.Peer.Name).ToList();
    }

    public Task<TcpClient> AcceptAsync(CancellationToken token) => _listener.AcceptTcpClientAsync(token).AsTask();

    public static async Task<SslStream> ServerTlsAsync(TcpClient client, X509Certificate2 certificate, CancellationToken token)
    {
        var tls = new SslStream(client.GetStream());
        try
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }, token);
            return tls;
        }
        catch { await tls.DisposeAsync(); throw; }
    }

    public static async Task<(SslStream Tls, string Fingerprint)> ClientTlsAsync(
        TcpClient client, IPAddress address, LanPeer peer, string fingerprint, CancellationToken token)
    {
        await client.ConnectAsync(address, peer.Port, token);
        var actual = "";
        var tls = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
        {
            if (certificate is null) return false;
            actual = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
            return IsIdentity(peer.Id, actual) && actual.StartsWith(fingerprint, StringComparison.Ordinal);
        });
        try
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "Sink PC Sync",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }, token);
            return (tls, actual);
        }
        catch { await tls.DisposeAsync(); throw; }
    }

    public static bool IsIdentity(Guid id, string fingerprint) =>
        fingerprint.Length == 64 && fingerprint.All(Uri.IsHexDigit)
        && id == new Guid(Convert.FromHexString(fingerprint.AsSpan(0, 32)));

    public void Dispose()
    {
        _discovery.Dispose();
        _listener.Stop();
    }

    private static uint Number(IPAddress address) => BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
}

internal static class LanWire
{
    private const int MaxSnapshot = 64 * 1024 * 1024;

    public static async Task SendAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value);
        if (payload.Length > MaxSnapshot) throw new InvalidDataException("This database is too large for PC sync.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(payload, token);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token, int limit = MaxSnapshot)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        var size = BinaryPrimitives.ReadInt32BigEndian(header);
        if (size <= 0 || size > limit) throw new InvalidDataException("The peer sent an invalid or oversized message.");
        var payload = new byte[size];
        await stream.ReadExactlyAsync(payload, token);
        return JsonSerializer.Deserialize<T>(payload)!;
    }
}
