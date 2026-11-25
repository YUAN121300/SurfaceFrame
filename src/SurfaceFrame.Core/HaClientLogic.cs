using System.Globalization;
using System.Text.Json;

namespace SurfaceFrame;

public enum ConnPhase
{
    Idle,
    WaitAuth,
    Authenticating,
    Subscribing,
    Snapshotting,
    Live,
    Disconnected
}

public enum PendingKind
{
    None,
    TurningOn,
    TurningOff,
    Unconfirmed
}

public sealed record CardView(
    string EntityId,
    string Name,
    EntityKind Kind,
    string? State,
    string Display,
    PendingKind Pending,
    bool Known,
    string? Group = null,
    string? Icon = null,
    IReadOnlyDictionary<string, string>? Attributes = null);

public sealed class HaClientLogic
{
    static readonly string[] ScalarAttributeKeys =
    {
        "temperature",
        "humidity",
        "current_temperature",
        "current_position",
        "hvac_action",
        "unit_of_measurement",
        "month_cost",
        "last_month_cost",
        "month_missing_days",
        "year_cost",
        "year_usage",
        "tier",
        "rate"
    };

    static readonly string[] JsonObjectAttributeKeys =
    {
        "daily_cost",
        "daily_usage"
    };

    readonly Dictionary<string, EntityConfig> _cards = new(StringComparer.Ordinal);
    readonly List<EntityConfig> _order = new();
    readonly HashSet<string> _trackedInfo = new(StringComparer.Ordinal);
    readonly Dictionary<string, Tracked> _states = new(StringComparer.Ordinal);
    readonly Dictionary<string, Inflight> _inflight = new(StringComparer.Ordinal);
    readonly List<Tracked> _buffer = new();
    readonly string _presenceId;
    readonly string _home;
    readonly string _away;
    readonly string _token;

    int _nextId;
    int _subscribeId;
    int _statesId;
    int _pongId;
    DateTime _pongDeadline;
    DateTime _lastPing;
    DateTime? _phaseDeadline;
    bool _transportFault;
    bool _reachedLive;

    public ConnPhase Phase { get; private set; } = ConnPhase.Idle;
    public bool ControlsEnabled { get; private set; }
    public PresenceKind Presence { get; private set; } = PresenceKind.Unknown;

    sealed class Inflight
    {
        public bool TurnOn { get; init; }
        public int MessageId { get; set; }
        public DateTime? Deadline { get; set; }
    }

    sealed class Tracked
    {
        public string EntityId = "";
        public string State = "";
        public string Display = "";
        public DateTimeOffset Stamp;
        public Dictionary<string, string> Attributes = new(StringComparer.Ordinal);
    }

    public HaClientLogic(
        IReadOnlyList<EntityConfig> entities,
        string presenceEntityId,
        string presenceHome,
        string presenceAway,
        string token,
        IReadOnlyList<EntityConfig>? infoEntities = null)
    {
        foreach (var entity in entities)
        {
            if (string.IsNullOrWhiteSpace(entity.EntityId) || _cards.ContainsKey(entity.EntityId))
                continue;
            _cards[entity.EntityId] = entity;
            _order.Add(entity);
            if (entity.EntityIds != null)
            {
                foreach (var subId in entity.EntityIds)
                {
                    if (!string.IsNullOrWhiteSpace(subId))
                        _trackedInfo.Add(subId);
                }
            }
        }
        if (infoEntities != null)
        {
            foreach (var entity in infoEntities)
            {
                if (!string.IsNullOrWhiteSpace(entity.EntityId))
                    _trackedInfo.Add(entity.EntityId);
            }
        }
        _presenceId = presenceEntityId ?? "";
        _home = string.IsNullOrWhiteSpace(presenceHome) ? "home" : presenceHome;
        _away = string.IsNullOrWhiteSpace(presenceAway) ? "not_home" : presenceAway;
        _token = token ?? "";
    }

    public void BeginSession(DateTime now)
    {
        Phase = ConnPhase.WaitAuth;
        ControlsEnabled = false;
        _buffer.Clear();
        MarkCommandsUnconfirmed();
        _subscribeId = 0;
        _statesId = 0;
        _pongId = 0;
        _phaseDeadline = now + FrameTiming.SessionDeadline;
        _transportFault = false;
        _reachedLive = false;
    }

    public void OnTransportClosed(DateTime now)
    {
        if (Phase == ConnPhase.Disconnected)
            return;
        Phase = ConnPhase.Disconnected;
        ControlsEnabled = false;
        MarkCommandsUnconfirmed();
        _buffer.Clear();
        _pongId = 0;
        _subscribeId = 0;
        _statesId = 0;
        _phaseDeadline = null;
    }

    public bool ConsumeTransportFault()
    {
        var fault = _transportFault;
        _transportFault = false;
        return fault;
    }

    public bool ConsumeReachedLive()
    {
        var reached = _reachedLive;
        _reachedLive = false;
        return reached;
    }

    public bool TryCommand(string entityId, bool turnOn, DateTime now, out string? json)
    {
        json = null;
        if (!CanCommand(entityId, out var card) || _inflight.ContainsKey(entityId))
            return false;
        var command = new Inflight { TurnOn = turnOn };
        _inflight.Add(entityId, command);
        json = SendCommand(card, command, now);
        return true;
    }

    public bool TryRetry(string entityId, DateTime now, out string? json)
    {
        json = null;
        if (!CanCommand(entityId, out var card)
            || !_inflight.TryGetValue(entityId, out var command) || command.Deadline.HasValue)
            return false;
        json = SendCommand(card, command, now);
        return true;
    }

    bool CanCommand(string entityId, out EntityConfig card)
    {
        if (!_cards.TryGetValue(entityId, out var candidate))
        {
            card = null!;
            return false;
        }
        card = candidate;
        if (Phase != ConnPhase.Live || !ControlsEnabled || card.Kind == EntityKind.Sensor)
            return false;
        var state = StateOf(entityId);
        return card.Kind == EntityKind.Cover
            ? state is "open" or "closed" or "opening" or "closing"
            : state is "on" or "off";
    }

    public bool TryCallService(string domain, string service, object target, out string? json)
    {
        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(service))
        {
            json = null;
            return false;
        }
        var id = ++_nextId;
        json = ServiceJson(id, domain, service, target);
        return true;
    }

    string SendCommand(EntityConfig card, Inflight command, DateTime now)
    {
        command.MessageId = ++_nextId;
        command.Deadline = now + FrameTiming.CommandTimeout;
        var (domain, service) = card.Kind switch
        {
            EntityKind.Light => ("light", command.TurnOn ? "turn_on" : "turn_off"),
            EntityKind.Cover => ("cover", command.TurnOn ? "open_cover" : "close_cover"),
            _ => ("switch", command.TurnOn ? "turn_on" : "turn_off")
        };
        object target = card.EntityIds is { Count: > 1 } ids ? ids : card.EntityId;
        return ServiceJson(command.MessageId, domain, service, target);
    }

    void MarkCommandsUnconfirmed()
    {
        foreach (var command in _inflight.Values)
            command.Deadline = null;
    }

    public IReadOnlyList<string> OnTimer(DateTime now)
    {
        var list = new List<string>();
        if (Phase is ConnPhase.Idle or ConnPhase.Disconnected)
            return list;

        if (Phase != ConnPhase.Live && _phaseDeadline is DateTime deadline && now >= deadline)
        {
            Fault();
            return list;
        }

        if (_pongId != 0 && now >= _pongDeadline)
        {
            Fault();
            return list;
        }

        if (Phase == ConnPhase.Live && _pongId == 0 && now - _lastPing >= FrameTiming.Heartbeat)
        {
            var id = ++_nextId;
            _pongId = id;
            _pongDeadline = now + FrameTiming.PongTimeout;
            _lastPing = now;
            list.Add(JsonSerializer.Serialize(new { id, type = "ping" }));
        }

        if (Phase != ConnPhase.Live)
            return list;

        foreach (var command in _inflight.Values)
        {
            if (command.Deadline is DateTime expires && now >= expires)
                command.Deadline = null;
        }
        return list;
    }

    public IReadOnlyList<string> OnMessage(string json, DateTime now)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                return Array.Empty<string>();
            switch (typeEl.GetString())
            {
                case "auth_required":
                    if (Phase != ConnPhase.WaitAuth)
                        return Array.Empty<string>();
                    Phase = ConnPhase.Authenticating;
                    return new[]
                    {
                        JsonSerializer.Serialize(new Dictionary<string, string>
                        {
                            ["type"] = "auth",
                            ["access_token"] = _token
                        })
                    };
                case "auth_invalid":
                    Fault();
                    return Array.Empty<string>();
                case "auth_ok":
                    if (Phase != ConnPhase.Authenticating)
                        return Array.Empty<string>();
                    return BeginSubscribe();
                case "event":
                    OnEvent(root, now);
                    return Array.Empty<string>();
                case "pong":
                    ClearPong(ReadId(root));
                    return Array.Empty<string>();
                case "result":
                    return OnResult(root, now);
                default:
                    return Array.Empty<string>();
            }
        }
    }

    // Subscribe to state_changed before get_states so events that arrive during the snapshot can be reconciled.
    IReadOnlyList<string> BeginSubscribe()
    {
        Phase = ConnPhase.Subscribing;
        _buffer.Clear();
        _subscribeId = ++_nextId;
        return new[]
        {
            JsonSerializer.Serialize(new
            {
                id = _subscribeId,
                type = "subscribe_events",
                event_type = "state_changed"
            })
        };
    }

    void OnEvent(JsonElement root, DateTime now)
    {
        if (Phase is not (ConnPhase.Subscribing or ConnPhase.Snapshotting or ConnPhase.Live))
            return;
        if (!root.TryGetProperty("event", out var ev) || ev.ValueKind != JsonValueKind.Object)
            return;
        if (!ev.TryGetProperty("event_type", out var eventType)
            || eventType.ValueKind != JsonValueKind.String || eventType.GetString() != "state_changed")
            return;
        if (!ev.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return;
        if (!data.TryGetProperty("new_state", out var newState))
            return;
        Tracked tracked;
        bool hadStamp;
        if (newState.ValueKind == JsonValueKind.Null
            && data.TryGetProperty("entity_id", out var removedId) && removedId.ValueKind == JsonValueKind.String
            && removedId.GetString() is string entityId && IsTracked(entityId))
        {
            tracked = new Tracked { EntityId = entityId, State = "unknown", Display = "未知", Stamp = new DateTimeOffset(now) };
            hadStamp = false;
        }
        else if (!TryParseState(newState, now, out tracked, out hadStamp))
        {
            return;
        }
        if (!hadStamp && TryReadStamp(ev, "time_fired", out var fired))
            tracked.Stamp = fired;
        if (Phase == ConnPhase.Live)
            ApplyLive(tracked);
        else
            _buffer.Add(tracked);
    }

    IReadOnlyList<string> OnResult(JsonElement root, DateTime now)
    {
        var id = ReadId(root);
        if (_pongId != 0 && id == _pongId)
        {
            ClearPong(id);
            return Array.Empty<string>();
        }

        var success = root.TryGetProperty("success", out var successEl) && successEl.ValueKind == JsonValueKind.True;
        if (id == _subscribeId && Phase == ConnPhase.Subscribing)
        {
            if (!success)
            {
                Fault();
                return Array.Empty<string>();
            }
            Phase = ConnPhase.Snapshotting;
            _statesId = ++_nextId;
            return new[] { JsonSerializer.Serialize(new { id = _statesId, type = "get_states" }) };
        }

        if (id == _statesId && Phase == ConnPhase.Snapshotting)
        {
            if (!success || !root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            {
                Fault();
                return Array.Empty<string>();
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in result.EnumerateArray())
            {
                if (!TryParseState(item, now, out var tracked, out _))
                    continue;
                ApplyState(tracked);
                seen.Add(tracked.EntityId);
            }
            foreach (var entityId in AllTracked())
            {
                if (seen.Contains(entityId))
                    continue;
                ApplyState(new Tracked
                {
                    EntityId = entityId,
                    State = "unknown",
                    Display = "未知",
                    Stamp = DateTimeOffset.MinValue
                });
            }
            foreach (var buffered in _buffer)
                ApplyLive(buffered);
            _buffer.Clear();
            Phase = ConnPhase.Live;
            ControlsEnabled = true;
            _lastPing = now;
            _phaseDeadline = null;
            _reachedLive = true;
            return Array.Empty<string>();
        }

        if (!success && Phase == ConnPhase.Live)
        {
            foreach (var command in _inflight.Values)
            {
                if (command.MessageId == id)
                    command.Deadline = null;
            }
        }
        return Array.Empty<string>();
    }

    void ApplyLive(Tracked tracked)
    {
        if (!IsTracked(tracked.EntityId))
            return;
        if (_states.TryGetValue(tracked.EntityId, out var current) && tracked.Stamp < current.Stamp)
            return;
        ApplyState(tracked);
    }

    void ApplyState(Tracked tracked)
    {
        _states[tracked.EntityId] = tracked;
        if (tracked.EntityId == _presenceId)
            Presence = MapPresence(tracked.State);
        if (_inflight.TryGetValue(tracked.EntityId, out var command))
        {
            var isCover = _cards.TryGetValue(tracked.EntityId, out var card) && card.Kind == EntityKind.Cover;
            var expected = isCover
                ? (command.TurnOn ? "open" : "closed")
                : (command.TurnOn ? "on" : "off");
            if (string.Equals(tracked.State, expected, StringComparison.OrdinalIgnoreCase))
                _inflight.Remove(tracked.EntityId);
        }
    }

    bool IsTracked(string id) => id == _presenceId || _cards.ContainsKey(id) || _trackedInfo.Contains(id);

    IEnumerable<string> AllTracked()
    {
        var yielded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entity in _order)
        {
            if (yielded.Add(entity.EntityId))
                yield return entity.EntityId;
        }
        foreach (var entityId in _trackedInfo)
        {
            if (yielded.Add(entityId))
                yield return entityId;
        }
        if (!string.IsNullOrEmpty(_presenceId) && yielded.Add(_presenceId))
            yield return _presenceId;
    }

    PresenceKind MapPresence(string? state)
    {
        if (string.IsNullOrEmpty(state) || state is "unknown" or "unavailable")
            return PresenceKind.Unknown;
        if (string.Equals(state, _home, StringComparison.OrdinalIgnoreCase))
            return PresenceKind.Home;
        if (string.Equals(state, _away, StringComparison.OrdinalIgnoreCase))
            return PresenceKind.Away;
        return PresenceKind.Unknown;
    }

    bool TryParseState(JsonElement el, DateTime now, out Tracked tracked, out bool hadStamp)
    {
        tracked = new Tracked();
        hadStamp = false;
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty("entity_id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
            return false;
        var id = idEl.GetString() ?? "";
        if (!IsTracked(id))
            return false;
        var state = el.TryGetProperty("state", out var stateEl) && stateEl.ValueKind == JsonValueKind.String
            ? stateEl.GetString() ?? ""
            : "";
        hadStamp = TryReadStamp(el, "last_updated", out var stamp) || TryReadStamp(el, "last_changed", out stamp);
        if (!hadStamp)
            stamp = new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc));
        tracked.EntityId = id;
        tracked.State = state;
        tracked.Display = FormatDisplay(el, state);
        tracked.Stamp = stamp;
        ExtractAttributes(el, tracked.Attributes);
        return true;
    }

    static void ExtractAttributes(JsonElement el, Dictionary<string, string> dest)
    {
        if (!el.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Object)
            return;
        foreach (var key in ScalarAttributeKeys)
        {
            if (!attributes.TryGetProperty(key, out var val))
                continue;
            var text = val.ValueKind switch
            {
                JsonValueKind.String => val.GetString(),
                JsonValueKind.Number => val.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
            if (!string.IsNullOrEmpty(text))
                dest[key] = text;
        }
        foreach (var key in JsonObjectAttributeKeys)
        {
            if (attributes.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.Object)
                dest[key] = val.GetRawText();
        }
    }

    static bool TryReadStamp(JsonElement el, string name, out DateTimeOffset stamp)
    {
        stamp = default;
        if (!el.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return false;
        return DateTimeOffset.TryParse(
            value.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out stamp);
    }

    static string FormatDisplay(JsonElement el, string state)
    {
        if (string.Equals(state, "unavailable", StringComparison.OrdinalIgnoreCase))
            return "不可用";
        if (string.Equals(state, "unknown", StringComparison.OrdinalIgnoreCase))
            return "未知";
        if (el.TryGetProperty("attributes", out var attributes)
            && attributes.ValueKind == JsonValueKind.Object
            && attributes.TryGetProperty("unit_of_measurement", out var unit)
            && unit.ValueKind == JsonValueKind.String)
        {
            var text = unit.GetString();
            if (!string.IsNullOrEmpty(text))
                return state + " " + text;
        }
        return state;
    }

    void ClearPong(int id)
    {
        if (_pongId != 0 && id == _pongId)
            _pongId = 0;
    }

    static int ReadId(JsonElement root)
        => root.TryGetProperty("id", out var id) && id.TryGetInt32(out var value) ? value : 0;

    void Fault()
    {
        Phase = ConnPhase.Disconnected;
        ControlsEnabled = false;
        MarkCommandsUnconfirmed();
        _buffer.Clear();
        _pongId = 0;
        _subscribeId = 0;
        _statesId = 0;
        _phaseDeadline = null;
        _transportFault = true;
    }

    static string ServiceJson(int id, string domain, string service, object entityIds)
        => JsonSerializer.Serialize(new
        {
            id,
            type = "call_service",
            domain,
            service,
            target = new { entity_id = entityIds }
        });

    public PendingKind PendingOf(string entityId)
    {
        if (!_inflight.TryGetValue(entityId, out var command))
            return PendingKind.None;
        if (!command.Deadline.HasValue)
            return PendingKind.Unconfirmed;
        return command.TurnOn ? PendingKind.TurningOn : PendingKind.TurningOff;
    }

    public string? StateOf(string entityId)
        => _states.TryGetValue(entityId, out var state) ? state.State : null;

    public string? DisplayOf(string entityId)
        => _states.TryGetValue(entityId, out var state) ? state.Display : null;

    public string? AttributeOf(string entityId, string name)
        => _states.TryGetValue(entityId, out var state) && state.Attributes.TryGetValue(name, out var val) ? val : null;

    public IReadOnlyDictionary<string, CardView> SnapshotAll()
    {
        var map = new Dictionary<string, CardView>(StringComparer.Ordinal);
        foreach (var pair in _states)
        {
            _cards.TryGetValue(pair.Key, out var card);
            map[pair.Key] = new CardView(
                pair.Key,
                card?.Name ?? pair.Key,
                card?.Kind ?? EntityKind.Sensor,
                pair.Value.State,
                pair.Value.Display,
                PendingOf(pair.Key),
                true,
                card?.Group,
                card?.Icon,
                new Dictionary<string, string>(pair.Value.Attributes, StringComparer.Ordinal));
        }
        return map;
    }

    public IReadOnlyList<CardView> Cards()
    {
        var list = new List<CardView>(_order.Count);
        foreach (var card in _order)
        {
            _states.TryGetValue(card.EntityId, out var state);
            list.Add(new CardView(
                card.EntityId,
                card.Name,
                card.Kind,
                state?.State,
                state?.Display ?? "等待状态",
                PendingOf(card.EntityId),
                state != null,
                card.Group,
                card.Icon,
                state != null ? new Dictionary<string, string>(state.Attributes, StringComparer.Ordinal) : null));
        }
        return list;
    }
}
