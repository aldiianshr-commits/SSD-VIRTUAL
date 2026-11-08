namespace SsdVirtual.GoogleDrive;

/// <summary>
/// Build-time OAuth client. Empty by default: Google requires every application to register its own
/// "Desktop app" OAuth client. Either set the repository secrets GOOGLE_CLIENT_ID / GOOGLE_CLIENT_SECRET
/// (CI then generates GoogleClient.g.cs) or enter the client in SsdVirtual → Settings → Google Drive.
/// </summary>
public static partial class GoogleClient
{
    public const string EmbeddedClientId = "";
    public const string EmbeddedClientSecret = "";
}
