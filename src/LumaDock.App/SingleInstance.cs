using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using LumaDock.Core;
using LumaDock.Storage;

namespace LumaDock.App;

/// <summary>One resident process per data directory and session, with same-user activation over a sleeping pipe.</summary>
internal sealed class SingleInstance : IDisposable
{
    private const byte OpenLaunchpad = 1;
    private const byte Acknowledgement = 0xAC;
    private readonly Mutex mutex;
    private readonly string pipeName;
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private NamedPipeServerStream? currentServer;
    private Task? listener;
    private bool disposed;
    public bool IsPrimary { get; }
    internal Task ListenerCompletion => listener ?? Task.CompletedTask;

    public SingleInstance(string dataDirectory)
    {
        var identity = Catalog.StableId(Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        mutex = new Mutex(false, "Local\\LumaDock-" + identity);
        try { IsPrimary = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsPrimary = true; }
        // Mutexes are session-local; keep pipes session-local too for fast-user switching/RDP.
        using var process = Process.GetCurrentProcess();
        pipeName = $"LumaDock-{process.SessionId}-{identity}";
    }

    public void StartListener(Action activate)
    {
        if (!IsPrimary) throw new InvalidOperationException("Only the resident process can listen for activation.");
        ObjectDisposedException.ThrowIf(disposed, this);
        if (listener != null) throw new InvalidOperationException("Activation listener is already running.");
        listener = ListenAsync(activate, stop.Token);
    }

    private async Task ListenAsync(Action activate, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            var endpointCreated = false;
            try
            {
                using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                endpointCreated = true;
                lock (gate)
                {
                    if (disposed) return;
                    currentServer = server;
                }
                await server.WaitForConnectionAsync(cancellation).ConfigureAwait(false);
                // A stalled local client must not hold the single activation endpoint indefinitely.
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                var request = new byte[1];
                if (await server.ReadAsync(request, requestTimeout.Token).ConfigureAwait(false) == 1 && request[0] == OpenLaunchpad)
                {
                    activate();
                    await server.WriteAsync(new byte[] { Acknowledgement }, requestTimeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { if (cancellation.IsCancellationRequested) break; }
            catch (ObjectDisposedException) when (cancellation.IsCancellationRequested) { break; }
            catch (IOException ex)
            {
                if (cancellation.IsCancellationRequested) break;
                Log.Write("Activation connection closed", ex);
                if (!endpointCreated) break;
            }
            catch (Exception ex)
            {
                // Stop on a permanent endpoint/dispatcher failure instead of retrying in the background.
                Log.Write("Activation listener stopped", ex); break;
            }
            finally { lock (gate) currentServer = null; }
        }
    }

    public async Task<bool> RequestActivationAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            // ConnectAsync waits for the first process to finish setting up its listener; no polling.
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            if (GetNamedPipeServerProcessId(client.SafePipeHandle, out var serverProcessId))
                AllowSetForegroundWindow(serverProcessId);
            await client.WriteAsync(new byte[] { OpenLaunchpad }, timeout.Token).ConfigureAwait(false);
            var response = new byte[1];
            return await client.ReadAsync(response, timeout.Token).ConfigureAwait(false) == 1 && response[0] == Acknowledgement;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException)
        { Log.Write("Activation request failed", ex); return false; }
    }

    public void Dispose()
    {
        if (disposed) return;
        lock (gate)
        {
            disposed = true; stop.Cancel(); currentServer?.Dispose();
        }
        // Main constructs/disposes this on the STA UI thread, preserving Mutex ownership rules.
        if (IsPrimary) mutex.ReleaseMutex();
        mutex.Dispose();
        if (listener == null || listener.IsCompleted) stop.Dispose();
        else _ = listener.ContinueWith(_ => stop.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
