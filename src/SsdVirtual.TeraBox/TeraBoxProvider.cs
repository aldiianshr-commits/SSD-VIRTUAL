using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SsdVirtual.Cloud;
using SsdVirtual.Core;

namespace SsdVirtual.TeraBox;

/// <summary>
/// TeraBox integration through the same web endpoints terabox.com uses in the browser, authenticated with the
/// session the user creates by signing in on the official TeraBox login page inside SsdVirtual.
///
/// TeraBox does not offer a self-service public developer API (its Open Platform is partner-only), so this
/// adapter is marked experimental: the endpoints are undocumented and may change, and TeraBox's terms may
/// restrict automated access. The session cookie is only stored after the user explicitly agrees, is
/// encrypted with DPAPI, and is never sent anywhere except TeraBox.
/// </summary>
public sealed partial class TeraBoxProvider : ICloudProvider
{
    private const string AppId = "250528";
    private const string UserAgent = "terabox;1.37.0.7;PC;PC-Windows;10.0.22631;WindowsTeraBox";
    private const long BaseChunk = 4L * 1024 * 1024;

    private readonly HttpClient _http;
    private string _cookie;
    private string _baseUrl = "https://www.terabox.com";
    private string _domainPrefix = "jp";
    private string _jsToken = "";
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private readonly ConcurrentDictionary<string, (string Url, DateTime Expires)> _links = new();

    public TeraBoxProvider(HttpClient http, string cookie)
    {
        _http = http;
        _cookie = cookie;
    }

    public ProviderKind Kind => ProviderKind.TeraBox;
    public ProviderCapabilities Capabilities => ProviderCapabilities.PathBased | ProviderCapabilities.Trash;
    public string Note => "Experimental: uses the TeraBox web session from your own sign-in (no public TeraBox API exists). Endpoints may change without notice.";

    public void UpdateCookie(string cookie) { _cookie = cookie; _jsToken = ""; _links.Clear(); }

    // ------------------------------------------------------------------ plumbing

    private HttpRequestMessage NewRequest(HttpMethod method, string url)
    {
        var r = new HttpRequestMessage(method, url);
        r.Headers.TryAddWithoutValidation("Cookie", _cookie);
        r.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        r.Headers.TryAddWithoutValidation("Referer", _baseUrl + "/");
        r.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        r.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        return r;
    }

    private string BuildUrl(string pathOrUrl, IDictionary<string, string>? query)
    {
        var url = pathOrUrl.StartsWith("https://", StringComparison.Ordinal) ? pathOrUrl : _baseUrl + pathOrUrl;
        var q = new Dictionary<string, string>
        {
            ["app_id"] = AppId, ["web"] = "1", ["channel"] = "dubox", ["clienttype"] = "0", ["jsToken"] = _jsToken,
        };
        if (query != null) foreach (var kv in query) q[kv.Key] = kv.Value;
        var sb = new StringBuilder(url);
        sb.Append(url.Contains('?') ? '&' : '?');
        sb.Append(string.Join("&", q.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value))));
        return sb.ToString();
    }

    private async Task EnsureJsTokenAsync(CancellationToken ct, bool force = false)
    {
        if (!force && _jsToken.Length > 0) return;
        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!force && _jsToken.Length > 0) return;
            using var resp = await Http.SendWithRetryAsync(_http, () => Task.FromResult(NewRequest(HttpMethod.Get, _baseUrl + "/main")), ct).ConfigureAwait(false);
            var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var m = JsTokenRegex().Match(html);
            if (!m.Success) m = JsTokenPlainRegex().Match(html);
            if (!m.Success)
            {
                if ((int)resp.StatusCode is >= 300 and < 400 || html.Contains("login", StringComparison.OrdinalIgnoreCase))
                    throw new CloudException(CloudErrorKind.Auth, "TeraBox session is not signed in or has expired. Reconnect the account.");
                throw new CloudException(CloudErrorKind.Server, "TeraBox page format changed (jsToken not found).");
            }
            _jsToken = Uri.UnescapeDataString(m.Groups[1].Value);
        }
        finally { _tokenLock.Release(); }
    }

    [GeneratedRegex("fn%28%22(.*?)%22%29")]
    private static partial Regex JsTokenRegex();

    [GeneratedRegex("jsToken\\s*=\\s*\"([^\"]+)\"")]
    private static partial Regex JsTokenPlainRegex();

    private async Task<JsonDocument> CallAsync(HttpMethod method, string path, IDictionary<string, string>? query, HttpContent? body, CancellationToken ct, bool retried = false)
    {
        await EnsureJsTokenAsync(ct).ConfigureAwait(false);
        string? bodyText = body == null ? null : await body.ReadAsStringAsync(ct).ConfigureAwait(false);
        var bodyType = body?.Headers.ContentType?.ToString();
        using var resp = await Http.SendWithRetryAsync(_http, () =>
        {
            var r = NewRequest(method, BuildUrl(path, query));
            if (bodyText != null)
            {
                r.Content = new StringContent(bodyText, Encoding.UTF8);
                r.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(bodyType ?? "application/x-www-form-urlencoded");
            }
            return Task.FromResult(r);
        }, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode && text.Length == 0)
            throw new CloudException(Http.KindFromStatus(resp.StatusCode), $"TeraBox HTTP {(int)resp.StatusCode}", (int)resp.StatusCode);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException) { throw new CloudException(CloudErrorKind.Server, "TeraBox returned an unexpected response."); }

        var errno = Errno(doc.RootElement);
        if (errno is 4000023 or 450016 && !retried)
        {
            doc.Dispose();
            await EnsureJsTokenAsync(ct, force: true).ConfigureAwait(false);
            return await CallAsync(method, path, query, body == null ? null : new StringContent(bodyText!, Encoding.UTF8, bodyType ?? "application/x-www-form-urlencoded"), ct, true).ConfigureAwait(false);
        }
        if (errno == -6 && resp.Headers.TryGetValues("Url-Domain-Prefix", out var prefixes) && !retried)
        {
            doc.Dispose();
            _domainPrefix = prefixes.First();
            _baseUrl = "https://" + _domainPrefix + ".terabox.com";
            Log.Info("TeraBox: switched to regional host " + _baseUrl);
            return await CallAsync(method, path, query, body == null ? null : new StringContent(bodyText!, Encoding.UTF8, bodyType ?? "application/x-www-form-urlencoded"), ct, true).ConfigureAwait(false);
        }
        if (errno == -6) { doc.Dispose(); throw new CloudException(CloudErrorKind.Auth, $"TeraBox session expired (errno {errno}). Reconnect the account."); }
        if (errno == 9000) { doc.Dispose(); throw new CloudException(CloudErrorKind.Unsupported, "TeraBox is not available in this region."); }
        if (errno is -9 or 31066) { doc.Dispose(); throw new CloudException(CloudErrorKind.NotFound, $"TeraBox: item not found (errno {errno})."); }
        if (errno is -8 or 31061) { doc.Dispose(); throw new CloudException(CloudErrorKind.AlreadyExists, $"TeraBox: item already exists (errno {errno})."); }
        if (errno is -10 or 31112) { doc.Dispose(); throw new CloudException(CloudErrorKind.QuotaExceeded, "TeraBox storage is full."); }
        if (errno != 0) { doc.Dispose(); throw new CloudException(CloudErrorKind.Server, $"TeraBox error (errno {errno})."); }
        return doc;
    }

    private static int Errno(JsonElement e)
    {
        if (!e.TryGetProperty("errno", out var v)) return 0;
        return v.ValueKind == JsonValueKind.Number ? v.GetInt32() : int.TryParse(v.GetString(), out var i) ? i : 0;
    }

    private static FormUrlEncodedContent Form(Dictionary<string, string> data) => new(data);

    private static long GetLong(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind == JsonValueKind.Number ? v.GetInt64() : long.TryParse(v.GetString(), out var l) ? l : 0;
    }

    private static string GetString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";

    private static CloudItem ParseItem(JsonElement f)
    {
        var path = GetString(f, "path");
        var name = GetString(f, "server_filename");
        if (name.Length == 0) name = path.Split('/').LastOrDefault() ?? "";
        var mtime = GetLong(f, "server_mtime");
        var ctime = GetLong(f, "server_ctime");
        return new CloudItem
        {
            Id = GetString(f, "fs_id"),
            Path = path,
            Name = name,
            IsDirectory = GetLong(f, "isdir") == 1,
            Size = GetLong(f, "size"),
            ModifiedUtc = mtime > 0 ? DateTimeOffset.FromUnixTimeSeconds(mtime).UtcDateTime : default,
            CreatedUtc = ctime > 0 ? DateTimeOffset.FromUnixTimeSeconds(ctime).UtcDateTime : DateTime.UtcNow,
        };
    }

    private static string JoinPath(string parent, string name) => (parent.TrimEnd('/') + "/" + name).Replace("//", "/");

    // ------------------------------------------------------------------ ICloudProvider

    public async Task<CloudAccountInfo> GetAccountInfoAsync(CancellationToken ct)
    {
        using (await CallAsync(HttpMethod.Get, "/api/check/login", null, null, ct).ConfigureAwait(false)) { }
        var info = new CloudAccountInfo();
        using (var q = await CallAsync(HttpMethod.Get, "/api/quota", new Dictionary<string, string> { ["checkexpire"] = "1", ["checkfree"] = "1" }, null, ct).ConfigureAwait(false))
        {
            info.TotalBytes = GetLong(q.RootElement, "total");
            info.UsedBytes = GetLong(q.RootElement, "used");
        }
        try
        {
            using var u = await CallAsync(HttpMethod.Get, "/passport/get_info", null, null, ct).ConfigureAwait(false);
            if (u.RootElement.TryGetProperty("data", out var d))
            {
                info.Name = GetString(d, "display_name");
                if (info.Name.Length == 0) info.Name = GetString(d, "username");
                info.Email = GetString(d, "email");
            }
        }
        catch (CloudException ex) when (ex.Kind != CloudErrorKind.Network)
        {
            Log.Debug("TeraBox user info unavailable: " + ex.Message);
        }
        return info;
    }

    public async Task<CloudItem> ResolveRootAsync(string remoteRoot, CancellationToken ct)
    {
        var path = "/" + remoteRoot.Trim('/', '\\').Replace('\\', '/');
        if (path != "/")
        {
            var parent = path[..path.LastIndexOf('/')];
            if (parent.Length == 0) parent = "/";
            var name = path[(path.LastIndexOf('/') + 1)..];
            var siblings = await ListAsync(new RemoteRef("", parent), ct).ConfigureAwait(false);
            var existing = siblings.FirstOrDefault(s => s.IsDirectory && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;
            if (parent != "/") await ResolveRootAsync(parent, ct).ConfigureAwait(false);
            return await CreateFolderAsync(new RemoteRef("", parent), parent, name, ct).ConfigureAwait(false);
        }
        return new CloudItem { Id = "/", Path = "/", Name = "", IsDirectory = true };
    }

    public async Task<IReadOnlyList<CloudItem>> ListAsync(RemoteRef folder, CancellationToken ct)
    {
        var dir = string.IsNullOrEmpty(folder.Path) ? "/" : folder.Path;
        var list = new List<CloudItem>();
        for (int page = 1; ; page++)
        {
            using var doc = await CallAsync(HttpMethod.Get, "/api/list", new Dictionary<string, string>
            {
                ["dir"] = dir, ["page"] = page.ToString(), ["num"] = "1000", ["order"] = "name", ["desc"] = "0", ["showempty"] = "1",
            }, null, ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("list", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) break;
            foreach (var f in arr.EnumerateArray()) list.Add(ParseItem(f));
            if (arr.GetArrayLength() < 1000) break;
        }
        return list;
    }

    public async Task<CloudItem> CreateFolderAsync(RemoteRef parent, string parentPath, string name, CancellationToken ct)
    {
        var path = JoinPath(string.IsNullOrEmpty(parentPath) ? "/" : parentPath, name);
        using var doc = await CallAsync(HttpMethod.Post, "/api/create", new Dictionary<string, string> { ["a"] = "commit" },
            Form(new() { ["path"] = path, ["isdir"] = "1", ["block_list"] = "[]", ["rtype"] = "0" }), ct).ConfigureAwait(false);
        var item = ParseItem(doc.RootElement);
        item.IsDirectory = true;
        if (item.Path.Length == 0) item.Path = path;
        if (item.Name.Length == 0) item.Name = name;
        return item;
    }

    private async Task ManageAsync(string opera, object filelist, string ondup, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(filelist);
        using var _ = await CallAsync(HttpMethod.Post, "/api/filemanager", new Dictionary<string, string> { ["opera"] = opera, ["onnest"] = "fail" },
            Form(new() { ["async"] = "0", ["filelist"] = json, ["ondup"] = ondup }), ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(RemoteRef item, bool isDirectory, bool toTrash, CancellationToken ct)
    {
        // TeraBox always moves deleted items to its own recycle bin.
        try { await ManageAsync("delete", new[] { item.Path }, "fail", ct).ConfigureAwait(false); }
        catch (CloudException ex) when (ex.Kind == CloudErrorKind.NotFound) { }
    }

    public async Task<CloudItem> MoveAsync(RemoteRef item, RemoteRef oldParent, RemoteRef newParent, string newParentPath, string newName, bool isDirectory, CancellationToken ct)
    {
        var oldParentPath = item.Path[..Math.Max(0, item.Path.LastIndexOf('/'))];
        if (oldParentPath.Length == 0) oldParentPath = "/";
        var dest = string.IsNullOrEmpty(newParentPath) ? "/" : newParentPath;
        if (string.Equals(oldParentPath, dest, StringComparison.Ordinal))
            await ManageAsync("rename", new[] { new Dictionary<string, string> { ["path"] = item.Path, ["newname"] = newName } }, "fail", ct).ConfigureAwait(false);
        else
            await ManageAsync("move", new[] { new Dictionary<string, string> { ["path"] = item.Path, ["dest"] = dest, ["newname"] = newName } }, "overwrite", ct).ConfigureAwait(false);
        _links.TryRemove(item.Id, out _);
        return new CloudItem { Id = item.Id, Path = JoinPath(dest, newName), Name = newName, IsDirectory = isDirectory, ModifiedUtc = DateTime.UtcNow };
    }

    private async Task<string> GetDownloadUrlAsync(RemoteRef item, CancellationToken ct)
    {
        if (_links.TryGetValue(item.Id, out var cached) && cached.Expires > DateTime.UtcNow) return cached.Url;

        string sign;
        using (var home = await CallAsync(HttpMethod.Get, "/api/home/info", null, null, ct).ConfigureAwait(false))
        {
            var data = home.RootElement.GetProperty("data");
            sign = Sign(GetString(data, "sign3"), GetString(data, "sign1"));
        }
        string dlink;
        using (var doc = await CallAsync(HttpMethod.Get, "/api/download", new Dictionary<string, string>
        {
            ["type"] = "dlink", ["fidlist"] = "[" + item.Id + "]", ["sign"] = sign, ["vip"] = "2",
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
        }, null, ct).ConfigureAwait(false))
        {
            var arr = doc.RootElement.GetProperty("dlink");
            if (arr.GetArrayLength() == 0) throw new CloudException(CloudErrorKind.NotFound, "TeraBox returned no download link.");
            dlink = GetString(arr[0], "dlink");
        }

        // The dlink answers with a redirect to the actual content server.
        var url = dlink;
        for (int hop = 0; hop < 5; hop++)
        {
            using var resp = await Http.SendWithRetryAsync(_http, () =>
            {
                var r = NewRequest(HttpMethod.Get, url);
                r.Headers.Range = new RangeHeaderValue(0, 0);
                return Task.FromResult(r);
            }, ct, 4, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            var code = (int)resp.StatusCode;
            if (code is >= 300 and < 400 && resp.Headers.Location != null)
            {
                url = resp.Headers.Location.IsAbsoluteUri ? resp.Headers.Location.ToString() : new Uri(new Uri(url), resp.Headers.Location).ToString();
                continue;
            }
            if (resp.IsSuccessStatusCode) break;
            throw new CloudException(Http.KindFromStatus(resp.StatusCode), $"TeraBox download link failed (HTTP {code}).", code);
        }
        _links[item.Id] = (url, DateTime.UtcNow.AddMinutes(20));
        return url;
    }

    /// <summary>RC4-style signature the TeraBox web client computes for download requests.</summary>
    public static string Sign(string key, string data)
    {
        if (key.Length == 0) return "";
        var a = new int[256];
        var p = new int[256];
        for (int q = 0; q < 256; q++) { a[q] = key[q % key.Length]; p[q] = q; }
        for (int u = 0, q = 0; q < 256; q++)
        {
            u = (u + p[q] + a[q]) % 256;
            (p[q], p[u]) = (p[u], p[q]);
        }
        var o = new byte[data.Length];
        for (int i = 0, u = 0, q = 0; q < data.Length; q++)
        {
            i = (i + 1) % 256;
            u = (u + p[i]) % 256;
            (p[i], p[u]) = (p[u], p[i]);
            var k = p[(p[i] + p[u]) % 256];
            o[q] = (byte)(data[q] ^ k);
        }
        return Convert.ToBase64String(o);
    }

    public async Task<int> ReadAsync(RemoteRef item, long offset, Memory<byte> buffer, long fileSize, CancellationToken ct)
    {
        var count = (int)Math.Min(buffer.Length, Math.Max(0, fileSize - offset));
        if (count == 0) return 0;
        var started = DateTime.UtcNow;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var url = await GetDownloadUrlAsync(item, ct).ConfigureAwait(false);
            using var resp = await Http.SendWithRetryAsync(_http, () =>
            {
                var r = NewRequest(HttpMethod.Get, url);
                r.Headers.Range = new RangeHeaderValue(offset, offset + count - 1);
                return Task.FromResult(r);
            }, ct, 5, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone || (int)resp.StatusCode is >= 300 and < 400)
            {
                _links.TryRemove(item.Id, out _);
                continue;
            }
            if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) return 0;
            if (resp.StatusCode != HttpStatusCode.PartialContent && !(resp.StatusCode == HttpStatusCode.OK && offset == 0))
                throw new CloudException(Http.KindFromStatus(resp.StatusCode), $"TeraBox download failed (HTTP {(int)resp.StatusCode}).", (int)resp.StatusCode);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var n = await Http.ReadIntoAsync(stream, buffer[..count], ct).ConfigureAwait(false);
            TransferStats.RecordFetch(DateTime.UtcNow - started);
            return n;
        }
        throw new CloudException(CloudErrorKind.Server, "TeraBox download link kept expiring.");
    }

    private sealed class UploadState
    {
        public string UploadId { get; set; } = "";
        public string Path { get; set; } = "";
        public string Host { get; set; } = "";
        public long Length { get; set; }
        public long ChunkSize { get; set; }
        public List<string> PartMd5 { get; set; } = new();
    }

    public static long ChunkSizeFor(long length)
    {
        long chunk = BaseChunk, threshold = 4L * 1024 * 1024 * 1024;
        if (length < chunk) return Math.Max(length, 1);
        while (length > threshold) { chunk <<= 1; threshold <<= 1; }
        return chunk;
    }

    public async Task<CloudItem> UploadAsync(UploadRequest request, CancellationToken ct)
    {
        var targetPath = request.Existing is { } ex ? ex.Path : JoinPath(string.IsNullOrEmpty(request.ParentPath) ? "/" : request.ParentPath, request.Name);
        var parentPath = targetPath[..Math.Max(1, targetPath.LastIndexOf('/'))];
        var mtime = new DateTimeOffset(request.ModifiedUtc == default ? DateTime.UtcNow : request.ModifiedUtc).ToUnixTimeSeconds().ToString();

        UploadState? state = null;
        var saved = request.Session.Load();
        if (!string.IsNullOrEmpty(saved))
        {
            try { state = JsonSerializer.Deserialize<UploadState>(saved); } catch { }
            if (state != null && (state.Length != request.Length || state.Path != targetPath)) state = null;
        }

        if (state == null)
        {
            // locate upload host
            string host = $"c-{_domainPrefix}.terabox.com";
            try
            {
                using var resp = await Http.SendWithRetryAsync(_http, () => Task.FromResult(NewRequest(HttpMethod.Get,
                    $"https://{_domainPrefix}-data.terabox.com/rest/2.0/pcs/file?method=locateupload")), ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                var h = GetString(doc.RootElement, "host");
                if (h.Length > 0) host = h;
            }
            catch (Exception e) when (e is JsonException or CloudException { Kind: not CloudErrorKind.Network })
            {
                Log.Debug("TeraBox locateupload failed, using default host: " + e.Message);
            }

            var chunk = ChunkSizeFor(request.Length);
            var placeholder = request.Length > chunk ? "[\"5910a591dd8fc18c32a8f3df4fdc1761\",\"a5fc157d78e6ad1c7e114b056c92821e\"]" : "[\"5910a591dd8fc18c32a8f3df4fdc1761\"]";
            using var pre = await CallAsync(HttpMethod.Post, "/api/precreate", null, Form(new()
            {
                ["path"] = targetPath, ["autoinit"] = "1", ["target_path"] = parentPath, ["block_list"] = placeholder,
                ["local_mtime"] = mtime, ["file_limit_switch_v34"] = "true", ["size"] = request.Length.ToString(),
            }), ct).ConfigureAwait(false);
            state = new UploadState
            {
                UploadId = GetString(pre.RootElement, "uploadid"), Path = targetPath, Host = host, Length = request.Length, ChunkSize = chunk,
            };
            request.Session.Save(JsonSerializer.Serialize(state));
        }

        var count = request.Length == 0 ? 1 : (int)((request.Length + state.ChunkSize - 1) / state.ChunkSize);
        var buffer = new byte[(int)Math.Min(state.ChunkSize, Math.Max(1, request.Length))];
        for (int part = state.PartMd5.Count; part < count; part++)
        {
            var start = part * state.ChunkSize;
            var len = (int)Math.Min(state.ChunkSize, request.Length - start);
            request.Content.Position = start;
            int got = 0;
            while (got < len)
            {
                var n = await request.Content.ReadAsync(buffer.AsMemory(got, len - got), ct).ConfigureAwait(false);
                if (n == 0) throw new IOException("Local file shrank during upload.");
                got += n;
            }
            var md5 = Convert.ToHexString(MD5.HashData(buffer.AsSpan(0, len))).ToLowerInvariant();
            var url = BuildUrl($"https://{state.Host}/rest/2.0/pcs/superfile2", new Dictionary<string, string>
            {
                ["method"] = "upload", ["path"] = state.Path, ["uploadid"] = state.UploadId, ["partseq"] = part.ToString(), ["uploadsign"] = "0",
            });
            for (int attempt = 1; ; attempt++)
            {
                using var resp = await Http.SendWithRetryAsync(_http, () =>
                {
                    var r = NewRequest(HttpMethod.Post, url);
                    var form = new MultipartFormDataContent();
                    var file = new StreamContent(new ThrottledReadStream(new MemoryStream(buffer, 0, len, false), len, start, request.Progress));
                    file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    form.Add(file, "file", "blob");
                    r.Content = form;
                    return Task.FromResult(r);
                }, ct).ConfigureAwait(false);
                var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                string serverMd5 = "";
                try { using var d = JsonDocument.Parse(text); serverMd5 = GetString(d.RootElement, "md5"); } catch (JsonException) { }
                if (resp.IsSuccessStatusCode && string.Equals(serverMd5, md5, StringComparison.OrdinalIgnoreCase)) break;
                if (attempt >= 5) throw new CloudException(CloudErrorKind.Server, $"TeraBox rejected chunk {part} (HTTP {(int)resp.StatusCode}).");
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct).ConfigureAwait(false);
            }
            state.PartMd5.Add(md5);
            request.Session.Save(JsonSerializer.Serialize(state));
        }

        using var created = await CallAsync(HttpMethod.Post, "/api/create", new Dictionary<string, string> { ["isdir"] = "0", ["rtype"] = request.Existing != null ? "3" : "1" },
            Form(new()
            {
                ["path"] = state.Path, ["size"] = request.Length.ToString(), ["uploadid"] = state.UploadId, ["target_path"] = parentPath,
                ["block_list"] = JsonSerializer.Serialize(state.PartMd5), ["local_mtime"] = mtime,
            }), ct).ConfigureAwait(false);
        request.Session.Clear();
        var item = ParseItem(created.RootElement);
        if (item.Path.Length == 0) item.Path = state.Path;
        if (item.Name.Length == 0) item.Name = state.Path[(state.Path.LastIndexOf('/') + 1)..];
        if (item.Size == 0) item.Size = request.Length;
        _links.TryRemove(item.Id, out _);
        return item;
    }

    public async Task<CloudItem?> GetItemAsync(RemoteRef item, CancellationToken ct)
    {
        try
        {
            using var doc = await CallAsync(HttpMethod.Get, "/api/filemetas", new Dictionary<string, string>
            {
                ["target"] = JsonSerializer.Serialize(new[] { item.Path }),
            }, null, ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Array || info.GetArrayLength() == 0) return null;
            var it = ParseItem(info[0]);
            if (it.Path.Length == 0) it.Path = item.Path;
            return it;
        }
        catch (CloudException ex) when (ex.Kind == CloudErrorKind.NotFound) { return null; }
    }

    public Task<string> GetChangeCursorAsync(CancellationToken ct) => throw new CloudException(CloudErrorKind.Unsupported, "TeraBox has no change feed; SsdVirtual polls folders instead.");
    public Task<ChangeBatch> GetChangesAsync(string cursor, CancellationToken ct) => throw new CloudException(CloudErrorKind.Unsupported, "TeraBox has no change feed.");

    public void Dispose() => _tokenLock.Dispose();
}
