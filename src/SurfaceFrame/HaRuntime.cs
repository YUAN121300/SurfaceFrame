using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SurfaceFrame;

readonly record struct HaSnapshot(
    ConnPhase Phase,
    bool ControlsEnabled,
    PresenceKind Presence,
    IReadOnlyList<CardView> Cards,
    IReadOnlyDictionary<string, CardView> AllStates,
    bool Fault);

sealed class HaRuntime : IDisposable
{
    readonly object _gate = new();
    readonly HaClientLogic _logic;
    readonly DemoBroker? _demo;
    readonly Uri? _uri;
    readonly bool _missingToken;
    CancellationTokenSource? _appStop;
    CancellationTokenSource? _session;
    ChannelWriter<string>? _send;
    Task? _socketTask;

    public bool MissingToken => _missingToken;

    public HaRuntime(LoadedConfig config)
    {
        _logic = new HaClientLogic(
            config.HomeAssistant.Entities,
            config.HomeAssistant.PresenceEntityId,
            config.HomeAssistant.PresenceHome,
            config.HomeAssistant.PresenceAway,
            config.HaToken ?? "",
            config.HomeAssistant.AllTrackedInfoEntities());
        if (config.Demo.Enabled)
        {
            _demo = new DemoBroker(config.HomeAssistant.Entities, config.HomeAssistant.PresenceEntityId, config.HomeAssistant.PresenceHome);
            return;
        }
        if (string.IsNullOrEmpty(config.HaToken))
        {
            _missingToken = true;
            return;
        }
        _uri = new Uri(config.HomeAssistant.Url);
    }

    public void Start()
    {
        // Demo stays inside the process and never opens a socket.
        if (_demo != null)
        {
            Resume(DateTime.UtcNow);
            return;
        }
        if (_uri == null)
            return;
        _appStop = new CancellationTokenSource();
        _socketTask = Task.Run(() => RunAsync(_appStop.Token));
    }

    public void Tick(DateTime now)
    {
        lock (_gate)
        {
            if (_demo != null)
                DemoTick(now);
            else
                Enqueue(_logic.OnTimer(now));
        }
    }

    public HaSnapshot Take()
    {
        lock (_gate)
        {
            return new HaSnapshot(
                _logic.Phase,
                _logic.ControlsEnabled,
                _logic.Presence,
                _logic.Cards(),
                _logic.SnapshotAll(),
                _logic.ConsumeTransportFault());
        }
    }

    public bool ControlsEnabled
    {
        get { lock (_gate) return _logic.ControlsEnabled; }
    }

    public bool Press(string entityId, bool turnOn, DateTime now)
    {
        lock (_gate)
        {
            return _logic.TryCommand(entityId, turnOn, now, out var json) && DispatchCommand(json!, now);
        }
    }

    public bool Retry(string entityId, DateTime now)
    {
        lock (_gate)
            return _logic.TryRetry(entityId, now, out var json) && DispatchCommand(json!, now);
    }

    public bool CallService(string domain, string service, object target, DateTime now)
    {
        lock (_gate)
            return _logic.TryCallService(domain, service, target, out var json) && DispatchCommand(json!, now);
    }

    bool DispatchCommand(string json, DateTime now)
    {
        if (_demo != null)
            Drain(_demo.Accept(json, now), now);
        else
            Enqueue(new[] { json });
        return true;
    }

    public void SetPresence(string state, DateTime now)
    {
        if (_demo == null)
            return;
        lock (_gate)
            Drain(_demo.SetPresence(state, now), now);
    }

    public void SetPace(ConfirmPace pace)
    {
        if (_demo == null)
            return;
        lock (_gate)
            _demo.Pace = pace;
    }

    public void Drop(DateTime now)
    {
        if (_demo != null)
        {
            lock (_gate)
            {
                _demo.Disconnect();
                _logic.OnTransportClosed(now);
            }
            return;
        }
        DropSocket();
    }

    public void Resume(DateTime now)
    {
        if (_demo == null)
            return;
        lock (_gate)
        {
            _demo.Connect();
            _logic.BeginSession(now);
            Drain(_demo.Start(now), now);
        }
    }

    public void DropSocket()
    {
        try { _session?.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        try { _appStop?.Cancel(); } catch (ObjectDisposedException) { }
        DropSocket();
        try { _socketTask?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _appStop?.Dispose();
    }

    void DemoTick(DateTime now)
    {
        var seed = new List<string>();
        foreach (var outgoing in _logic.OnTimer(now))
            seed.AddRange(_demo!.Accept(outgoing, now));
        seed.AddRange(_demo!.Poll(now));
        Drain(seed, now);
    }

    void Drain(IEnumerable<string> seed, DateTime now)
    {
        var queue = new Queue<string>(seed);
        var guard = 0;
        while (queue.Count > 0 && guard++ < 40)
        {
            var message = queue.Dequeue();
            foreach (var outgoing in _logic.OnMessage(message, now))
                foreach (var response in _demo!.Accept(outgoing, now))
                    queue.Enqueue(response);
        }
    }

    void Enqueue(IReadOnlyList<string> messages)
    {
        var writer = _send;
        if (writer == null)
            return;
        foreach (var message in messages)
        {
            if (!writer.TryWrite(message))
                return;
        }
    }

    async Task RunAsync(CancellationToken appCt)
    {
        var delay = ReconnectBackoff.InitialMs;
        while (!appCt.IsCancellationRequested)
        {
            var reached = false;
            try
            {
                using var session = CancellationTokenSource.CreateLinkedTokenSource(appCt);
                _session = session;
                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                Log.Info("ha connecting");
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(session.Token))
                {
                    connect.CancelAfter(FrameTiming.SessionDeadline);
                    await socket.ConnectAsync(_uri!, connect.Token).ConfigureAwait(false);
                }
                var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false
                });
                _send = channel.Writer;
                lock (_gate)
                    _logic.BeginSession(DateTime.UtcNow);
                var sendTask = SendLoop(socket, channel.Reader, session.Token);
                var recvTask = RecvLoop(socket, channel.Writer, session.Token);
                await Task.WhenAny(sendTask, recvTask).ConfigureAwait(false);
                lock (_gate)
                {
                    _send = null;
                    _logic.OnTransportClosed(DateTime.UtcNow);
                }
                session.Cancel();
                channel.Writer.TryComplete();
                try { await Task.WhenAll(sendTask, recvTask).ConfigureAwait(false); } catch { }
                lock (_gate)
                    reached = _logic.ConsumeReachedLive();
            }
            catch (OperationCanceledException) when (appCt.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Info("ha " + ex.GetType().Name);
                lock (_gate)
                    reached = _logic.ConsumeReachedLive();
            }
            finally
            {
                _send = null;
                lock (_gate)
                    _logic.OnTransportClosed(DateTime.UtcNow);
            }
            if (appCt.IsCancellationRequested)
                break;
            if (reached)
                delay = ReconnectBackoff.InitialMs;
            try { await Task.Delay(delay, appCt).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            if (!reached)
                delay = ReconnectBackoff.Next(delay);
        }
    }

    async Task SendLoop(ClientWebSocket socket, ChannelReader<string> reader, CancellationToken ct)
    {
        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (reader.TryRead(out var message))
            {
                var bytes = Encoding.UTF8.GetBytes(message);
                await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
        }
    }

    async Task RecvLoop(ClientWebSocket socket, ChannelWriter<string> writer, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var stream = new System.IO.MemoryStream();
        while (!ct.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (result.MessageType != WebSocketMessageType.Text)
                break;
            stream.Write(buffer, 0, result.Count);
            if (stream.Length > 8 * 1024 * 1024)
                break;
            if (!result.EndOfMessage)
                continue;
            var text = Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
            stream.SetLength(0);
            List<string> outbound;
            lock (_gate)
                outbound = new List<string>(_logic.OnMessage(text, DateTime.UtcNow));
            foreach (var message in outbound)
            {
                if (!writer.TryWrite(message))
                    return;
            }
        }
    }
}
