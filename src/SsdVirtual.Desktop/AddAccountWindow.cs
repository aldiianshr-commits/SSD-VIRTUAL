using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SsdVirtual.Core;
using SsdVirtual.Core.Ipc;
using SsdVirtual.GoogleDrive;

namespace SsdVirtual.Desktop;

/// <summary>
/// Adds an account (or signs an existing one in again). Google uses OAuth 2.0 in the default browser;
/// TeraBox uses its official login page inside an embedded browser and only continues after explicit consent.
/// </summary>
public sealed class AddAccountWindow : Window
{
    private readonly AccountConfig? _existing;
    private readonly ComboBox _provider;
    private readonly TextBox _name = Ui.Input("", 300);
    private readonly ComboBox _letter;
    private readonly ComboBox _cache;
    private readonly TextBlock _status = Ui.Dim("");
    private readonly Button _signIn;
    private CancellationTokenSource? _cts;

    public AddAccountWindow(AccountConfig? existing = null)
    {
        _existing = existing;
        Title = existing == null ? Loc.T("Accounts.Add") : Loc.T("Accounts.SignInAgain");
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MinWidth = 460;
        this.Res(BackgroundProperty, "Bg");
        WindowChrome.ApplyDarkTitleBar(this, App.Prefs.Theme == "Dark");

        _provider = Ui.Combo((ProviderKind.GoogleDrive, "Google Drive"), (ProviderKind.TeraBox, Loc.T("Add.TeraBoxExperimental")), (ProviderKind.LocalFolder, Loc.T("Provider.LocalFolder")));
        _letter = Ui.Combo(Dialogs.FreeLetters().Select(c => ((object)c, c + ":")).ToArray());
        _cache = Ui.Combo(CacheSizes.Presets.Select(p => ((object)p.Bytes, p.Label)).ToArray());
        _cache.Select(50L * Bytes.GiB);
        _signIn = new Button { Content = Loc.T("Add.SignIn"), Style = (Style)FindResource("PrimaryButton"), IsDefault = true };
        _signIn.Click += async (_, _) => await SignInAsync();
        var cancel = new Button { Content = Loc.T("Common.Cancel"), IsCancel = true };
        cancel.Click += (_, _) => _cts?.Cancel();

        var body = Ui.Stack(Ui.Title(Title), new Border { Height = 12 });
        if (existing == null)
        {
            body.Children.Add(Ui.Field(Loc.T("Add.Provider"), _provider));
            body.Children.Add(Ui.Field(Loc.T("Add.DisplayName"), _name));
            body.Children.Add(Ui.Field(Loc.T("Disks.DriveLetter"), _letter));
            body.Children.Add(Ui.Field(Loc.T("Add.CacheSize"), _cache));
        }
        else
        {
            _provider.Select(existing.Provider);
            body.Children.Add(Ui.Text($"{existing.DisplayName} ({existing.DriveLetter}:) — {Ui.ProviderText(existing.Provider)}"));
        }
        body.Children.Add(Ui.Dim(Loc.T("Add.Privacy"), 11.5));
        body.Children.Add(_status);
        var buttons = Ui.Row(_signIn, cancel);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(0, 14, 0, 0);
        body.Children.Add(buttons);
        body.Width = 440;
        Content = new Border { Padding = new Thickness(24), Child = body };
    }

    private async Task SignInAsync()
    {
        _signIn.IsEnabled = false;
        _cts = new CancellationTokenSource();
        try
        {
            var provider = (ProviderKind)(_provider.Selected() ?? ProviderKind.GoogleDrive);
            var letter = _existing?.DriveLetter ?? (char)(_letter.Selected() ?? '\0');
            var cache = (long)(_cache.Selected() ?? 50L * Bytes.GiB);
            var name = _name.Text.Trim();
            switch (provider)
            {
                case ProviderKind.GoogleDrive:
                {
                    var settings = Svc.LastStatus?.Settings ?? await Svc.Call<AppSettings>(IpcMethods.GetSettings);
                    var (clientId, clientSecret) = GoogleOAuth.ResolveClient(settings.GoogleClientId, settings.GoogleClientSecret);
                    if (string.IsNullOrEmpty(clientId)) throw new InvalidOperationException(Loc.T("Add.GoogleClientMissing"));
                    _status.Text = Loc.T("Add.WaitingBrowser");
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    timeout.CancelAfter(TimeSpan.FromMinutes(5));
                    var result = await GoogleOAuth.SignInAsync(clientId, clientSecret, _existing?.Email, timeout.Token);
                    Activate();
                    if (_existing != null)
                    {
                        if (_existing.Email.Length > 0 && !string.Equals(_existing.Email, result.Email, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException(Loc.F("Add.WrongAccount", _existing.Email, result.Email));
                        await Svc.Call(IpcMethods.UpdateCredentials, new UpdateCredentialsParams
                        {
                            AccountId = _existing.Id, RefreshToken = result.Tokens.RefreshToken, AccessToken = result.Tokens.AccessToken,
                            AccessTokenExpiresUtc = result.Tokens.ExpiresUtc,
                        }, 60000);
                    }
                    else
                    {
                        _status.Text = Loc.T("Add.Mounting");
                        await Svc.Call<AccountConfig>(IpcMethods.AddGoogleAccount, new AddGoogleAccountParams
                        {
                            DisplayName = name, DriveLetter = letter, Email = result.Email, AccountName = result.Name,
                            RefreshToken = result.Tokens.RefreshToken, AccessToken = result.Tokens.AccessToken,
                            AccessTokenExpiresUtc = result.Tokens.ExpiresUtc, CacheQuotaBytes = cache,
                            ClientId = settings.GoogleClientId, ClientSecret = settings.GoogleClientSecret,
                        }, 120000);
                    }
                    break;
                }
                case ProviderKind.TeraBox:
                {
                    var login = new TeraBoxLoginWindow { Owner = this };
                    if (login.ShowDialog() != true || string.IsNullOrEmpty(login.CookieHeader)) { _status.Text = Loc.T("Add.Cancelled"); return; }
                    if (_existing != null)
                        await Svc.Call(IpcMethods.UpdateCredentials, new UpdateCredentialsParams { AccountId = _existing.Id, Cookie = login.CookieHeader }, 60000);
                    else
                    {
                        _status.Text = Loc.T("Add.Mounting");
                        await Svc.Call<AccountConfig>(IpcMethods.AddTeraBoxAccount, new AddTeraBoxAccountParams
                        {
                            DisplayName = name, DriveLetter = letter, Cookie = login.CookieHeader, CacheQuotaBytes = cache,
                        }, 120000);
                    }
                    break;
                }
                default:
                {
                    if (_existing != null) { DialogResult = true; return; }
                    var folder = Dialogs.PickFolder(Loc.T("Add.PickFolder"));
                    if (folder == null) return;
                    await Svc.Call<AccountConfig>(IpcMethods.AddLocalFolderAccount, new AddLocalFolderAccountParams
                    {
                        DisplayName = name, DriveLetter = letter, FolderPath = folder, CacheQuotaBytes = cache,
                    }, 120000);
                    break;
                }
            }
            await Svc.RefreshStatusAsync();
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            _status.Text = Loc.T("Add.Cancelled");
        }
        catch (Exception ex)
        {
            _status.Text = "";
            Ui.ShowError(ex);
        }
        finally
        {
            _signIn.IsEnabled = true;
        }
    }
}

public static class CacheSizes
{
    public static readonly (long Bytes, string Label)[] Presets =
    {
        (10L * Bytes.GiB, "10 GB"), (20L * Bytes.GiB, "20 GB"), (50L * Bytes.GiB, "50 GB"), (100L * Bytes.GiB, "100 GB"),
        (200L * Bytes.GiB, "200 GB"), (500L * Bytes.GiB, "500 GB"),
    };
}
