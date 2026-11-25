using System.Text;
using System.Text.Json;

namespace SurfaceFrame;

public enum ConfirmPace
{
    Quick,
    Slow
}

public sealed class DemoBroker
{
    readonly Dictionary<string, string> _states = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _units = new(StringComparer.Ordinal);
    readonly List<Scheduled> _scheduled = new();
    readonly string _presenceId;

    public bool Connected { get; private set; } = true;
    public ConfirmPace Pace { get; set; } = ConfirmPace.Quick;

    sealed class Scheduled
    {
        public DateTime At;
        public string EntityId = "";
        public string State = "";
        public string Json = "";
    }

    public DemoBroker(IReadOnlyList<EntityConfig> entities, string presenceId, string presenceState)
    {
        foreach (var entity in entities)
        {
            _states[entity.EntityId] = entity.Kind == EntityKind.Sensor ? "22.5" : "off";
            if (entity.Kind == EntityKind.Sensor)
                _units[entity.EntityId] = "°C";
        }
        _presenceId = presenceId;
        _states[presenceId] = presenceState;
    }

    public void Disconnect()
    {
        Connected = false;
        _scheduled.Clear();
    }

    public void Connect() => Connected = true;

    public IReadOnlyList<string> Start(DateTime now)
    {
        Connected = true;
        return new[] { "{\"type\":\"auth_required\",\"ha_version\":\"demo\"}" };
    }

    public IReadOnlyList<string> SetPresence(string state, DateTime now)
    {
        _states[_presenceId] = state;
        if (!Connected)
            return Array.Empty<string>();
        return new[] { EventJson(_presenceId, state, now) };
    }

    public IReadOnlyList<string> Poll(DateTime now)
    {
        if (!Connected || _scheduled.Count == 0)
            return Array.Empty<string>();
        var due = _scheduled.Where(item => item.At <= now).OrderBy(item => item.At).ToList();
        if (due.Count == 0)
            return Array.Empty<string>();
        _scheduled.RemoveAll(item => item.At <= now);
        foreach (var item in due)
            _states[item.EntityId] = item.State;
        return due.ConvertAll(item => item.Json);
    }

    public IReadOnlyList<string> Accept(string outgoingJson, DateTime now)
    {
        if (!Connected)
            return Array.Empty<string>();
        using var doc = JsonDocument.Parse(outgoingJson);
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString();
        if (type == "auth")
            return new[] { "{\"type\":\"auth_ok\",\"ha_version\":\"demo\"}" };
        if (type == "subscribe_events")
            return new[] { ResultJson(root.GetProperty("id").GetInt32()) };
        if (type == "get_states")
            return new[] { StatesJson(root.GetProperty("id").GetInt32(), now) };
        if (type == "ping")
            return new[] { "{\"id\":" + root.GetProperty("id").GetInt32().ToString() + ",\"type\":\"pong\"}" };
        if (type == "call_service")
        {
            var id = root.GetProperty("id").GetInt32();
            var service = root.GetProperty("service").GetString() ?? "";
            var entity = root.GetProperty("target").GetProperty("entity_id").GetString() ?? "";
            var newState = service == "turn_on" ? "on" : "off";
            var at = now + (Pace == ConfirmPace.Quick ? FrameTiming.QuickConfirm : FrameTiming.SlowConfirm);
            _scheduled.Add(new Scheduled
            {
                At = at,
                EntityId = entity,
                State = newState,
                Json = EventJson(entity, newState, at)
            });
            return new[] { ResultJson(id) };
        }
        return Array.Empty<string>();
    }

    static string ResultJson(int id)
        => "{\"id\":" + id.ToString() + ",\"type\":\"result\",\"success\":true,\"result\":null}";

    string StatesJson(int id, DateTime now)
    {
        var stamp = Stamp(now);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteString("type", "result");
            writer.WriteBoolean("success", true);
            writer.WritePropertyName("result");
            writer.WriteStartArray();
            foreach (var pair in _states)
            {
                writer.WriteStartObject();
                writer.WriteString("entity_id", pair.Key);
                writer.WriteString("state", pair.Value);
                writer.WriteString("last_changed", stamp);
                WriteAttributes(writer, pair.Key);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    string EventJson(string entityId, string state, DateTime when)
    {
        var stamp = Stamp(when);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "event");
            writer.WritePropertyName("event");
            writer.WriteStartObject();
            writer.WriteString("event_type", "state_changed");
            writer.WriteString("time_fired", stamp);
            writer.WritePropertyName("data");
            writer.WriteStartObject();
            writer.WriteString("entity_id", entityId);
            writer.WritePropertyName("new_state");
            writer.WriteStartObject();
            writer.WriteString("entity_id", entityId);
            writer.WriteString("state", state);
            writer.WriteString("last_changed", stamp);
            WriteAttributes(writer, entityId);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    void WriteAttributes(Utf8JsonWriter writer, string entityId)
    {
        writer.WritePropertyName("attributes");
        writer.WriteStartObject();
        if (_units.TryGetValue(entityId, out var unit))
            writer.WriteString("unit_of_measurement", unit);
        writer.WriteEndObject();
    }

    static string Stamp(DateTime when)
        => new DateTimeOffset(DateTime.SpecifyKind(when, DateTimeKind.Utc)).ToString("O");
}
