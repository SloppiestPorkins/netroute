using System.Buffers;
using System.Text.Json;

namespace NetRoute.Ipc;

/// <summary>
/// Shared bounded NDJSON framing. Reads are buffered (one pipe read returns many bytes, not one),
/// a line longer than <see cref="MaximumLineBytes"/> is drained and reported rather than stored,
/// and a response that would exceed the cap is refused before anything is written, so the stream
/// stays usable.
/// </summary>
public sealed class IpcConnection(Stream stream) : IDisposable
{
    public const int MaximumLineBytes = 1024 * 1024;
    private readonly Stream _stream = stream;
    private readonly byte[] _buffer = new byte[16 * 1024];
    private int _start, _end;

    public async Task<T?> ReadAsync<T>(CancellationToken ct = default)
    {
        var line = new ArrayBufferWriter<byte>();
        var oversized = false;
        var any = false;
        while (true)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = await _stream.ReadAsync(_buffer.AsMemory(), ct);
                if (_end == 0)
                    return any ? throw new EndOfStreamException("Incomplete IPC message.") : default;
            }
            if (TakeBufferedChunk(line, ref oversized, ref any)) break;
        }
        if (oversized) throw new IpcLineTooLongException();
        return Deserialize<T>(line);
    }

    // The span work lives in its own method because a span cannot be a local in an async
    // method under C# 12. Neither of these awaits anything, so nothing is held across one.

    /// <summary>Takes what is buffered up to the next newline. True once the line is complete.</summary>
    private bool TakeBufferedChunk(ArrayBufferWriter<byte> line, ref bool oversized, ref bool any)
    {
        var span = _buffer.AsSpan(_start, _end - _start);
        var newline = span.IndexOf((byte)'\n');
        var chunk = newline >= 0 ? span[..newline] : span;
        if (chunk.Length > 0) any = true;
        if (!oversized)
        {
            if (line.WrittenCount + chunk.Length > MaximumLineBytes) oversized = true;
            else line.Write(chunk);
        }
        if (newline >= 0)
        {
            _start += newline + 1;
            return true;
        }
        _start = _end;
        return false;
    }

    private static T? Deserialize<T>(ArrayBufferWriter<byte> line)
    {
        var bytes = line.WrittenSpan;
        if (bytes.Length > 0 && bytes[^1] == (byte)'\r') bytes = bytes[..^1];
        return JsonSerializer.Deserialize<T>(bytes, IpcProtocol.JsonOptions);
    }

    /// <summary>Throws <see cref="IpcLineTooLongException"/>, having written nothing, if the value is over the cap.</summary>
    public async Task WriteAsync<T>(T value, CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, IpcProtocol.JsonOptions);
        if (payload.Length > MaximumLineBytes) throw new IpcLineTooLongException();
        var framed = new byte[payload.Length + 1];
        payload.CopyTo(framed, 0);
        framed[^1] = (byte)'\n';
        await _stream.WriteAsync(framed, ct);
        await _stream.FlushAsync(ct);
    }

    public void Dispose() => _stream.Dispose();
}

public sealed class IpcLineTooLongException : Exception;
