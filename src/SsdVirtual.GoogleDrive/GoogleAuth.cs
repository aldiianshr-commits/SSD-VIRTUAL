using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SsdVirtual.Cloud;
using SsdVirtual.Core;

namespace SsdVirtual.GoogleDrive;

public sealed class GoogleTokens
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTime ExpiresUtc { get; set; }
}

public sealed class GoogleOAuthResult
{
    public GoogleTokens Tokens { get; set; } = new();
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>
/// OAuth 2.0 for installed apps (RFC 8252): system browser + loopback redirect + PKCE.
/// The user signs in on accounts.google.com in their own browser; SsdVirtual never sees the password.
/// </summary>
public static class GoogleOAuth
{
    public const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    public const string UserInfoEndpoint = "https://openidconnect.googleapis.com/v1/userinfo";
    public const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";
    public const string Scopes = "openid email profile https://www.googleapis.com/auth/drive";

    public static (string ClientId, string ClientSecret) ResolveClient(string? overrideId, string? overrideSecret)
    {
        if (!string.IsNullOrWhiteSpace(overrideId)) return (overrideId.Trim(), (overrideSecret ?? "").Trim());
        return (GoogleClient.EmbeddedClientId, GoogleClient.EmbeddedClientSecret);
    }

    /// <summary>Runs the interactive sign-in. Opens the default browser and waits for the redirect.</summary>
    public static async Task<GoogleOAuthResult> SignInAsync(string clientId, string clientSecret, string? loginHint, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(clientId))
            throw new InvalidOperationException("No Google OAuth client is configured. Add one in Settings → Google Drive.");

        var port = GetFreePort();
        var redirect = $"http://127.0.0.1:{port}/";
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        var listener = new HttpListener();
        listener.Prefixes.Add(redirect);
        try
        {
            listener.Start();
        }
        catch (HttpListenerException)
        {
            listener.Close();
            redirect = $"http://localhost:{port}/";
            listener = new HttpListener();
            listener.Prefixes.Add(redirect);
            listener.Start();
        }
        using var listenerScope = listener;

        var url = AuthEndpoint +
                  "?response_type=code" +
                  "&client_id=" + Uri.EscapeDataString(clientId) +
                  "&redirect_uri=" + Uri.EscapeDataString(redirect) +
                  "&scope=" + Uri.EscapeDataString(Scopes) +
                  "&code_challenge=" + challenge +
                  "&code_challenge_method=S256" +
                  "&state=" + state +
                  "&access_type=offline&prompt=" + Uri.EscapeDataString("consent select_account") +
                  (string.IsNullOrEmpty(loginHint) ? "" : "&login_hint=" + Uri.EscapeDataString(loginHint));
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

        HttpListenerContext ctx;
        using (ct.Register(() => { try { listener.Stop(); } catch { } }))
        {
            try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        }

        var query = ctx.Request.QueryString;
        var error = query["error"];
        var code = query["code"];
        var html = error == null && code != null && query["state"] == state
            ? "<html><body style='font-family:Segoe UI;background:#111;color:#eee'><h2>SsdVirtual</h2><p>Sign-in complete. You can close this tab.</p></body></html>"
            : "<html><body style='font-family:Segoe UI;background:#111;color:#eee'><h2>SsdVirtual</h2><p>Sign-in failed. Return to the app.</p></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        ctx.Response.Close();
        listener.Stop();

        if (error != null) throw new CloudException(CloudErrorKind.Auth, "Google sign-in was cancelled or denied: " + error);
        if (code == null || query["state"] != state) throw new CloudException(CloudErrorKind.Auth, "Invalid OAuth response (state mismatch).");

        using var http = Http.CreateClient(TimeSpan.FromSeconds(60));
        var form = new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = clientId,
            ["redirect_uri"] = redirect,
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = verifier,
        };
        if (!string.IsNullOrEmpty(clientSecret)) form["client_secret"] = clientSecret;
        using var resp = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new CloudException(CloudErrorKind.Auth, "Token exchange failed: " + json, (int)resp.StatusCode);
        var tokens = ParseTokenResponse(json, null);
        if (string.IsNullOrEmpty(tokens.RefreshToken))
            throw new CloudException(CloudErrorKind.Auth, "Google did not return a refresh token. Remove SsdVirtual from your Google account's third-party access and sign in again.");

        var result = new GoogleOAuthResult { Tokens = tokens };
        using var req = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        using var ui = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (ui.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(await ui.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            result.Email = doc.RootElement.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "";
            result.Name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        }
        return result;
    }

    public static GoogleTokens ParseTokenResponse(string json, string? existingRefreshToken)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new GoogleTokens
        {
            AccessToken = root.GetProperty("access_token").GetString() ?? "",
            RefreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? existingRefreshToken ?? "" : existingRefreshToken ?? "",
            ExpiresUtc = DateTime.UtcNow.AddSeconds(root.TryGetProperty("expires_in", out var ex) ? ex.GetInt32() - 60 : 3000),
        };
    }

    public static async Task RevokeAsync(string token, CancellationToken ct)
    {
        try
        {
            using var http = Http.CreateClient(TimeSpan.FromSeconds(20));
            using var _ = await http.PostAsync(RevokeEndpoint, new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("Token revoke failed (ignored).", ex);
        }
    }

    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Holds and refreshes the access token of one account (service side).</summary>
public sealed class GoogleTokenSource
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly HttpClient _http;
    private readonly string _clientId, _clientSecret;
    private GoogleTokens _tokens;
    private readonly Action<GoogleTokens> _persist;

    public GoogleTokenSource(HttpClient http, string clientId, string clientSecret, GoogleTokens tokens, Action<GoogleTokens> persist)
    {
        _http = http;
        _clientId = clientId;
        _clientSecret = clientSecret;
        _tokens = tokens;
        _persist = persist;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct, bool forceRefresh = false)
    {
        if (!forceRefresh && !string.IsNullOrEmpty(_tokens.AccessToken) && _tokens.ExpiresUtc > DateTime.UtcNow) return _tokens.AccessToken;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && !string.IsNullOrEmpty(_tokens.AccessToken) && _tokens.ExpiresUtc > DateTime.UtcNow) return _tokens.AccessToken;
            if (string.IsNullOrEmpty(_tokens.RefreshToken)) throw new CloudException(CloudErrorKind.Auth, "No refresh token; sign in again.");
            if (string.IsNullOrEmpty(_clientId)) throw new CloudException(CloudErrorKind.Auth, "Google OAuth client is not configured.");
            var form = new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["refresh_token"] = _tokens.RefreshToken,
                ["grant_type"] = "refresh_token",
            };
            if (!string.IsNullOrEmpty(_clientSecret)) form["client_secret"] = _clientSecret;
            using var resp = await Http.SendWithRetryAsync(_http, () => Task.FromResult(new HttpRequestMessage(HttpMethod.Post, GoogleOAuth.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(form),
            }), ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                if (json.Contains("invalid_grant", StringComparison.Ordinal) || json.Contains("invalid_client", StringComparison.Ordinal))
                    throw new CloudException(CloudErrorKind.Auth, "Google session expired or was revoked. Reconnect the account.", (int)resp.StatusCode);
                throw new CloudException(Http.KindFromStatus(resp.StatusCode) == CloudErrorKind.Auth ? CloudErrorKind.Server : Http.KindFromStatus(resp.StatusCode),
                    "Token refresh failed: " + json, (int)resp.StatusCode);
            }
            _tokens = GoogleOAuth.ParseTokenResponse(json, _tokens.RefreshToken);
            _persist(_tokens);
            return _tokens.AccessToken;
        }
        finally
        {
            _lock.Release();
        }
    }
}
