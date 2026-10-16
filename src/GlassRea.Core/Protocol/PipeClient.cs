using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace GlassRea.Core.Protocol;

/// <summary>Talks to the Glass-Rea service. One request at a time; reconnects after any failure.</summary>
public sealed class PipeClient : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public async Task<T> Call<T>(Request request, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await Connect(ct);
                    await _writer!.WriteLineAsync(JsonSerializer.Serialize(request, Pipe.Json).AsMemory(), ct);
                    var line = await _reader!.ReadLineAsync(ct) ?? throw new IOException("The service closed the connection.");
                    var response = JsonSerializer.Deserialize<Response>(line, Pipe.Json)!;
                    if (!response.Ok) throw new ServiceException(response.Error ?? "The service reported an error.");
                    return response.Data is { } data ? data.Deserialize<T>(Pipe.Json)! : default!;
                }
                catch (Exception e) when (e is IOException or TimeoutException or ObjectDisposedException && attempt == 0)
                {
                    Reset(); // the service may have restarted; try once more on a fresh connection
                }
            }
        }
        finally { _gate.Release(); }
    }

    private async Task Connect(CancellationToken ct)
    {
        if (_pipe is { IsConnected: true }) return;
        Reset();
        _pipe = new NamedPipeClientStream(".", Pipe.Name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await _pipe.ConnectAsync(1500, ct);
        _reader = new StreamReader(_pipe, new UTF8Encoding(false));
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true };
    }

    private void Reset()
    {
        _reader?.Dispose(); _writer?.Dispose(); _pipe?.Dispose();
        _reader = null; _writer = null; _pipe = null;
    }

    public void Dispose() { Reset(); _gate.Dispose(); }
}

public sealed class ServiceException(string message) : Exception(message);
