using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SsdVirtual.Cloud;
using SsdVirtual.Core;

namespace SsdVirtual.GoogleDrive;

/// <summary>Google Drive API v3 over plain HTTPS (official REST API, OAuth 2.0 bearer tokens).</summary>
public sealed class GoogleDriveProvider : ICloudProvider
{
    private const string Api = "https://www.googleapis.com/drive/v3";
    private const string UploadApi = "https://www.googleapis.com/upload/drive/v3";
    private const string FolderMime = "application/vnd.google-apps.folder";
    private const string FileFields = "id,name,mimeType,size,modifiedTime,createdTime,md5Checksum,parents,trashed";
    /// <summary>Chunk size for resumable uploads (must be a multiple of 256 KiB).</summary>
    private const int UploadChunk = 16 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly GoogleTokenSource _tokens;

    public GoogleDriveProvider(HttpClient http, GoogleTokenSource tokens)
    {
        _http = http;
        _tokens = tokens;
    }

    public ProviderKind Kind => ProviderKind.GoogleDrive;
    public ProviderCapabilities Capabilities => ProviderCapabilities.ChangeFeed | ProviderCapabilities.ContentMd5 | ProviderCapabilities.Trash;
    public string Note => "Google Drive API v3 (official). Google Docs/Sheets/Slides files are not shown because they have no binary content.";

    // ------------------------------------------------------------------ plumbing

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead, bool allowNonSuccess = false)
    {
        bool refreshed = false;
        while (true)
        {
            var token = await _tokens.GetAccessTokenAsync(ct, forceRefresh: refreshed).ConfigureAwait(false);
            var resp = await Http.SendWithRetryAsync(_http, () =>
            {
                var r = build();
                r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return Task.FromResult(r);
            }, ct, 6, completion).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.Unauthorized && !refreshed)
            {
                resp.Dispose();
                refreshed = true;
                continue;
            }
            if (resp.StatusCode == HttpStatusCode.Forbidden)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (body.Contains("rateLimitExceeded") || body.Contains("userRateLimitExceeded"))
                {
                    resp.Dispose();
                    await Task.Delay(TimeSpan.FromSeconds(2 + Random.Shared.Next(3)), ct).ConfigureAwait(false);
                    continue;
                }
                resp.Dispose();
                var kind = body.Contains("storageQuotaExceeded") ? CloudErrorKind.QuotaExceeded : CloudErrorKind.Auth;
                throw new CloudException(kind, "Google Drive refused the request: " + Trim(body), 403);
            }
            if (!resp.IsSuccessStatusCode && !allowNonSuccess)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var code = resp.StatusCode;
                resp.Dispose();
                throw new CloudException(Http.KindFromStatus(code), $"Google Drive error {(int)code}: {Trim(body)}", (int)code);
            }
            return resp;
        }
    }

    private static string Trim(string s) => s.Length > 400 ? s[..400] : s;

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct).ConfigureAwait(false);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    private async Task<JsonDocument> SendJsonAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        var payload = body == null ? null : JsonSerializer.Serialize(body);
        using var resp = await SendAsync(() => new HttpRequestMessage(method, url)
        {
            Content = payload == null ? null : new StringContent(payload, Encoding.UTF8, "application/json"),
        }, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    private static CloudItem ParseItem(JsonElement e)
    {
        var item = new CloudItem
        {
            Id = e.GetProperty("id").GetString() ?? "",
            Name = e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            IsDirectory = e.TryGetProperty("mimeType", out var m) && m.GetString() == FolderMime,
            Size = e.TryGetProperty("size", out var s) && long.TryParse(s.GetString(), out var sz) ? sz : 0,
            ModifiedUtc = e.TryGetProperty("modifiedTime", out var mt) && mt.TryGetDateTime(out var mtd) ? mtd.ToUniversalTime() : DateTime.UtcNow,
            CreatedUtc = e.TryGetProperty("createdTime", out var ctm) && ctm.TryGetDateTime(out var ctd) ? ctd.ToUniversalTime() : DateTime.UtcNow,
            Md5 = e.TryGetProperty("md5Checksum", out var md) ? md.GetString() ?? "" : "",
            Trashed = e.TryGetProperty("trashed", out var t) && t.ValueKind == JsonValueKind.True,
        };
        if (e.TryGetProperty("parents", out var parents) && parents.ValueKind == JsonValueKind.Array)
            foreach (var p in parents.EnumerateArray()) item.ParentIds.Add(p.GetString() ?? "");
        return item;
    }

    /// <summary>Google-native documents (Docs, Sheets...) and shortcuts have no downloadable bytes.</summary>
    private static bool IsSupported(JsonElement e)
    {
        var mime = e.TryGetProperty("mimeType", out var m) ? m.GetString() ?? "" : "";
        return mime == FolderMime || !mime.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal);
    }

    private static string Q(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

    // ------------------------------------------------------------------ ICloudProvider

    public async Task<CloudAccountInfo> GetAccountInfoAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync(Api + "/about?fields=user(displayName,emailAddress),storageQuota(limit,usage)", ct).ConfigureAwait(false);
        var r = doc.RootElement;
        var info = new CloudAccountInfo();
        if (r.TryGetProperty("user", out var u))
        {
            info.Email = u.TryGetProperty("emailAddress", out var e) ? e.GetString() ?? "" : "";
            info.Name = u.TryGetProperty("displayName", out var d) ? d.GetString() ?? "" : "";
        }
        if (r.TryGetProperty("storageQuota", out var q))
        {
            if (q.TryGetProperty("limit", out var l) && long.TryParse(l.GetString(), out var limit)) info.TotalBytes = limit;
            else info.Unlimited = true;
            if (q.TryGetProperty("usage", out var us) && long.TryParse(us.GetString(), out var usage)) info.UsedBytes = usage;
        }
        return info;
    }

    public async Task<CloudItem> ResolveRootAsync(string remoteRoot, CancellationToken ct)
    {
        using var rootDoc = await GetJsonAsync(Api + "/files/root?fields=" + FileFields, ct).ConfigureAwait(false);
        var current = ParseItem(rootDoc.RootElement);
        foreach (var part in remoteRoot.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var url = Api + "/files?pageSize=10&fields=files(" + FileFields + ")&q=" +
                      Uri.EscapeDataString($"'{Q(current.Id)}' in parents and name = '{Q(part)}' and mimeType = '{FolderMime}' and trashed = false");
            using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
            var files = doc.RootElement.GetProperty("files");
            current = files.GetArrayLength() > 0
                ? ParseItem(files[0])
                : await CreateFolderAsync(new RemoteRef(current.Id, ""), "", part, ct).ConfigureAwait(false);
        }
        return current;
    }

    public async Task<IReadOnlyList<CloudItem>> ListAsync(RemoteRef folder, CancellationToken ct)
    {
        var list = new List<CloudItem>();
        string? pageToken = null;
        do
        {
            var url = Api + "/files?pageSize=1000&spaces=drive&fields=nextPageToken,files(" + FileFields + ")&q=" +
                      Uri.EscapeDataString($"'{Q(folder.Id)}' in parents and trashed = false") +
                      (pageToken == null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken));
            using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
            foreach (var f in doc.RootElement.GetProperty("files").EnumerateArray())
                if (IsSupported(f)) list.Add(ParseItem(f));
            pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var t) ? t.GetString() : null;
        } while (!string.IsNullOrEmpty(pageToken));
        return list;
    }

    public async Task<CloudItem> CreateFolderAsync(RemoteRef parent, string parentPath, string name, CancellationToken ct)
    {
        using var doc = await SendJsonAsync(HttpMethod.Post, Api + "/files?fields=" + FileFields,
            new Dictionary<string, object> { ["name"] = name, ["mimeType"] = FolderMime, ["parents"] = new[] { parent.Id } }, ct).ConfigureAwait(false);
        return ParseItem(doc.RootElement);
    }

    public async Task DeleteAsync(RemoteRef item, bool isDirectory, bool toTrash, CancellationToken ct)
    {
        try
        {
            if (toTrash)
            {
                using var _ = await SendJsonAsync(HttpMethod.Patch, Api + "/files/" + Uri.EscapeDataString(item.Id) + "?fields=id",
                    new Dictionary<string, object> { ["trashed"] = true }, ct).ConfigureAwait(false);
            }
            else
            {
                using var _ = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, Api + "/files/" + Uri.EscapeDataString(item.Id)), ct).ConfigureAwait(false);
            }
        }
        catch (CloudException ex) when (ex.Kind == CloudErrorKind.NotFound)
        {
            // Already gone: deleting is idempotent.
        }
    }

    public async Task<CloudItem> MoveAsync(RemoteRef item, RemoteRef oldParent, RemoteRef newParent, string newParentPath, string newName, bool isDirectory, CancellationToken ct)
    {
        var url = Api + "/files/" + Uri.EscapeDataString(item.Id) + "?fields=" + FileFields;
        if (oldParent.Id != newParent.Id)
            url += "&addParents=" + Uri.EscapeDataString(newParent.Id) + "&removeParents=" + Uri.EscapeDataString(oldParent.Id);
        using var doc = await SendJsonAsync(HttpMethod.Patch, url, new Dictionary<string, object> { ["name"] = newName }, ct).ConfigureAwait(false);
        return ParseItem(doc.RootElement);
    }

    public async Task<int> ReadAsync(RemoteRef item, long offset, Memory<byte> buffer, long fileSize, CancellationToken ct)
    {
        var count = (int)Math.Min(buffer.Length, Math.Max(0, fileSize - offset));
        if (count == 0) return 0;
        var started = DateTime.UtcNow;
        using var resp = await SendAsync(() =>
        {
            var r = new HttpRequestMessage(HttpMethod.Get, Api + "/files/" + Uri.EscapeDataString(item.Id) + "?alt=media");
            r.Headers.Range = new RangeHeaderValue(offset, offset + count - 1);
            return r;
        }, ct, HttpCompletionOption.ResponseHeadersRead, allowNonSuccess: true).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) return 0;
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new CloudException(Http.KindFromStatus(resp.StatusCode), $"Download failed {(int)resp.StatusCode}: {Trim(body)}", (int)resp.StatusCode);
        }
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.OK && offset > 0)
        {
            // Server ignored the range header: skip forward.
            var skip = new byte[64 * 1024];
            long toSkip = offset;
            while (toSkip > 0)
            {
                int n = await stream.ReadAsync(skip.AsMemory(0, (int)Math.Min(skip.Length, toSkip)), ct).ConfigureAwait(false);
                if (n == 0) return 0;
                toSkip -= n;
            }
        }
        var read = await Http.ReadIntoAsync(stream, buffer[..count], ct).ConfigureAwait(false);
        TransferStats.RecordFetch(DateTime.UtcNow - started);
        return read;
    }

    private sealed class SessionState
    {
        public string Uri { get; set; } = "";
        public long Length { get; set; }
    }

    public async Task<CloudItem> UploadAsync(UploadRequest request, CancellationToken ct)
    {
        var length = request.Length;
        SessionState? state = null;
        var saved = request.Session.Load();
        if (!string.IsNullOrEmpty(saved))
        {
            try { state = JsonSerializer.Deserialize<SessionState>(saved); } catch { state = null; }
            if (state != null && state.Length != length) state = null;
        }

        for (int restart = 0; restart < 3; restart++)
        {
            if (state == null)
            {
                state = new SessionState { Uri = await StartSessionAsync(request, ct).ConfigureAwait(false), Length = length };
                request.Session.Save(JsonSerializer.Serialize(state));
            }

            var (offset, done) = await QueryOffsetAsync(state, ct).ConfigureAwait(false);
            if (done != null) { request.Session.Clear(); return done; }
            if (offset < 0)
            {
                // Session expired (404/410): start over.
                request.Session.Clear();
                state = null;
                continue;
            }

            while (true)
            {
                var chunk = (int)Math.Min(UploadChunk, length - offset);
                request.Content.Position = offset;
                var start = offset;
                using var resp = await PutChunkAsync(state.Uri, request, start, chunk, length, ct).ConfigureAwait(false);
                var code = (int)resp.StatusCode;
                if (code == 200 || code == 201)
                {
                    request.Session.Clear();
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                    return ParseItem(doc.RootElement);
                }
                if (code == 308)
                {
                    offset = ParseRangeEnd(resp) + 1;
                    request.Progress?.Invoke(offset);
                    continue;
                }
                if (code == 404 || code == 410) { request.Session.Clear(); state = null; break; }
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (code == 403 && body.Contains("storageQuotaExceeded"))
                    throw new CloudException(CloudErrorKind.QuotaExceeded, "Google Drive storage is full.", 403);
                if (code >= 500 || code == 429 || code == 408)
                {
                    // Transient: re-query the committed offset and continue.
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                    var (o, d) = await QueryOffsetAsync(state, ct).ConfigureAwait(false);
                    if (d != null) { request.Session.Clear(); return d; }
                    if (o < 0) { request.Session.Clear(); state = null; break; }
                    offset = o;
                    continue;
                }
                throw new CloudException(Http.KindFromStatus(resp.StatusCode), $"Upload failed {code}: {Trim(body)}", code);
            }
        }
        throw new CloudException(CloudErrorKind.Server, "Upload session could not be completed after several restarts.");
    }

    private async Task<string> StartSessionAsync(UploadRequest request, CancellationToken ct)
    {
        var meta = new JsonObject
        {
            ["modifiedTime"] = (request.ModifiedUtc == default ? DateTime.UtcNow : request.ModifiedUtc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        };
        string url;
        HttpMethod method;
        if (request.Existing is { } existing)
        {
            url = UploadApi + "/files/" + Uri.EscapeDataString(existing.Id) + "?uploadType=resumable&fields=" + FileFields;
            method = HttpMethod.Patch;
        }
        else
        {
            meta["name"] = request.Name;
            meta["parents"] = new JsonArray(JsonValue.Create(request.Parent.Id));
            url = UploadApi + "/files?uploadType=resumable&fields=" + FileFields;
            method = HttpMethod.Post;
        }
        var json = meta.ToJsonString();
        using var resp = await SendAsync(() =>
        {
            var r = new HttpRequestMessage(method, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            r.Headers.Add("X-Upload-Content-Length", request.Length.ToString());
            r.Headers.Add("X-Upload-Content-Type", "application/octet-stream");
            return r;
        }, ct).ConfigureAwait(false);
        return resp.Headers.Location?.ToString() ?? throw new CloudException(CloudErrorKind.Server, "Google did not return an upload session URI.");
    }

    /// <summary>Returns the next byte to send, -1 when the session is gone, or the finished item.</summary>
    private async Task<(long Offset, CloudItem? Done)> QueryOffsetAsync(SessionState state, CancellationToken ct)
    {
        using var resp = await SendAsync(() =>
        {
            var r = new HttpRequestMessage(HttpMethod.Put, state.Uri) { Content = new ByteArrayContent(Array.Empty<byte>()) };
            r.Content.Headers.ContentRange = new ContentRangeHeaderValue(state.Length);
            return r;
        }, ct, allowNonSuccess: true).ConfigureAwait(false);
        var code = (int)resp.StatusCode;
        if (code == 200 || code == 201)
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return (state.Length, ParseItem(doc.RootElement));
        }
        if (code == 308) return (resp.Headers.TryGetValues("Range", out _) ? ParseRangeEnd(resp) + 1 : 0, null);
        if (code == 404 || code == 410) return (-1, null);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new CloudException(Http.KindFromStatus(resp.StatusCode), $"Upload status query failed {code}: {Trim(body)}", code);
    }

    private async Task<HttpResponseMessage> PutChunkAsync(string uri, UploadRequest request, long start, int count, long total, CancellationToken ct)
    {
        // A chunk is buffered so it can be retried without re-reading a half-consumed stream.
        var buffer = new byte[count];
        request.Content.Position = start;
        int got = 0;
        while (got < count)
        {
            int n = await request.Content.ReadAsync(buffer.AsMemory(got, count - got), ct).ConfigureAwait(false);
            if (n == 0) throw new IOException("Local file shrank during upload.");
            got += n;
        }
        return await SendAsync(() =>
        {
            var content = new StreamContent(new ThrottledReadStream(new MemoryStream(buffer, false), count, start, request.Progress));
            content.Headers.ContentLength = count;
            if (total == 0) content.Headers.ContentRange = new ContentRangeHeaderValue(0);
            else content.Headers.ContentRange = new ContentRangeHeaderValue(start, start + count - 1, total);
            return new HttpRequestMessage(HttpMethod.Put, uri) { Content = content };
        }, ct, allowNonSuccess: true).ConfigureAwait(false);
    }

    private static long ParseRangeEnd(HttpResponseMessage resp)
    {
        if (!resp.Headers.TryGetValues("Range", out var values)) return -1;
        var v = values.FirstOrDefault() ?? "";
        var dash = v.LastIndexOf('-');
        return dash >= 0 && long.TryParse(v[(dash + 1)..], out var end) ? end : -1;
    }

    public async Task<string> GetChangeCursorAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync(Api + "/changes/startPageToken", ct).ConfigureAwait(false);
        return doc.RootElement.GetProperty("startPageToken").GetString() ?? "";
    }

    public async Task<ChangeBatch> GetChangesAsync(string cursor, CancellationToken ct)
    {
        var batch = new ChangeBatch();
        var token = cursor;
        while (true)
        {
            var url = Api + "/changes?pageSize=1000&includeRemoved=true&spaces=drive&restrictToMyDrive=true" +
                      "&fields=nextPageToken,newStartPageToken,changes(fileId,removed,file(" + FileFields + "))&pageToken=" + Uri.EscapeDataString(token);
            using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
            foreach (var c in doc.RootElement.GetProperty("changes").EnumerateArray())
            {
                var id = c.GetProperty("fileId").GetString() ?? "";
                var removed = c.TryGetProperty("removed", out var r) && r.ValueKind == JsonValueKind.True;
                if (removed || !c.TryGetProperty("file", out var f)) { batch.RemovedIds.Add(id); continue; }
                var item = ParseItem(f);
                if (item.Trashed || !IsSupported(f)) batch.RemovedIds.Add(id);
                else batch.Changed.Add(item);
            }
            if (doc.RootElement.TryGetProperty("newStartPageToken", out var ns))
            {
                batch.NextCursor = ns.GetString() ?? token;
                return batch;
            }
            token = doc.RootElement.GetProperty("nextPageToken").GetString() ?? throw new CloudException(CloudErrorKind.Server, "Malformed change feed.");
        }
    }

    public void Dispose() { }
}
