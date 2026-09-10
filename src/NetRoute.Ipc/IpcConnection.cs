using System.Text;
using System.Text.Json;

namespace NetRoute.Ipc;

/// <summary>Shared bounded NDJSON framing prevents either peer from allocating unbounded input.</summary>
public sealed class IpcConnection(Stream stream) : IDisposable
{
    public const int MaximumLineBytes = 1024 * 1024;
    private readonly Stream _stream = stream;

    public async Task<T?> ReadAsync<T>(CancellationToken ct = default)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        var oversized = false;
        while (true)
        {
            var read = await _stream.ReadAsync(one, ct);
            if (read == 0) return bytes.Count == 0 ? default : throw new EndOfStreamException("Incomplete IPC message.");
            if (one[0] == (byte)'\n') break;
            if (bytes.Count >= MaximumLineBytes) oversized = true;
            else if (one[0] != (byte)'\r') bytes.Add(one[0]);
        }
        if (oversized) throw new IpcLineTooLongException();
        return JsonSerializer.Deserialize<T>(bytes.ToArray(), IpcProtocol.JsonOptions);
    }

    public async Task WriteAsync<T>(T value, CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, IpcProtocol.JsonOptions);
        await _stream.WriteAsync(payload, ct);
        await _stream.WriteAsync(new byte[] { (byte)'\n' }, ct);
        await _stream.FlushAsync(ct);
    }

    public void Dispose() => _stream.Dispose();
}

public sealed class IpcLineTooLongException : Exception;
