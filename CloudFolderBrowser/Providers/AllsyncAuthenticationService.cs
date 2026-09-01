namespace CloudFolderBrowser.Providers;

public sealed record AllsyncLoadResult(
    bool Success,
    bool Cancelled,
    int StatusCode,
    string? ErrorMessage)
{
    internal static AllsyncLoadResult Completed() => new(true, false, 200, null);
    internal static AllsyncLoadResult Aborted() => new(false, true, 401, null);

    internal static AllsyncLoadResult Failed(int statusCode, string? message) =>
        new(false, false, statusCode, message);
}

/// <summary>
/// Owns the AllSync/Qloud credential state machine. The caller supplies a
/// credential broker, so retry behavior is independent from WinForms.
/// </summary>
public sealed class AllsyncAuthenticationService
{
    private readonly Func<string, bool, Task<bool>> _preloadAsync;
    private readonly Func<string, string, IProgress<int>?, Task<int>> _loadAsync;
    private readonly Func<string> _shareId;
    private readonly Func<string> _savedPassword;
    private readonly Action<string> _log;

    public AllsyncAuthenticationService(MainFormModel model)
        : this(
            model.PreloadAllsync,
            model.LoadAllsync,
            () => model.folderKey,
            () => model.password,
            message => model.WriteToLog(message))
    {
    }

    internal AllsyncAuthenticationService(
        Func<string, bool, Task<bool>> preloadAsync,
        Func<string, string, IProgress<int>?, Task<int>> loadAsync,
        Func<string> shareId,
        Func<string> savedPassword,
        Action<string>? log = null)
    {
        _preloadAsync = preloadAsync;
        _loadAsync = loadAsync;
        _shareId = shareId;
        _savedPassword = savedPassword;
        _log = log ?? (_ => { });
    }

    public async Task<AllsyncLoadResult> LoadAsync(
        string url,
        bool onlyCheck,
        ICloudProviderCredentialBroker credentialBroker,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool preloaded = await _preloadAsync(url, onlyCheck).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        if (!preloaded)
            return AllsyncLoadResult.Failed(0, null);
        if (onlyCheck)
            return AllsyncLoadResult.Completed();

        string shareId = _shareId();
        string password = _savedPassword();
        int statusCode = await _loadAsync(shareId, password, progress).ConfigureAwait(true);
        int attempt = 0;
        bool savedCredentialRejected = statusCode == 401 && !string.IsNullOrEmpty(password);

        while (statusCode == 401)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;
            _log($"\n{DateTime.Now:O}\n AllSync password rejected for {shareId}; requesting credential attempt {attempt}.\n\n");
            password = await credentialBroker.RequestPasswordAsync(
                new CloudProviderCredentialRequest(
                    "allsync",
                    "AllSync / Qloud",
                    shareId,
                    attempt,
                    savedCredentialRejected),
                cancellationToken).ConfigureAwait(true) ?? string.Empty;
            if (password.Length == 0)
                return AllsyncLoadResult.Aborted();

            statusCode = await _loadAsync(shareId, password, progress).ConfigureAwait(true);
            savedCredentialRejected = false;
        }

        return statusCode switch
        {
            200 => AllsyncLoadResult.Completed(),
            403 => AllsyncLoadResult.Failed(statusCode, "Failed to load folder: Forbidden"),
            500 => AllsyncLoadResult.Failed(statusCode, "Failed to load folder: Server Error. Try later?"),
            504 => AllsyncLoadResult.Failed(statusCode, "Failed to load folder: Connection Timeout"),
            _ => AllsyncLoadResult.Failed(statusCode, $"Failed to load folder (error {statusCode}).")
        };
    }
}
