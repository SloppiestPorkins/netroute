using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NetRoute.Poc;

/// <summary>
/// Determines the public IP a socket actually egresses from, given a local source address.
///
/// <para>This is the ground truth behind the VERIFIED state in §24. A WFP filter existing
/// tells us what we asked Windows to do; this tells us what actually happened on the wire.
/// The two are not the same thing and the product is explicitly not allowed to conflate them.</para>
/// </summary>
public static class EgressProbe
{
    /// <summary>
    /// TCP path: plain HTTP GET to an echo service, with the socket bound to <paramref name="source"/>.
    /// </summary>
    public static async Task<ProbeResult> TcpAsync(IPAddress source, CancellationToken ct = default)
    {
        const string host = "api.ipify.org";

        try
        {
            using var socket = new Socket(source.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            // The bind is the whole point. Port 0 lets Windows pick the ephemeral port,
            // but the source address pins which interface the route lookup may use.
            socket.Bind(new IPEndPoint(source, 0));

            var addresses = await Dns.GetHostAddressesAsync(host, ct);
            var target = addresses.FirstOrDefault(a => a.AddressFamily == source.AddressFamily);
            if (target is null)
            {
                return ProbeResult.Fail($"no {source.AddressFamily} address for {host}");
            }

            await socket.ConnectAsync(new IPEndPoint(target, 80), ct);

            var request = Encoding.ASCII.GetBytes(
                $"GET / HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\nUser-Agent: NetRoute-POC\r\n\r\n");
            await socket.SendAsync(request, SocketFlags.None, ct);

            var buffer = new byte[4096];
            var received = new StringBuilder();
            int read;
            while ((read = await socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None, ct)) > 0)
            {
                received.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            var response = received.ToString();
            var separator = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (separator < 0)
            {
                return ProbeResult.Fail("malformed HTTP response");
            }

            var localEndpoint = (IPEndPoint?)socket.LocalEndPoint;
            return ProbeResult.Ok(response[(separator + 4)..].Trim(), localEndpoint?.Address);
        }
        catch (Exception ex)
        {
            return ProbeResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// UDP path: DNS TXT query for o-o.myaddr.l.google.com against ns1.google.com, which
    /// answers with the querying client's public address.
    ///
    /// <para>UDP needs its own probe rather than being assumed to follow TCP. §21 makes UDP
    /// mandatory from the start because it is what games actually use, and a TCP-only
    /// proof would not tell us anything about the case that matters.</para>
    /// </summary>
    public static async Task<ProbeResult> UdpAsync(IPAddress source, CancellationToken ct = default)
    {
        // ns1.google.com. Hard-coded because resolving it would itself use the default
        // interface and muddy what we are trying to measure.
        var resolver = IPAddress.Parse("216.239.32.10");

        if (source.AddressFamily != AddressFamily.InterNetwork)
        {
            return ProbeResult.Fail("UDP probe currently targets an IPv4 resolver only");
        }

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(source, 0));

            var query = BuildTxtQuery("o-o.myaddr.l.google.com", id: 0x4E52);
            await socket.SendToAsync(query, SocketFlags.None, new IPEndPoint(resolver, 53), ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));

            var buffer = new byte[512];
            var from = new IPEndPoint(IPAddress.Any, 0);
            var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, from, timeout.Token);

            var text = ParseFirstTxtRecord(buffer.AsSpan(0, result.ReceivedBytes));
            var localEndpoint = (IPEndPoint?)socket.LocalEndPoint;

            return text is null
                ? ProbeResult.Fail("no TXT record in DNS response")
                : ProbeResult.Ok(text, localEndpoint?.Address);
        }
        catch (OperationCanceledException)
        {
            return ProbeResult.Fail("UDP probe timed out (no reply)");
        }
        catch (Exception ex)
        {
            return ProbeResult.Fail(ex.Message);
        }
    }

    private static byte[] BuildTxtQuery(string name, ushort id)
    {
        var buffer = new List<byte>(64);

        buffer.Add((byte)(id >> 8));
        buffer.Add((byte)(id & 0xFF));
        buffer.AddRange(new byte[] { 0x01, 0x00 });  // standard query, recursion desired
        buffer.AddRange(new byte[] { 0x00, 0x01 });  // QDCOUNT = 1
        buffer.AddRange(new byte[] { 0x00, 0x00 });  // ANCOUNT
        buffer.AddRange(new byte[] { 0x00, 0x00 });  // NSCOUNT
        buffer.AddRange(new byte[] { 0x00, 0x00 });  // ARCOUNT

        foreach (var label in name.Split('.'))
        {
            buffer.Add((byte)label.Length);
            buffer.AddRange(Encoding.ASCII.GetBytes(label));
        }
        buffer.Add(0);                                // root label

        buffer.AddRange(new byte[] { 0x00, 0x10 });  // QTYPE = TXT
        buffer.AddRange(new byte[] { 0x00, 0x01 });  // QCLASS = IN

        return buffer.ToArray();
    }

    private static string? ParseFirstTxtRecord(ReadOnlySpan<byte> response)
    {
        if (response.Length < 12)
        {
            return null;
        }

        var answerCount = (response[6] << 8) | response[7];
        if (answerCount == 0)
        {
            return null;
        }

        var offset = 12;
        offset = SkipName(response, offset);
        offset += 4;  // QTYPE + QCLASS

        for (var i = 0; i < answerCount && offset < response.Length; i++)
        {
            offset = SkipName(response, offset);
            if (offset + 10 > response.Length)
            {
                return null;
            }

            var type = (response[offset] << 8) | response[offset + 1];
            var rdLength = (response[offset + 8] << 8) | response[offset + 9];
            offset += 10;

            if (offset + rdLength > response.Length)
            {
                return null;
            }

            if (type == 16 && rdLength > 1)
            {
                // TXT rdata is one or more length-prefixed strings; we only need the first.
                var stringLength = response[offset];
                return Encoding.ASCII.GetString(response.Slice(offset + 1, Math.Min(stringLength, rdLength - 1)));
            }

            offset += rdLength;
        }

        return null;
    }

    private static int SkipName(ReadOnlySpan<byte> buffer, int offset)
    {
        while (offset < buffer.Length)
        {
            var length = buffer[offset];

            if ((length & 0xC0) == 0xC0)
            {
                return offset + 2;  // compression pointer terminates the name
            }
            if (length == 0)
            {
                return offset + 1;
            }

            offset += length + 1;
        }

        return offset;
    }
}

public sealed record ProbeResult(bool Success, string? PublicAddress, IPAddress? BoundLocalAddress, string? Error)
{
    public static ProbeResult Ok(string publicAddress, IPAddress? local)
        => new(true, publicAddress, local, null);

    public static ProbeResult Fail(string error)
        => new(false, null, null, error);
}
