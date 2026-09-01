using System.Net;
using System.Net.Http.Headers;
using CloudFolderBrowser.Accounts;
using CG.Web.MegaApiClient;
using Newtonsoft.Json;
using WebDAVClient;
using YandexDiskSharp;
using YandexDiskSharp.Models;
using CloudFolderBrowser.Networking;
using CloudFolderBrowser.Providers;
using Exception = System.Exception;

namespace CloudFolderBrowser;

public partial class MainForm
{
    private void MigrateLegacyAccounts()
    {
        try
        {
            IReadOnlyList<CloudAccountProfile> accounts = accountStore.GetAll();
            if (!string.IsNullOrWhiteSpace(Properties.Settings.Default.loginTokenMega)
                && accounts.All(profile => profile.Provider != CloudAccountProvider.Mega))
            {
                accountStore.Upsert(new CloudAccountProfile
                {
                    Provider = CloudAccountProvider.Mega,
                    DisplayName = string.IsNullOrWhiteSpace(Properties.Settings.Default.megaLogin)
                        ? "MEGA account"
                        : Properties.Settings.Default.megaLogin,
                    UserName = Properties.Settings.Default.megaLogin,
                    Secret = Properties.Settings.Default.loginTokenMega,
                    SecretKind = "mega-session"
                });
            }

            if (!string.IsNullOrWhiteSpace(Properties.Settings.Default.accessTokenYandex)
                && accounts.All(profile => profile.Provider != CloudAccountProvider.YandexDisk))
            {
                accountStore.Upsert(new CloudAccountProfile
                {
                    Provider = CloudAccountProvider.YandexDisk,
                    DisplayName = "Yandex Disk account",
                    Secret = Properties.Settings.Default.accessTokenYandex,
                    SecretKind = "oauth-token"
                });
            }
        }
        catch (Exception ex)
        {
            Model.WriteToLog($"\n{DateTime.Now:O}\nUnable to migrate cloud accounts: {ex}\n", true);
        }
    }

    private async void RestoreActiveCloudAccountsOnShown(object? sender, EventArgs e)
    {
        Shown -= RestoreActiveCloudAccountsOnShown;
        foreach (CloudAccountProfile profile in accountStore.GetAll().Where(account => account.IsActive))
        {
            if (profile.Provider is not (CloudAccountProvider.Mega or CloudAccountProvider.YandexDisk))
                continue;
            try
            {
                await ActivateAccountAsync(profile);
            }
            catch (Exception ex)
            {
                Model.WriteToLog(
                    $"\n{DateTime.Now:O}\nUnable to restore {profile.ProviderName} account '{profile.DisplayName}': {ex}\n",
                    true);
            }
        }
        UpdateAccountButtonState();
    }

    internal void UpdateAccountButtonState()
    {
        if (loginMega_button == null)
            return;
        int count;
        try
        {
            count = accountStore.GetAll().Count;
        }
        catch
        {
            count = 0;
        }
        loginMega_button.Text = count == 0 ? "Accounts" : $"Accounts ({count})";
    }

    internal async Task SaveAndActivateAccountAsync(CloudAccountProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        switch (profile.Provider)
        {
            case CloudAccountProvider.Mega:
                if (profile.SecretKind == "password")
                {
                    await LoginMega(profile.UserName, profile.Secret);
                    profile.Secret = Properties.Settings.Default.loginTokenMega;
                    profile.SecretKind = "mega-session";
                }
                else
                {
                    await ActivateMegaSessionAsync(profile.Secret);
                }
                break;

            case CloudAccountProvider.YandexDisk:
                await ConnectYandexAsync(profile.Secret);
                profile.SecretKind = "oauth-token";
                break;

            case CloudAccountProvider.WebDav:
                await ConnectWebDavAsync(profile);
                profile.SecretKind = "password";
                break;

            case CloudAccountProvider.Dropbox:
                await ValidateBearerTokenAsync(
                    "https://api.dropboxapi.com/2/users/get_current_account",
                    profile.Secret,
                    HttpMethod.Post,
                    "Dropbox");
                profile.SecretKind = "oauth-token";
                break;

            case CloudAccountProvider.GoogleDrive:
                await ValidateBearerTokenAsync(
                    "https://www.googleapis.com/drive/v3/about?fields=user(displayName,emailAddress)",
                    profile.Secret,
                    HttpMethod.Get,
                    "Google Drive");
                profile.SecretKind = "oauth-token";
                break;

            case CloudAccountProvider.AllDebrid:
            case CloudAccountProvider.RealDebrid:
            case CloudAccountProvider.DebridLink:
            case CloudAccountProvider.Premiumize:
            case CloudAccountProvider.TorBox:
                await CloudProviderRegistry.Default.ValidateAccountAsync(profile);
                profile.SecretKind = "api-token";
                break;
        }

        accountStore.Upsert(profile);
        UpdateAccountButtonState();
    }

    internal async Task ActivateAccountAsync(CloudAccountProfile profile)
    {
        switch (profile.Provider)
        {
            case CloudAccountProvider.Mega:
                await ActivateMegaSessionAsync(profile.Secret);
                break;
            case CloudAccountProvider.YandexDisk:
                await ConnectYandexAsync(profile.Secret);
                break;
            case CloudAccountProvider.WebDav:
                await ConnectWebDavAsync(profile);
                break;
            case CloudAccountProvider.Dropbox:
                await ValidateBearerTokenAsync(
                    "https://api.dropboxapi.com/2/users/get_current_account",
                    profile.Secret,
                    HttpMethod.Post,
                    "Dropbox");
                break;
            case CloudAccountProvider.GoogleDrive:
                await ValidateBearerTokenAsync(
                    "https://www.googleapis.com/drive/v3/about?fields=user(displayName,emailAddress)",
                    profile.Secret,
                    HttpMethod.Get,
                    "Google Drive");
                break;
            case CloudAccountProvider.AllDebrid:
            case CloudAccountProvider.RealDebrid:
            case CloudAccountProvider.DebridLink:
            case CloudAccountProvider.Premiumize:
            case CloudAccountProvider.TorBox:
                await CloudProviderRegistry.Default.ValidateAccountAsync(profile);
                break;
        }
        accountStore.SetActive(profile.Provider, profile.Id);
        UpdateAccountButtonState();
    }

    internal void RemoveAccount(CloudAccountProfile profile)
    {
        if (profile.IsActive && profile.Provider == CloudAccountProvider.Mega)
            LogoutMega();
        if (profile.IsActive && profile.Provider == CloudAccountProvider.YandexDisk)
        {
            rc = NetworkClientAdapters.CreateYandexClient();
            yadiskFolder = null!;
            Properties.Settings.Default.loginedYandex = false;
            Properties.Settings.Default.accessTokenYandex = string.Empty;
            Properties.Settings.Default.Save();
        }
        accountStore.Remove(profile.Id);
        UpdateAccountButtonState();
    }

    private async Task ActivateMegaSessionAsync(string serializedToken)
    {
        if (string.IsNullOrWhiteSpace(serializedToken))
            throw new InvalidDataException("The saved MEGA session is empty.");
        var token = JsonConvert.DeserializeObject<MegaApiClient.LogonSessionToken>(
            serializedToken,
            new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto })
            ?? throw new InvalidDataException("The saved MEGA session is invalid.");
        await LoginMega(token);
        Properties.Settings.Default.loginTokenMega = serializedToken;
        Properties.Settings.Default.Save();
    }

    internal async Task ConnectYandexAsync(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("A Yandex OAuth token is required.", nameof(accessToken));

        var client = NetworkClientAdapters.CreateYandexClient(accessToken);
        client.Timeout = 20000;
        client.ReadWriteTimeout = 30000;
        Disk disk = await client.GetDiskAsync();
        Resource root = await client.GetResourceAsync("disk:/", limit: 200);
        rc = client;
        yadiskFolder = new CloudFolder(root);
        if (string.IsNullOrWhiteSpace(yadiskFolder.Path))
            yadiskFolder.Path = "disk:/";
        freeSpace = Math.Max(0, disk.TotalSpace - disk.UsedSpace);

        int maximum = (int)Math.Clamp(disk.TotalSpace / (1024 * 1024), 1, int.MaxValue);
        int used = (int)Math.Clamp(disk.UsedSpace / (1024 * 1024), 0, maximum);
        yadiskSpace_progressBar.Maximum = maximum;
        yadiskSpace_progressBar.Value = used;
        yadiskSpace_progressBar.CustomText = $"Yandex free: {freeSpace / 1073741824d:0.##} GB";
        yadiskSpace_progressBar.Visible = true;

        Properties.Settings.Default.accessTokenYandex = accessToken.Trim();
        Properties.Settings.Default.loginedYandex = true;
        Properties.Settings.Default.Save();
    }

    private async Task ConnectWebDavAsync(CloudAccountProfile profile)
    {
        if (!Uri.TryCreate(profile.ServerUrl, UriKind.Absolute, out Uri? serverUri)
            || serverUri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Enter a valid HTTPS WebDAV server URL.");
        }

        var client = NetworkClientAdapters.CreateWebDavClient(new NetworkCredential(profile.UserName, profile.Secret));
        client.Server = serverUri.GetLeftPart(UriPartial.Authority);
        client.BasePath = serverUri.AbsolutePath;
        client.UserAgent = "CloudFolderBrowser";
        client.UserAgentVersion = AppVersion;
        _ = (await client.List("/", 0)).ToArray();
        webdavClient = client;
    }

    private static async Task ValidateBearerTokenAsync(
        string url,
        string token,
        HttpMethod method,
        string providerName)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException($"A {providerName} access token is required.");
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        if (method == HttpMethod.Post)
            request.Content = new StringContent(string.Empty);
        string routeKey = providerName.Equals("Google Drive", StringComparison.OrdinalIgnoreCase)
            ? "GoogleDrive"
            : providerName;
        using HttpClient client = AppHttpClientFactory.CreateClient(
            TimeSpan.FromSeconds(20), routeKey: routeKey);
        using HttpResponseMessage response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"{providerName} rejected this token (HTTP {(int)response.StatusCode}).",
                null,
                response.StatusCode);
    }
}
