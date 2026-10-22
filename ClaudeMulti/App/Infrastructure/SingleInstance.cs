using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeMulti.App.Infrastructure;

/// <summary>
/// Lets only one manager run per user. A lock file (which the system releases if the process dies) decides who is
/// first; the first one listens on a pipe so a later launch can tell it to come to the front. Works the same on
/// Windows, macOS and Linux (named events are Windows-only, and named mutexes are shared between users elsewhere).
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string ShowMessage = "show";

    private readonly string pipeName;
    private readonly CancellationTokenSource stop = new();
    private FileStream? lockFile;

    public SingleInstance(string folder)
    {
        Directory.CreateDirectory(folder);
        LockPath = Path.Combine(folder, "manager.lock");
        // The user name keeps the pipe (a socket file under /tmp on macOS and Linux) apart on shared machines.
        pipeName = "ClaudeMulti.Show." + Environment.UserName;
    }

    public string LockPath { get; }

    /// <summary>True when this process is the first manager. Otherwise the running one has been asked to show itself.</summary>
    public bool TryBecomeFirst()
    {
        try
        {
            lockFile = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AskRunningInstanceToShow();
            return false;
        }
    }

    private void AskRunningInstanceToShow()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(ShowMessage);
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
        {
            // The other manager is still starting; it shows its window anyway.
        }
    }

    /// <summary>Calls <paramref name="onShow"/> (on a background thread) each time another launch asks for the window.</summary>
    public void Listen(Action onShow) => _ = Task.Run(() => ListenLoop(onShow));

    private async Task ListenLoop(Action onShow)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(stop.Token);
                using var reader = new StreamReader(server);
                if (await reader.ReadLineAsync(stop.Token) == ShowMessage) onShow();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(500); // a client hung up early; listen again
            }
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        lockFile?.Dispose();
    }
}
