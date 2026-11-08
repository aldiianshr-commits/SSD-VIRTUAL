using System.Text.Json;
using SsdVirtual.Core;

namespace SsdVirtual.Security;

/// <summary>Credentials of one account. Never written to disk unencrypted and never returned over IPC.</summary>
public sealed class AccountSecrets
{
    public string RefreshToken { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public DateTime AccessTokenExpiresUtc { get; set; }
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    /// <summary>TeraBox web session cookie header (from the user's own interactive login).</summary>
    public string Cookie { get; set; } = "";
}

public sealed class SecretStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, AccountSecrets> _secrets = new();

    public SecretStore(string path)
    {
        _path = path;
        Load();
    }

    private void Load()
    {
        lock (_gate)
        {
            var text = ProtectedFile.ReadText(_path);
            _secrets = string.IsNullOrEmpty(text)
                ? new Dictionary<string, AccountSecrets>()
                : JsonSerializer.Deserialize<Dictionary<string, AccountSecrets>>(text) ?? new();
        }
    }

    public AccountSecrets Get(string accountId)
    {
        lock (_gate)
        {
            return _secrets.TryGetValue(accountId, out var s) ? Clone(s) : new AccountSecrets();
        }
    }

    public void Set(string accountId, AccountSecrets secrets)
    {
        lock (_gate)
        {
            _secrets[accountId] = Clone(secrets);
            Save();
        }
    }

    public void Update(string accountId, Action<AccountSecrets> update)
    {
        lock (_gate)
        {
            if (!_secrets.TryGetValue(accountId, out var s)) _secrets[accountId] = s = new AccountSecrets();
            update(s);
            Save();
        }
    }

    public void Remove(string accountId)
    {
        lock (_gate)
        {
            if (_secrets.Remove(accountId)) Save();
        }
    }

    private void Save() => ProtectedFile.WriteText(_path, JsonSerializer.Serialize(_secrets));

    private static AccountSecrets Clone(AccountSecrets s) => new()
    {
        RefreshToken = s.RefreshToken,
        AccessToken = s.AccessToken,
        AccessTokenExpiresUtc = s.AccessTokenExpiresUtc,
        ClientId = s.ClientId,
        ClientSecret = s.ClientSecret,
        Cookie = s.Cookie,
    };
}

/// <summary>Encrypted persistence of <see cref="ServiceConfig"/>.</summary>
public sealed class ConfigStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public ConfigStore(string path) => _path = path;

    public ServiceConfig Load()
    {
        lock (_gate)
        {
            try
            {
                var text = ProtectedFile.ReadText(_path);
                if (string.IsNullOrWhiteSpace(text)) return new ServiceConfig();
                return JsonSerializer.Deserialize<ServiceConfig>(text, Json) ?? new ServiceConfig();
            }
            catch (Exception ex)
            {
                Log.Error("Configuration could not be loaded; starting with defaults.", ex);
                return new ServiceConfig();
            }
        }
    }

    public void Save(ServiceConfig config)
    {
        lock (_gate) ProtectedFile.WriteText(_path, JsonSerializer.Serialize(config, Json));
    }

    public static string ExportJson(ServiceConfig config) => JsonSerializer.Serialize(config, Json);
    public static ServiceConfig ImportJson(string json) => JsonSerializer.Deserialize<ServiceConfig>(json, Json) ?? throw new InvalidDataException("Empty configuration.");
}
