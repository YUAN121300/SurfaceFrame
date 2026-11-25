using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SurfaceFrame;

public enum EntityKind
{
    Light,
    Switch,
    Sensor,
    Cover
}

public sealed record EntityConfig(
    string EntityId,
    string Name,
    EntityKind Kind,
    string? Group = null,
    string? Icon = null,
    IReadOnlyList<string>? EntityIds = null);

public sealed record AlbumConfig(string Folder, bool Crop, int IntervalSeconds, string? Pin = null);

public sealed record SceneConfig(
    string Id,
    string Name,
    string Subtitle,
    string Icon,
    string Mode,
    string Domain,
    IReadOnlyList<string> EntityIds,
    bool WarmAccent = false);

public sealed record RoomSensorConfig(string Name, string? TempEntity, string? HumidityEntity);

public sealed record VehicleDashboardConfig(
    string Title,
    string? BatteryEntity,
    string? RangeEntity,
    string? StateEntity,
    string? PluggedEntity,
    string? InsideTempEntity,
    string? OutsideTempEntity,
    string? DoorsEntity,
    string? SentryEntity);

public sealed record ClimateDashboardConfig(
    string Title,
    string? HeaterEntity,
    string? ObserverEntity,
    string? SupplyTempEntity,
    string? ReturnTempEntity,
    string? TargetTempEntity,
    string? Ac1Name,
    string? Ac1Entity,
    string? Ac2Name,
    string? Ac2Entity);

public sealed record EnergyDashboardConfig(
    string Title,
    string? PowerEntity,
    string? ElectricityEntity,
    string? GasEntity,
    string CurrencySymbol = "¥");

public sealed record DeviceStatusConfig(
    string Name,
    string EntityId,
    string Kind = "sensor",
    string? ExtraEntityId = null,
    string? ExtraLabel = null);

public sealed record DashboardConfig(
    string? WeatherEntity,
    IReadOnlyList<RoomSensorConfig> Rooms,
    VehicleDashboardConfig? Vehicle,
    ClimateDashboardConfig? Climate,
    EnergyDashboardConfig? Energy,
    IReadOnlyList<DeviceStatusConfig> Devices);

public sealed record HaSettings(
    string Url,
    string TokenEnv,
    IReadOnlyList<EntityConfig> Entities,
    string PresenceEntityId,
    string PresenceHome,
    string PresenceAway,
    IReadOnlyList<EntityConfig>? InfoEntities = null,
    IReadOnlyList<SceneConfig>? Scenes = null,
    DashboardConfig? Dashboard = null,
    string? RemoteLockEntityId = null,
    string? RemoteAlbumEntityId = null)
{
    public IReadOnlyList<EntityConfig> AllTrackedInfoEntities()
    {
        var list = new List<EntityConfig>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? id)
        {
            if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
                list.Add(new EntityConfig(id, id, EntityKind.Sensor));
        }
        Add(RemoteLockEntityId);
        Add(RemoteAlbumEntityId);
        if (InfoEntities != null)
        {
            foreach (var e in InfoEntities)
                if (!string.IsNullOrWhiteSpace(e.EntityId) && seen.Add(e.EntityId))
                    list.Add(e);
        }
        if (Scenes != null)
        {
            foreach (var s in Scenes)
                foreach (var id in s.EntityIds)
                    Add(id);
        }
        if (Dashboard != null)
        {
            Add(Dashboard.WeatherEntity);
            foreach (var r in Dashboard.Rooms)
            {
                Add(r.TempEntity);
                Add(r.HumidityEntity);
            }
            if (Dashboard.Vehicle is { } v)
            {
                Add(v.BatteryEntity); Add(v.RangeEntity); Add(v.StateEntity); Add(v.PluggedEntity);
                Add(v.InsideTempEntity); Add(v.OutsideTempEntity); Add(v.DoorsEntity); Add(v.SentryEntity);
            }
            if (Dashboard.Climate is { } c)
            {
                Add(c.HeaterEntity); Add(c.ObserverEntity); Add(c.SupplyTempEntity); Add(c.ReturnTempEntity);
                Add(c.TargetTempEntity); Add(c.Ac1Entity); Add(c.Ac2Entity);
            }
            if (Dashboard.Energy is { } e)
            {
                Add(e.PowerEntity); Add(e.ElectricityEntity); Add(e.GasEntity);
            }
            foreach (var d in Dashboard.Devices)
            {
                Add(d.EntityId);
                Add(d.ExtraEntityId);
            }
        }
        return list;
    }
}

public sealed record ImmichSettings(
    bool Enabled,
    string BaseUrl,
    string ApiKeyEnv,
    string AlbumId,
    string CacheFolder,
    int MaxPhotos = 260);

public sealed record DemoSettings(bool Enabled);

public sealed class LoadedConfig
{
    public required AlbumConfig Album { get; init; }
    public required HaSettings HomeAssistant { get; init; }
    public required ImmichSettings Immich { get; init; }
    public required DemoSettings Demo { get; init; }
    public string? HaToken { get; init; }
    public string? ImmichApiKey { get; init; }
    public string ConfigDirectory { get; init; } = "";
    public string? PublicPath { get; init; }
    public string? Error { get; init; }

    public string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ConfigDirectory;
        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(ConfigDirectory, path));
    }
}

public static class ConfigLoader
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    static readonly JsonSerializerOptions SaveJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static LoadedConfig Load(string? publicPath, string? privatePath, Func<string, string?>? env)
    {
        var fullPublic = string.IsNullOrWhiteSpace(publicPath) ? null : Path.GetFullPath(publicPath);
        var directory = fullPublic == null
            ? AppContext.BaseDirectory
            : Path.GetDirectoryName(fullPublic) ?? AppContext.BaseDirectory;

        AppConfigFile file;
        if (string.IsNullOrWhiteSpace(publicPath) || !File.Exists(publicPath))
        {
            if (!string.IsNullOrWhiteSpace(publicPath))
                return WithError(DemoDefaults(directory), "找不到配置文件", fullPublic);
            file = DefaultFile();
        }
        else
        {
            try
            {
                file = JsonSerializer.Deserialize<AppConfigFile>(File.ReadAllText(publicPath), JsonOptions)
                    ?? throw new JsonException("Missing configuration object.");
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return WithError(DemoDefaults(directory), "配置无法读取", fullPublic);
            }
        }

        string? token = null;
        string? immichKey = null;
        if (!string.IsNullOrWhiteSpace(privatePath) && File.Exists(privatePath))
        {
            try
            {
                var secret = JsonSerializer.Deserialize<PrivateFile>(File.ReadAllText(privatePath), JsonOptions);
                token = Clean(secret?.HomeAssistantToken);
                immichKey = Clean(secret?.ImmichApiKey);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return WithError(DemoDefaults(directory), "无法读取私密配置", fullPublic);
            }
        }

        return Build(file, directory, fullPublic, token, immichKey, env);
    }

    public static LoadedConfig DemoDefaults(string directory)
        => Build(DefaultFile(), directory, null, null, null, null);

    public static bool SaveImmichSelection(string? publicPath, string? albumId)
    {
        if (string.IsNullOrWhiteSpace(publicPath) || !File.Exists(publicPath))
            return false;
        try
        {
            var raw = File.ReadAllText(publicPath);
            var root = JsonNode.Parse(
                raw,
                new JsonNodeOptions { PropertyNameCaseInsensitive = true },
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
            if (root == null)
                return false;

            var immichKey = root.FirstOrDefault(p => string.Equals(p.Key, "immich", StringComparison.OrdinalIgnoreCase)).Key ?? "immich";
            var immichNode = root[immichKey] as JsonObject ?? new JsonObject();
            root[immichKey] = immichNode;

            var enabled = !string.IsNullOrWhiteSpace(albumId);
            var enabledKey = immichNode.FirstOrDefault(p => string.Equals(p.Key, "enabled", StringComparison.OrdinalIgnoreCase)).Key ?? "enabled";
            immichNode[enabledKey] = enabled;

            if (enabled)
            {
                var albumKey = immichNode.FirstOrDefault(p => string.Equals(p.Key, "albumId", StringComparison.OrdinalIgnoreCase)).Key ?? "albumId";
                immichNode[albumKey] = albumId!.Trim();
            }

            var updated = root.ToJsonString(SaveJsonOptions) + "\n";
            ImmichSync.AtomicSave(publicPath, Encoding.UTF8.GetBytes(updated));
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static LoadedConfig Build(AppConfigFile file, string directory, string? publicPath, string? token, string? immichKey, Func<string, string?>? env)
    {
        var ha = file.HomeAssistant ?? new HaFile();
        var immich = file.Immich ?? new ImmichFile();
        var album = file.Album ?? new AlbumFile();
        var tokenEnv = string.IsNullOrWhiteSpace(ha.TokenEnv) ? "HA_TOKEN" : ha.TokenEnv;
        var apiKeyEnv = string.IsNullOrWhiteSpace(immich.ApiKeyEnv) ? "IMMICH_API_KEY" : immich.ApiKeyEnv;
        token ??= Clean(env?.Invoke(tokenEnv));
        immichKey ??= Clean(env?.Invoke(apiKeyEnv));
        var haUrl = string.IsNullOrWhiteSpace(ha.Url) ? "ws://ha.example:8123/api/websocket" : ha.Url;
        var immichUrl = string.IsNullOrWhiteSpace(immich.BaseUrl) ? "https://immich.example" : immich.BaseUrl;
        var albumId = Guid.TryParse(immich.AlbumId, out var id) ? id : Guid.Empty;
        var demoEnabled = file.Demo?.Enabled ?? false;
        var immichEnabled = immich.Enabled == true && !demoEnabled;
        var validHa = Uri.TryCreate(haUrl, UriKind.Absolute, out var haUri)
            && haUri.Scheme is "ws" or "wss" && !string.IsNullOrWhiteSpace(haUri.Host) && haUri.UserInfo.Length == 0;
        var validImmich = Uri.TryCreate(immichUrl, UriKind.Absolute, out var immichUri)
            && immichUri.Scheme is "http" or "https" && !string.IsNullOrWhiteSpace(immichUri.Host)
            && immichUri.UserInfo.Length == 0 && immichUri.Query.Length == 0 && immichUri.Fragment.Length == 0
            && albumId != Guid.Empty;

        var entities = ParseEntities(ha.Entities);
        var infoEntities = ParseEntities(ha.InfoEntities);
        var scenes = ParseScenes(ha.Scenes);
        var dashboard = ParseDashboard(ha.Dashboard);

        var interval = album.IntervalSeconds ?? 30;
        if (interval < 5)
            interval = 30;

        var maxPhotos = Math.Clamp(immich.MaxPhotos ?? 260, 10, 2000);

        return new LoadedConfig
        {
            ConfigDirectory = directory,
            PublicPath = publicPath,
            HaToken = validHa && !demoEnabled ? token : null,
            ImmichApiKey = !demoEnabled ? immichKey : null,
            Album = new AlbumConfig(
                string.IsNullOrWhiteSpace(album.Folder) ? "Photos" : album.Folder!,
                album.Crop ?? false,
                interval,
                string.IsNullOrWhiteSpace(album.Pin) ? null : album.Pin.Trim()),
            HomeAssistant = new HaSettings(
                haUrl,
                tokenEnv,
                entities,
                string.IsNullOrWhiteSpace(ha.PresenceEntityId) ? "person.demo" : ha.PresenceEntityId!,
                string.IsNullOrWhiteSpace(ha.PresenceHome) ? "home" : ha.PresenceHome!,
                string.IsNullOrWhiteSpace(ha.PresenceAway) ? "not_home" : ha.PresenceAway!,
                infoEntities,
                scenes,
                dashboard,
                CleanId(ha.RemoteLockEntityId),
                CleanId(ha.RemoteAlbumEntityId)),
            Immich = new ImmichSettings(
                immichEnabled && validImmich,
                immichUrl,
                apiKeyEnv,
                albumId.ToString("D"),
                string.IsNullOrWhiteSpace(immich.CacheFolder) ? "Cache" : immich.CacheFolder!,
                maxPhotos),
            Demo = new DemoSettings(demoEnabled),
            Error = !demoEnabled && !validHa ? "HA WebSocket 地址无效"
                : immichEnabled && !validImmich ? "Immich 地址或相册 UUID 无效" : null
        };
    }

    static List<EntityConfig> ParseEntities(List<EntityFile>? raw)
    {
        var list = new List<EntityConfig>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entity in raw ?? new List<EntityFile>())
        {
            if (entity == null || string.IsNullOrWhiteSpace(entity.EntityId) || !seen.Add(entity.EntityId))
                continue;
            list.Add(new EntityConfig(
                entity.EntityId,
                string.IsNullOrWhiteSpace(entity.Name) ? entity.EntityId : entity.Name!,
                ParseKind(entity.Kind),
                string.IsNullOrWhiteSpace(entity.Group) ? null : entity.Group!.Trim(),
                string.IsNullOrWhiteSpace(entity.Icon) ? null : entity.Icon!.Trim(),
                entity.EntityIds?
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()));
        }
        return list;
    }

    static List<SceneConfig> ParseScenes(List<SceneFile>? raw)
    {
        var list = new List<SceneConfig>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sc in raw ?? new List<SceneFile>())
        {
            if (sc == null || string.IsNullOrWhiteSpace(sc.Id) || !seen.Add(sc.Id.Trim()))
                continue;
            var ids = (sc.EntityIds ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (ids.Length == 0)
                continue;
            list.Add(new SceneConfig(
                sc.Id.Trim(),
                string.IsNullOrWhiteSpace(sc.Name) ? sc.Id.Trim() : sc.Name!.Trim(),
                string.IsNullOrWhiteSpace(sc.Subtitle) ? "点击执行" : sc.Subtitle!.Trim(),
                string.IsNullOrWhiteSpace(sc.Icon) ? "scene" : sc.Icon!.Trim().ToLowerInvariant(),
                string.IsNullOrWhiteSpace(sc.Mode) ? "toggle" : sc.Mode!.Trim().ToLowerInvariant(),
                string.IsNullOrWhiteSpace(sc.Domain) ? "light" : sc.Domain!.Trim().ToLowerInvariant(),
                ids,
                sc.WarmAccent ?? false));
        }
        return list;
    }

    static DashboardConfig? ParseDashboard(DashboardFile? raw)
    {
        if (raw == null)
            return null;
        var rooms = (raw.Rooms ?? new List<RoomSensorFile>())
            .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Name))
            .Select(r => new RoomSensorConfig(r.Name!.Trim(), CleanId(r.TempEntity), CleanId(r.HumidityEntity)))
            .Take(4)
            .ToList();
        var vehicle = raw.Vehicle == null ? null : new VehicleDashboardConfig(
            string.IsNullOrWhiteSpace(raw.Vehicle.Title) ? "电动车状态" : raw.Vehicle.Title!.Trim(),
            CleanId(raw.Vehicle.BatteryEntity), CleanId(raw.Vehicle.RangeEntity),
            CleanId(raw.Vehicle.StateEntity), CleanId(raw.Vehicle.PluggedEntity),
            CleanId(raw.Vehicle.InsideTempEntity), CleanId(raw.Vehicle.OutsideTempEntity),
            CleanId(raw.Vehicle.DoorsEntity), CleanId(raw.Vehicle.SentryEntity));
        var climate = raw.Climate == null ? null : new ClimateDashboardConfig(
            string.IsNullOrWhiteSpace(raw.Climate.Title) ? "暖通与空调" : raw.Climate.Title!.Trim(),
            CleanId(raw.Climate.HeaterEntity), CleanId(raw.Climate.ObserverEntity),
            CleanId(raw.Climate.SupplyTempEntity), CleanId(raw.Climate.ReturnTempEntity),
            CleanId(raw.Climate.TargetTempEntity),
            CleanId(raw.Climate.Ac1Name), CleanId(raw.Climate.Ac1Entity),
            CleanId(raw.Climate.Ac2Name), CleanId(raw.Climate.Ac2Entity));
        var energy = raw.Energy == null ? null : new EnergyDashboardConfig(
            string.IsNullOrWhiteSpace(raw.Energy.Title) ? "本月用能走势与费用" : raw.Energy.Title!.Trim(),
            CleanId(raw.Energy.PowerEntity), CleanId(raw.Energy.ElectricityEntity),
            CleanId(raw.Energy.GasEntity),
            string.IsNullOrWhiteSpace(raw.Energy.CurrencySymbol) ? "¥" : raw.Energy.CurrencySymbol!.Trim());
        var devices = (raw.Devices ?? new List<DeviceStatusFile>())
            .Where(d => d != null && !string.IsNullOrWhiteSpace(d.Name) && !string.IsNullOrWhiteSpace(d.EntityId))
            .Select(d => new DeviceStatusConfig(
                d.Name!.Trim(),
                d.EntityId!.Trim(),
                string.IsNullOrWhiteSpace(d.Kind) ? "sensor" : d.Kind!.Trim().ToLowerInvariant(),
                CleanId(d.ExtraEntityId),
                CleanId(d.ExtraLabel)))
            .Take(6)
            .ToList();
        return new DashboardConfig(CleanId(raw.WeatherEntity), rooms, vehicle, climate, energy, devices);
    }

    static string? CleanId(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    static LoadedConfig WithError(LoadedConfig config, string error, string? publicPath)
        => new()
        {
            Album = config.Album,
            HomeAssistant = config.HomeAssistant,
            Immich = config.Immich,
            Demo = new DemoSettings(false),
            ConfigDirectory = config.ConfigDirectory,
            PublicPath = publicPath,
            Error = error
        };

    static AppConfigFile DefaultFile() => new()
    {
        Album = new AlbumFile { Folder = "Photos", Crop = false, IntervalSeconds = 30 },
        HomeAssistant = new HaFile
        {
            Url = "ws://ha.example:8123/api/websocket",
            TokenEnv = "HA_TOKEN",
            PresenceEntityId = "person.demo",
            PresenceHome = "home",
            PresenceAway = "not_home",
            Entities = new List<EntityFile>
            {
                new() { EntityId = "light.demo_ceiling", Name = "客厅主灯", Kind = "light" },
                new() { EntityId = "switch.demo_plug", Name = "插座", Kind = "switch" },
                new() { EntityId = "sensor.demo_temp", Name = "温度", Kind = "sensor" }
            }
        },
        Immich = new ImmichFile
        {
            Enabled = false,
            BaseUrl = "https://immich.example",
            ApiKeyEnv = "IMMICH_API_KEY",
            AlbumId = "00000000-0000-0000-0000-000000000000",
            CacheFolder = "Cache",
            MaxPhotos = 260
        },
        Demo = new DemoFile { Enabled = true }
    };

    static EntityKind ParseKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "light" => EntityKind.Light,
        "switch" => EntityKind.Switch,
        "cover" => EntityKind.Cover,
        _ => EntityKind.Sensor
    };

    public static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return trimmed is "replace-me" or "changeme" or "YOUR_TOKEN" or "your-token" ? null : trimmed;
    }

    sealed class AppConfigFile
    {
        public AlbumFile? Album { get; set; }
        public HaFile? HomeAssistant { get; set; }
        public ImmichFile? Immich { get; set; }
        public DemoFile? Demo { get; set; }
    }

    sealed class AlbumFile
    {
        public string? Folder { get; set; }
        public bool? Crop { get; set; }
        public int? IntervalSeconds { get; set; }
        public string? Pin { get; set; }
    }

    sealed class HaFile
    {
        public string? Url { get; set; }
        public string? TokenEnv { get; set; }
        public List<EntityFile>? Entities { get; set; }
        public List<EntityFile>? InfoEntities { get; set; }
        public List<SceneFile>? Scenes { get; set; }
        public DashboardFile? Dashboard { get; set; }
        public string? PresenceEntityId { get; set; }
        public string? PresenceHome { get; set; }
        public string? PresenceAway { get; set; }
        public string? RemoteLockEntityId { get; set; }
        public string? RemoteAlbumEntityId { get; set; }
    }

    sealed class SceneFile
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Subtitle { get; set; }
        public string? Icon { get; set; }
        public string? Mode { get; set; }
        public string? Domain { get; set; }
        public List<string>? EntityIds { get; set; }
        public bool? WarmAccent { get; set; }
    }

    sealed class DashboardFile
    {
        public string? WeatherEntity { get; set; }
        public List<RoomSensorFile>? Rooms { get; set; }
        public VehicleDashboardFile? Vehicle { get; set; }
        public ClimateDashboardFile? Climate { get; set; }
        public EnergyDashboardFile? Energy { get; set; }
        public List<DeviceStatusFile>? Devices { get; set; }
    }

    sealed class RoomSensorFile
    {
        public string? Name { get; set; }
        public string? TempEntity { get; set; }
        public string? HumidityEntity { get; set; }
    }

    sealed class VehicleDashboardFile
    {
        public string? Title { get; set; }
        public string? BatteryEntity { get; set; }
        public string? RangeEntity { get; set; }
        public string? StateEntity { get; set; }
        public string? PluggedEntity { get; set; }
        public string? InsideTempEntity { get; set; }
        public string? OutsideTempEntity { get; set; }
        public string? DoorsEntity { get; set; }
        public string? SentryEntity { get; set; }
    }

    sealed class ClimateDashboardFile
    {
        public string? Title { get; set; }
        public string? HeaterEntity { get; set; }
        public string? ObserverEntity { get; set; }
        public string? SupplyTempEntity { get; set; }
        public string? ReturnTempEntity { get; set; }
        public string? TargetTempEntity { get; set; }
        public string? Ac1Name { get; set; }
        public string? Ac1Entity { get; set; }
        public string? Ac2Name { get; set; }
        public string? Ac2Entity { get; set; }
    }

    sealed class EnergyDashboardFile
    {
        public string? Title { get; set; }
        public string? PowerEntity { get; set; }
        public string? ElectricityEntity { get; set; }
        public string? GasEntity { get; set; }
        public string? CurrencySymbol { get; set; }
    }

    sealed class DeviceStatusFile
    {
        public string? Name { get; set; }
        public string? EntityId { get; set; }
        public string? Kind { get; set; }
        public string? ExtraEntityId { get; set; }
        public string? ExtraLabel { get; set; }
    }

    sealed class EntityFile
    {
        public string? EntityId { get; set; }
        public string? Name { get; set; }
        public string? Kind { get; set; }
        public string? Group { get; set; }
        public string? Icon { get; set; }
        public List<string>? EntityIds { get; set; }
    }

    sealed class ImmichFile
    {
        public bool? Enabled { get; set; }
        public string? BaseUrl { get; set; }
        public string? ApiKeyEnv { get; set; }
        public string? AlbumId { get; set; }
        public string? CacheFolder { get; set; }
        public int? MaxPhotos { get; set; }
    }

    sealed class DemoFile
    {
        public bool? Enabled { get; set; }
    }

    sealed class PrivateFile
    {
        public string? HomeAssistantToken { get; set; }
        public string? ImmichApiKey { get; set; }
    }
}
