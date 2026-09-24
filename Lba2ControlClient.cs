using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace LBAAssembler;

// TCP client for the LBA2 engine's --listen control socket (CONTROL_SERVER.CPP), used only to
// drive script breakpoints during an embedded Play session. See
// native/lba2-classic-community/docs/CONTROL.md and CONTROL_PROTO.H for the wire protocol this
// ports: one command per line in, that command's console output back verbatim, terminated by a
// line reading "<<END>>". While `stream on` is active, anything the engine logs is ALSO pushed
// "! "-prefixed, interleaved with command responses -- SCRIPT_BREAKPOINT.CPP (native) logs
// "[breakpoint] paused actor=.. kind=.. offset=.." on a hit, via the engine's own Log_Info, which
// is what PausedLine below watches for. A background loop reads continuously (not just while a
// command is in flight) so a pushed pause is seen promptly rather than only on the next command's
// response, matching the engine's own per-frame event flush (ControlServer_Poll).
internal sealed class Lba2ControlClient : IDisposable
{
    private const string Terminator = "<<END>>";
    private const string EventPrefix = "! ";
    private static readonly Regex PausedLine = new(@"\[breakpoint\] paused actor=(-?\d+) kind=(\d+) offset=(-?\d+)");

    private readonly TcpClient client;
    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    private readonly object sendLock = new();
    private readonly Thread readThread;
    private volatile bool disposed;
    private readonly StringBuilder pendingResponse = new();
    private TaskCompletionSource<string>? pendingCompletion;

    // Raised from the background read thread -- the caller (MainWindow.Play.cs) marshals to the UI thread.
    public event Action<int, int, int>? BreakpointHit; // actor, kind, offset
    public event Action<string>? Disconnected;

    private Lba2ControlClient(TcpClient client)
    {
        this.client = client;
        var stream = client.GetStream();
        reader = new StreamReader(stream, Encoding.UTF8);
        writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        readThread = new Thread(ReadLoop) { IsBackground = true, Name = "Lba2ControlClient" };
        readThread.Start();
    }

    // Connects and consumes the engine's own greeting (its first "ready" response), retrying for
    // a few seconds since the engine takes a moment after launch to start listening. Returns null
    // if it never comes up -- the caller treats breakpoints as simply unavailable this session.
    public static async Task<Lba2ControlClient?> ConnectAsync(int port, CancellationToken cancel)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && !cancel.IsCancellationRequested)
        {
            TcpClient? tcp = null;
            try
            {
                tcp = new TcpClient();
                await tcp.ConnectAsync(System.Net.IPAddress.Loopback, port, cancel);
                var self = new Lba2ControlClient(tcp);
                await self.SendAsync("stream on"); // enables the "! "-prefixed push events breakpoint hits arrive as
                return self;
            }
            catch (Exception error) when (error is SocketException or IOException or ObjectDisposedException)
            {
                tcp?.Dispose();
                try { await Task.Delay(300, cancel); } catch (OperationCanceledException) { return null; }
            }
        }
        return null;
    }

    // One console command; returns its response lines (without the terminator). Commands are
    // serialized (the server handles one client, one command at a time) -- a second caller waits
    // for the first's response before its own is sent.
    public Task<string> SendAsync(string command)
    {
        lock (sendLock)
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingCompletion = tcs;
            try { writer.WriteLine(command); }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                pendingCompletion = null;
                tcs.TrySetException(error);
            }
            return tcs.Task;
        }
    }

    private void ReadLoop()
    {
        try
        {
            while (!disposed)
            {
                var line = reader.ReadLine();
                if (line is null) { RaiseDisconnected("connection closed"); return; }
                if (line == Terminator)
                {
                    TaskCompletionSource<string>? tcs;
                    string body;
                    lock (sendLock)
                    {
                        tcs = pendingCompletion;
                        pendingCompletion = null;
                        body = pendingResponse.ToString();
                        pendingResponse.Clear();
                    }
                    tcs?.TrySetResult(body);
                    continue;
                }
                if (line.StartsWith(EventPrefix, StringComparison.Ordinal))
                {
                    HandleEvent(line[EventPrefix.Length..]);
                    continue;
                }
                lock (sendLock) pendingResponse.Append(line).Append('\n');
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            RaiseDisconnected(error.Message);
        }
    }

    private void HandleEvent(string line)
    {
        var m = PausedLine.Match(line);
        if (!m.Success) return;
        BreakpointHit?.Invoke(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
    }

    private void RaiseDisconnected(string why)
    {
        if (disposed) return;
        TaskCompletionSource<string>? tcs;
        lock (sendLock) { tcs = pendingCompletion; pendingCompletion = null; }
        tcs?.TrySetException(new IOException(why));
        Disconnected?.Invoke(why);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { client.Close(); } catch (SocketException) { }
        reader.Dispose();
        try { writer.Dispose(); } catch (IOException) { }
    }
}
