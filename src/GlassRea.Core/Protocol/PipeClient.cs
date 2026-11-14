using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using GlassRea.Core.Paths;
using Microsoft.Win32.SafeHandles;

namespace GlassRea.Core.Protocol;

/// <summary>
/// Talks to the Glass-Rea service. One request at a time; reconnects after any failure. Talks only to the process
/// Windows runs as one of Glass-Rea's services: anything else answering on the pipe's name is hung up on.
/// </summary>
public sealed partial class PipeClient(string name = Pipe.Name) : IDisposable
{
    /// <summary>Why the app hangs up: the program serving the pipe isn't Glass-Rea's service.</summary>
    public const string NotTheService = "The program answering on Glass-Rea's pipe isn't Glass-Rea's service.";

    /// <summary>Whether the process serving the pipe may be talked to; Glass-Rea's services only, but for tests.</summary>
    internal Func<SafePipeHandle, bool> Trusts { get; init; } = ServedByService;

    /// <summary>How long a request may wait for its answer.</summary>
    internal TimeSpan Answer { get; init; } = TimeSpan.FromSeconds(30);

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
                using var waited = CancellationTokenSource.CreateLinkedTokenSource(ct);
                waited.CancelAfter(Answer);
                try
                {
                    await Connect(ct);
                    await _writer!.WriteLineAsync(JsonSerializer.Serialize(request, Pipe.Json).AsMemory(), waited.Token);
                    var line = await _reader!.ReadLineAsync(waited.Token) ?? throw new IOException("The service closed the connection.");
                    var response = JsonSerializer.Deserialize<Response>(line, Pipe.Json)!;
                    if (!response.Ok) throw new ServiceException(response.Error ?? "The service reported an error.");
                    return response.Data is { } data ? data.Deserialize<T>(Pipe.Json)! : default!;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // A service that took the request and never answers: the app shows it isn't answering rather than
                    // waiting for good. Not sent again, since it may have been a change.
                    Reset();
                    throw new TimeoutException($"The service didn't answer the {request.Op} request within {Answer.TotalSeconds:0} seconds.");
                }
                catch (Exception e) when (e is IOException or TimeoutException or ObjectDisposedException && e is not NotTheServiceException && attempt == 0)
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
        _pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await _pipe.ConnectAsync(1500, ct);
        if (!Trusts(_pipe.SafePipeHandle)) { Reset(); throw new NotTheServiceException(); }
        _reader = new StreamReader(_pipe, new UTF8Encoding(false));
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true };
    }

    private void Reset()
    {
        Close(_reader, _writer, _pipe);
        _reader = null; _writer = null; _pipe = null;
    }

    /// <summary>
    /// Closes each, even when the service has already closed its end: closing the writer flushes it, and flushing a
    /// pipe the other side closed throws, which would leave the pipe itself open.
    /// </summary>
    internal static void Close(params IDisposable?[] parts)
    {
        foreach (var part in parts)
        {
            try { part?.Dispose(); }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        }
    }

    public void Dispose() { Reset(); _gate.Dispose(); }

    private static bool ServedByService(SafePipeHandle pipe) =>
        GetNamedPipeServerProcessId(pipe, out var pid) && pid != 0
        && (ServiceProcess(Packaging.StoreService) == pid || ServiceProcess(Packaging.ClassicService) == pid);

    /// <summary>The process id Windows has for a running service, or 0 if it isn't running or isn't there.</summary>
    private static unsafe uint ServiceProcess(string service)
    {
        const uint ScManagerConnect = 0x1, ServiceQueryStatus = 0x4, ScStatusProcessInfo = 0;
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == 0) return 0;
        try
        {
            var handle = OpenService(manager, service, ServiceQueryStatus);
            if (handle == 0) return 0;
            try
            {
                StatusProcess status;
                return QueryServiceStatusEx(handle, ScStatusProcessInfo, &status, (uint)sizeof(StatusProcess), out _) ? status.ProcessId : 0;
            }
            finally { CloseServiceHandle(handle); }
        }
        finally { CloseServiceHandle(manager); }
    }

    /// <summary>SERVICE_STATUS_PROCESS.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct StatusProcess
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenSCManager(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenService(nint manager, string service, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryServiceStatusEx(nint service, uint level, void* buffer, uint size, out uint needed);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);
}

public sealed class ServiceException(string message) : Exception(message);

/// <summary>The pipe is served by something other than Glass-Rea's service; asking again won't change that.</summary>
public sealed class NotTheServiceException() : IOException(PipeClient.NotTheService);
