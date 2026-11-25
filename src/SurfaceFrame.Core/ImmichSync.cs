using System.Text;
using System.Text.Json;

namespace SurfaceFrame;

public sealed record ImmichAsset(string Id, string? LivePhotoVideoId = null, PhotoMetadata? Metadata = null);

public sealed record ImmichAlbumInfo(string Id, string Name, int AssetCount);

public enum ImmichStatus
{
    Updated,
    KeptCache,
    FellBackToLocal,
    Empty
}

public sealed record ImmichResult(ImmichStatus Status, IReadOnlyList<string> Files);

public static class ImmichSync
{
    public static HttpRequestMessage BuildAlbumsListRequest(string baseUrl, string apiKey)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            Trim(baseUrl) + "/api/albums");
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        return request;
    }

    public static HttpRequestMessage BuildAlbumRequest(string baseUrl, string albumId, string apiKey)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            Trim(baseUrl) + "/api/albums/" + Uri.EscapeDataString(albumId));
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        return request;
    }

    public static HttpRequestMessage BuildSearchRequest(string baseUrl, string albumId, string apiKey, int page, int size = 250)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            Trim(baseUrl) + "/api/search/metadata");
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        var payload = JsonSerializer.Serialize(new
        {
            albumIds = new[] { albumId },
            type = "IMAGE",
            size,
            page,
            withExif = true
        });
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        return request;
    }

    public static HttpRequestMessage BuildRandomSearchRequest(string baseUrl, string albumId, string apiKey, int size)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Trim(baseUrl) + "/api/search/random");
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        var payload = JsonSerializer.Serialize(new { albumIds = new[] { albumId }, type = "IMAGE", size, withExif = true });
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        return request;
    }

    public static IReadOnlyList<ImmichAsset> ParseRandomAssets(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("Random assets array expected.");
        return ParseAssets(doc.RootElement);
    }

    static async Task<IReadOnlyList<ImmichAsset>> FetchRandomAlbumAssetsAsync(
        HttpClient http, string baseUrl, string albumId, string apiKey, int maxAssets, CancellationToken ct)
    {
        using (var req = BuildRandomSearchRequest(baseUrl, albumId, apiKey, Math.Max(1, maxAssets)))
        using (var resp = await http.SendAsync(req, ct).ConfigureAwait(false))
        {
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var items = ParseRandomAssets(body);
                if (items.Count > 0)
                    return items;
                using var check = BuildAlbumRequest(baseUrl, albumId, apiKey);
                using var checkResp = await http.SendAsync(check, ct).ConfigureAwait(false);
                if (!checkResp.IsSuccessStatusCode)
                    throw new IOException("album");
                return Array.Empty<ImmichAsset>();
            }
            if (resp.StatusCode is not (System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed))
                throw new IOException("random search");
        }
        using var albumReq = BuildAlbumRequest(baseUrl, albumId, apiKey);
        using var albumResp = await http.SendAsync(albumReq, ct).ConfigureAwait(false);
        if (!albumResp.IsSuccessStatusCode)
            throw new IOException("album");
        var albumBody = await albumResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var fallback = await FetchAlbumAssetsAsync(http, baseUrl, albumId, apiKey, albumBody, int.MaxValue, ct).ConfigureAwait(false);
        return SlideDeck.Shuffle(fallback).Take(maxAssets).ToList();
    }

    public static HttpRequestMessage BuildThumbRequest(string baseUrl, string assetId, string apiKey)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            Trim(baseUrl) + "/api/assets/" + Uri.EscapeDataString(assetId) + "/thumbnail?size=preview");
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        return request;
    }

    public static HttpRequestMessage BuildVideoRequest(string baseUrl, string videoId, string apiKey)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            Trim(baseUrl) + "/api/assets/" + Uri.EscapeDataString(videoId) + "/video/playback");
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        return request;
    }

    public static IReadOnlyList<ImmichAlbumInfo> ParseAlbumsList(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("Albums array expected.");
        var list = new List<ImmichAlbumInfo>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            var id = ReadId(item);
            if (id == null || !SafeId(id))
                continue;
            var count = item.TryGetProperty("assetCount", out var cEl) && cEl.TryGetInt32(out var c) ? c : 0;
            if (count <= 0)
                continue;
            var name = item.TryGetProperty("albumName", out var nEl) && nEl.ValueKind == JsonValueKind.String
                ? nEl.GetString() ?? id
                : id;
            list.Add(new ImmichAlbumInfo(id, name, count));
        }
        return list.OrderBy(a => a.AssetCount).ToList();
    }

    public static async Task<IReadOnlyList<ImmichAlbumInfo>> FetchAlbumsAsync(
        HttpClient http,
        string baseUrl,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var req = BuildAlbumsListRequest(baseUrl, apiKey);
        using var resp = await http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new IOException("albums list");
        var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseAlbumsList(body);
    }

    public static IReadOnlyList<ImmichAsset> ParseAlbum(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new JsonException("Album assets are missing.");
        return ParseAssets(assets);
    }

    public static IReadOnlyList<ImmichAsset> ParseSearchPage(string json, out int? nextPage)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("assets", out var assetsObj) || assetsObj.ValueKind != JsonValueKind.Object
            || !assetsObj.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new JsonException("Search assets are missing.");

        nextPage = null;
        if (assetsObj.TryGetProperty("nextPage", out var np))
        {
            if (np.ValueKind == JsonValueKind.String && int.TryParse(np.GetString(), out var strPage) && strPage > 0)
                nextPage = strPage;
            else if (np.ValueKind == JsonValueKind.Number && np.TryGetInt32(out var numPage) && numPage > 0)
                nextPage = numPage;
        }
        return ParseAssets(items);
    }

    static bool HasAssetsArray(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("assets", out var assets)
            && assets.ValueKind == JsonValueKind.Array;
    }

    static bool IsV3AlbumMetadata(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return root.ValueKind == JsonValueKind.Object
            && !root.TryGetProperty("assets", out _)
            && root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            && root.TryGetProperty("assetCount", out var countEl) && countEl.ValueKind == JsonValueKind.Number;
    }

    static IReadOnlyList<ImmichAsset> ParseAssets(JsonElement assets)
    {
        var list = new List<ImmichAsset>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in assets.EnumerateArray())
        {
            if (!IsImage(item))
                continue;
            var id = ReadId(item);
            if (id == null || !SafeId(id) || !seen.Add(id))
                continue;
            string? liveVid = null;
            if (item.TryGetProperty("livePhotoVideoId", out var liveEl) && liveEl.ValueKind == JsonValueKind.String)
            {
                var candidate = liveEl.GetString();
                if (candidate != null && SafeId(candidate))
                    liveVid = candidate;
            }
            var meta = PhotoMetaFormatter.FromAssetJson(item);
            list.Add(new ImmichAsset(id, liveVid, meta));
        }
        return list;
    }

    public static void AtomicSave(string finalPath, byte[] data)
    {
        var directory = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var partial = finalPath + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            File.WriteAllBytes(partial, data);
            File.Move(partial, finalPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(partial); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public static void SaveLiveMap(string cacheDir, IReadOnlyDictionary<string, string> map)
    {
        var path = Path.Combine(cacheDir, "live-map.json");
        var json = JsonSerializer.Serialize(map);
        AtomicSave(path, Encoding.UTF8.GetBytes(json));
    }

    public static Dictionary<string, string> LoadLiveMap(string cacheDir)
    {
        var path = Path.Combine(cacheDir, "live-map.json");
        if (!File.Exists(path))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var raw = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(raw);
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (parsed != null)
            {
                foreach (var kv in parsed)
                {
                    if (SafeId(kv.Key) && SafeId(kv.Value))
                        result[kv.Key] = kv.Value;
                }
            }
            return result;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static void SaveMetaMap(string cacheDir, IReadOnlyDictionary<string, PhotoMetadata> map)
    {
        var path = Path.Combine(cacheDir, "meta-map.json");
        var json = JsonSerializer.Serialize(map);
        AtomicSave(path, Encoding.UTF8.GetBytes(json));
    }

    public static Dictionary<string, PhotoMetadata> LoadMetaMap(string cacheDir)
    {
        var path = Path.Combine(cacheDir, "meta-map.json");
        if (!File.Exists(path))
            return new Dictionary<string, PhotoMetadata>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var raw = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, PhotoMetadata>>(raw);
            var result = new Dictionary<string, PhotoMetadata>(StringComparer.OrdinalIgnoreCase);
            if (parsed != null)
            {
                foreach (var kv in parsed)
                {
                    if (SafeId(kv.Key) && kv.Value != null && !kv.Value.IsEmpty)
                        result[kv.Key] = kv.Value;
                }
            }
            return result;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new Dictionary<string, PhotoMetadata>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static IReadOnlyList<string> ListCache(string cacheDir)
        => PhotoDeck.List(cacheDir).Where(IsCachedPreview).ToList();

    static bool IsCachedPreview(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> header = stackalloc byte[8];
            return file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length
                && HasImageHeader(header);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static bool IsCachedVideo(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            using var file = File.OpenRead(path);
            Span<byte> header = stackalloc byte[12];
            return file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length
                && HasVideoHeader(header);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static ReadOnlySpan<byte> PngHeader => new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };

    static bool HasImageHeader(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 8 && (
            (bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff)
            || bytes[..8].SequenceEqual(PngHeader));

    static bool HasVideoHeader(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 12 && bytes[4] == (byte)'f' && bytes[5] == (byte)'t' && bytes[6] == (byte)'y' && bytes[7] == (byte)'p';

    static async Task<IReadOnlyList<ImmichAsset>> FetchAlbumAssetsAsync(
        HttpClient http,
        string baseUrl,
        string albumId,
        string apiKey,
        string albumBody,
        int maxAssets,
        CancellationToken cancellationToken)
    {
        if (HasAssetsArray(albumBody))
        {
            var legacy = ParseAlbum(albumBody);
            return legacy.Count > maxAssets ? legacy.Take(maxAssets).ToList() : legacy;
        }
        if (!IsV3AlbumMetadata(albumBody))
            return ParseAlbum(albumBody);

        var all = new List<ImmichAsset>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int? page = 1;
        for (var step = 0; step < 100 && page is int currentPage && all.Count < maxAssets; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var searchRequest = BuildSearchRequest(baseUrl, albumId, apiKey, currentPage);
            using var searchResponse = await http.SendAsync(searchRequest, cancellationToken).ConfigureAwait(false);
            if (!searchResponse.IsSuccessStatusCode)
                throw new IOException("album search");
            var searchBody = await searchResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var items = ParseSearchPage(searchBody, out var nextPage);
            foreach (var item in items)
            {
                if (seen.Add(item.Id))
                {
                    all.Add(item);
                    if (all.Count >= maxAssets)
                        break;
                }
            }
            if (nextPage is not int next || next <= currentPage || items.Count == 0 || all.Count >= maxAssets)
                break;
            page = next;
        }
        return all;
    }

    public static async Task<ImmichResult> SyncAsync(
        HttpClient http,
        string baseUrl,
        string albumId,
        string apiKey,
        string cacheDir,
        string localDir,
        CancellationToken cancellationToken,
        int maxAssets = 260,
        Action<IReadOnlyList<string>>? onBatchReady = null,
        bool randomSample = false)
    {
        if (randomSample)
            return await SyncRandomAsync(http, baseUrl, albumId, apiKey, cacheDir, localDir, cancellationToken, maxAssets, onBatchReady).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(cacheDir);
            using var albumRequest = BuildAlbumRequest(baseUrl, albumId, apiKey);
            using var albumResponse = await http.SendAsync(albumRequest, cancellationToken).ConfigureAwait(false);
            if (!albumResponse.IsSuccessStatusCode)
                throw new IOException("album");
            var body = await albumResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var assets = await FetchAlbumAssetsAsync(http, baseUrl, albumId, apiKey, body, Math.Max(1, maxAssets), cancellationToken).ConfigureAwait(false);
            if (assets.Count == 0)
            {
                DeleteUnlisted(cacheDir, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                SaveLiveMap(cacheDir, new Dictionary<string, string>());
                return new ImmichResult(ImmichStatus.Empty, PhotoDeck.List(localDir));
            }

            var liveMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var metaMap = new Dictionary<string, PhotoMetadata>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in assets)
            {
                if (!string.IsNullOrEmpty(asset.LivePhotoVideoId))
                    liveMap[asset.Id] = asset.LivePhotoVideoId;
                if (asset.Metadata != null && !asset.Metadata.IsEmpty)
                    metaMap[asset.Id] = asset.Metadata;
            }
            SaveLiveMap(cacheDir, liveMap);
            SaveMetaMap(cacheDir, metaMap);

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var incomplete = false;
            var batchNotified = false;
            foreach (var asset in assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(cacheDir, asset.Id + ".jpg");
                if (!IsCachedPreview(path))
                {
                    using var thumbRequest = BuildThumbRequest(baseUrl, asset.Id, apiKey);
                    using var thumbResponse = await http.SendAsync(thumbRequest, cancellationToken).ConfigureAwait(false);
                    if (!thumbResponse.IsSuccessStatusCode)
                    {
                        if (thumbResponse.StatusCode != System.Net.HttpStatusCode.NotFound)
                            throw new IOException("Preview unavailable.");
                        incomplete = true;
                        continue;
                    }
                    var bytes = await thumbResponse.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                    if (!HasImageHeader(bytes))
                    {
                        incomplete = true;
                        continue;
                    }
                    AtomicSave(path, bytes);
                }
                keep.Add(Path.GetFullPath(path));
                if (!batchNotified && onBatchReady != null && keep.Count >= 6)
                {
                    batchNotified = true;
                    onBatchReady(keep.OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).ToList());
                }
            }

            if (incomplete)
                return Fallback(cacheDir, localDir);
            DeleteUnlisted(cacheDir, keep);
            var files = keep.OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase).ToList();
            return new ImmichResult(files.Count == 0 ? ImmichStatus.Empty : ImmichStatus.Updated, files);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException or JsonException or UnauthorizedAccessException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Fallback(cacheDir, localDir);
        }
    }

    static async Task<ImmichResult> SyncRandomAsync(
        HttpClient http, string baseUrl, string albumId, string apiKey, string cacheDir, string localDir,
        CancellationToken cancellationToken, int maxAssets, Action<IReadOnlyList<string>>? onBatchReady)
    {
        try
        {
            Directory.CreateDirectory(cacheDir);
            var assets = await FetchRandomAlbumAssetsAsync(http, baseUrl, albumId, apiKey, Math.Max(1, maxAssets), cancellationToken).ConfigureAwait(false);
            if (assets.Count == 0)
            {
                DeleteUnlisted(cacheDir, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                SaveLiveMap(cacheDir, new Dictionary<string, string>());
                return new ImmichResult(ImmichStatus.Empty, PhotoDeck.List(localDir));
            }

            var liveMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var metaMap = new Dictionary<string, PhotoMetadata>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in assets)
            {
                if (!string.IsNullOrEmpty(asset.LivePhotoVideoId))
                    liveMap[asset.Id] = asset.LivePhotoVideoId;
                if (asset.Metadata != null && !asset.Metadata.IsEmpty)
                    metaMap[asset.Id] = asset.Metadata;
            }
            SaveLiveMap(cacheDir, liveMap);
            SaveMetaMap(cacheDir, metaMap);

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ordered = new List<string>();
            var notified = false;
            foreach (var asset in assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(cacheDir, asset.Id + ".jpg");
                if (!IsCachedPreview(path))
                {
                    using var thumbRequest = BuildThumbRequest(baseUrl, asset.Id, apiKey);
                    using var thumbResponse = await http.SendAsync(thumbRequest, cancellationToken).ConfigureAwait(false);
                    if (!thumbResponse.IsSuccessStatusCode)
                    {
                        if (thumbResponse.StatusCode != System.Net.HttpStatusCode.NotFound)
                            throw new IOException("Preview unavailable.");
                        continue;
                    }
                    var bytes = await thumbResponse.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                    if (!HasImageHeader(bytes))
                        continue;
                    AtomicSave(path, bytes);
                }
                var full = Path.GetFullPath(path);
                if (keep.Add(full))
                    ordered.Add(full);
                if (!notified && onBatchReady != null && ordered.Count >= 6)
                {
                    notified = true;
                    onBatchReady(ordered.ToList());
                }
            }

            if (ordered.Count == 0)
                return Fallback(cacheDir, localDir);
            DeleteUnlisted(cacheDir, keep);
            return new ImmichResult(ImmichStatus.Updated, ordered);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException or JsonException or UnauthorizedAccessException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Fallback(cacheDir, localDir);
        }
    }

    public static async Task<string?> EnsureLivePhotoVideoAsync(
        HttpClient http,
        string baseUrl,
        string videoId,
        string apiKey,
        string liveCacheDir,
        string assetId,
        CancellationToken cancellationToken,
        long maxBytes = 150L * 1024 * 1024)
    {
        if (!SafeId(assetId) || !SafeId(videoId))
            return null;
        try
        {
            Directory.CreateDirectory(liveCacheDir);
            var finalPath = Path.Combine(liveCacheDir, assetId + ".mov");
            if (IsCachedVideo(finalPath))
            {
                try { File.SetLastWriteTimeUtc(finalPath, DateTime.UtcNow); } catch { }
                return finalPath;
            }
            using var req = BuildVideoRequest(baseUrl, videoId, apiKey);
            using var resp = await http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (!HasVideoHeader(bytes))
                return null;
            AtomicSave(finalPath, bytes);
            TrimVideoCache(liveCacheDir, maxBytes, finalPath);
            return finalPath;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException or UnauthorizedAccessException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    static void TrimVideoCache(string liveCacheDir, long maxBytes, string keepPath)
    {
        try
        {
            var files = new DirectoryInfo(liveCacheDir).EnumerateFiles("*.mov").ToList();
            var total = files.Sum(f => f.Length);
            if (total <= maxBytes)
                return;
            foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= maxBytes)
                    break;
                if (string.Equals(file.FullName, Path.GetFullPath(keepPath), StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    var len = file.Length;
                    file.Delete();
                    total -= len;
                }
                catch { }
            }
        }
        catch { }
    }

    static ImmichResult Fallback(string cacheDir, string localDir)
    {
        var cache = ListCache(cacheDir);
        if (cache.Count > 0)
            return new ImmichResult(ImmichStatus.KeptCache, cache);
        return new ImmichResult(ImmichStatus.FellBackToLocal, PhotoDeck.List(localDir));
    }

    static void DeleteUnlisted(string cacheDir, HashSet<string> keep)
    {
        foreach (var existing in PhotoDeck.List(cacheDir))
        {
            if (!keep.Contains(Path.GetFullPath(existing)))
            {
                try { File.Delete(existing); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    static bool IsImage(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return false;
        var type = item.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
            ? typeEl.GetString() : null;
        if (string.Equals(type, "VIDEO", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(type, "IMAGE", StringComparison.OrdinalIgnoreCase))
            return true;
        var mime = item.TryGetProperty("originalMimeType", out var mimeEl) && mimeEl.ValueKind == JsonValueKind.String
            ? mimeEl.GetString() : null;
        return mime != null && (mime.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase)
            || mime.Equals("image/png", StringComparison.OrdinalIgnoreCase));
    }

    static string? ReadId(JsonElement item)
    {
        if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            return id.GetString();
        if (item.TryGetProperty("assetId", out var assetId) && assetId.ValueKind == JsonValueKind.String)
            return assetId.GetString();
        return null;
    }

    static bool SafeId(string id)
    {
        if (id.Length is < 1 or > 80)
            return false;
        foreach (var ch in id)
        {
            if (ch is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_'))
                return false;
        }
        return true;
    }

    static string Trim(string baseUrl) => baseUrl.TrimEnd('/');
}
