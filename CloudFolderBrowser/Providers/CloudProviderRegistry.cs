using System.Reflection;
using System.Runtime.Loader;
using CloudFolderBrowser.Accounts;
using CloudFolderBrowser.Networking;

namespace CloudFolderBrowser.Providers;

[Flags]
public enum CloudProviderCapabilities
{
    None = 0,
    Browse = 1,
    DirectDownload = 2,
    Resume = 4,
    Import = 8,
    Authentication = 16,
    HealthCheck = 32,
    AccountValidation = 64,
    LinkResolver = 128
}

public interface ICloudProviderDescriptor
{
    string Id { get; }
    string DisplayName { get; }
    CloudServiceType ServiceType { get; }
    CloudProviderCapabilities Capabilities { get; }
    bool CanHandle(Uri uri);
}

public interface ICloudFolderProvider : ICloudProviderDescriptor
{
    Task<CloudFolder> LoadAsync(
        string url,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class CloudProviderRegistry
{
    private readonly object _gate = new();
    private readonly List<ICloudProviderDescriptor> _providers = new();
    private readonly List<AssemblyLoadContext> _pluginLoadContexts = new();

    public static CloudProviderRegistry Default { get; } = CreateDefault();

    public IReadOnlyList<ICloudProviderDescriptor> Providers
    {
        get
        {
            lock (_gate)
                return _providers.ToArray();
        }
    }

    public void Register(ICloudProviderDescriptor provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ValidateProvider(provider);
        lock (_gate)
        {
            EnsureIdsAreAvailable(new[] { provider });
            _providers.Insert(0, provider);
        }
    }

    private static void ValidateProvider(ICloudProviderDescriptor provider)
    {
        if (string.IsNullOrWhiteSpace(provider.Id))
            throw new ArgumentException("A cloud provider must have a stable, non-empty id.", nameof(provider));
        if (provider is ICloudProviderV2 v2
            && v2.ContractVersion != CloudProviderContractVersions.Current)
        {
            throw new NotSupportedException(
                $"Provider '{provider.Id}' uses contract v{v2.ContractVersion}; " +
                $"this application supports v{CloudProviderContractVersions.Current}.");
        }
    }

    private void RegisterPluginProviders(IReadOnlyList<ICloudProviderDescriptor> providers)
    {
        foreach (ICloudProviderDescriptor provider in providers)
            ValidateProvider(provider);
        lock (_gate)
        {
            EnsureIdsAreAvailable(providers);
            _providers.InsertRange(0, providers);
        }
    }

    private void EnsureIdsAreAvailable(IReadOnlyList<ICloudProviderDescriptor> providers)
    {
        string? duplicateInPlugin = providers
            .GroupBy(provider => provider.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateInPlugin != null)
            throw new InvalidOperationException(
                $"The plugin contains more than one provider with id '{duplicateInPlugin}'.");

        ICloudProviderDescriptor? duplicate = providers.FirstOrDefault(provider =>
            _providers.Any(existing => existing.Id.Equals(provider.Id, StringComparison.OrdinalIgnoreCase)));
        if (duplicate != null)
            throw new InvalidOperationException(
                $"A cloud provider with id '{duplicate.Id}' is already registered.");
    }

    public ICloudProviderDescriptor? Resolve(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return null;

        ICloudProviderDescriptor[] providers;
        lock (_gate)
            providers = _providers.ToArray();
        return providers.FirstOrDefault(provider =>
            provider.Capabilities.HasFlag(CloudProviderCapabilities.Browse)
            && provider.CanHandle(uri));
    }

    public async Task<CloudFolder> LoadAsync(
        string url,
        CloudProviderLoadContext context,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ICloudProviderDescriptor provider = Resolve(url)
            ?? throw new NotSupportedException("No registered provider can handle this link.");

        return provider switch
        {
            ICloudFolderProviderV2 v2 => await v2.LoadAsync(
                context, url, progress, cancellationToken).ConfigureAwait(false),
            ICloudFolderProvider v1 => await v1.LoadAsync(
                url, progress, cancellationToken).ConfigureAwait(false),
            _ => throw new NotSupportedException(
                $"Provider '{provider.DisplayName}' can identify this link but has no folder loader.")
        };
    }

    public Task<CloudFolder> LoadPortableAsync(
        string url,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default) =>
        LoadAsync(url, new CloudProviderLoadContext(), progress, cancellationToken);

    public ICloudProviderV2? GetForAccount(CloudAccountProvider provider)
    {
        lock (_gate)
        {
            return _providers.OfType<ICloudProviderV2>()
                .FirstOrDefault(candidate => candidate.AccountProvider == provider);
        }
    }

    public Task ValidateAccountAsync(
        CloudAccountProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ICloudProviderV2 provider = GetForAccount(profile.Provider)
            ?? throw new NotSupportedException(
                $"No Provider API v2 account integration is registered for {profile.ProviderName}.");
        return provider.ValidateAccountAsync(profile, cancellationToken);
    }

    public IDownloadLinkResolver? CreateLinkResolver(CloudAccountProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return GetForAccount(profile.Provider)?.CreateLinkResolver(profile);
    }

    public bool SupportsLinkResolver(CloudAccountProvider provider) =>
        GetForAccount(provider)?.Capabilities.HasFlag(CloudProviderCapabilities.LinkResolver) == true;

    public IReadOnlyList<CloudProviderHealthDefinition> GetHealthDefinitions()
    {
        lock (_gate)
        {
            return _providers.OfType<ICloudProviderV2>()
                .Select(provider => provider.HealthCheck)
                .Where(definition => definition != null)
                .Cast<CloudProviderHealthDefinition>()
                .GroupBy(definition => definition.RouteKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
        }
    }

    public IReadOnlyList<string> LoadPlugins(string directory)
    {
        var errors = new List<string>();
        if (!Directory.Exists(directory))
            return errors;

        foreach (string assemblyPath in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            ProviderPluginLoadContext? loadContext = null;
            try
            {
                loadContext = new ProviderPluginLoadContext(assemblyPath);
                Assembly assembly = loadContext.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
                IEnumerable<Type> providerTypes = GetLoadableTypes(assembly, errors, assemblyPath)
                    .Where(type => !type.IsAbstract
                        && typeof(ICloudProviderDescriptor).IsAssignableFrom(type)
                        && type.GetConstructor(Type.EmptyTypes) != null);
                ICloudProviderDescriptor[] discoveredProviders = providerTypes
                    .Select(providerType => Activator.CreateInstance(providerType))
                    .OfType<ICloudProviderDescriptor>()
                    .ToArray();
                if (discoveredProviders.Length > 0)
                {
                    RegisterPluginProviders(discoveredProviders);
                    lock (_gate)
                        _pluginLoadContexts.Add(loadContext);
                }
                else
                {
                    loadContext.Unload();
                }
            }
            catch (Exception ex)
            {
                loadContext?.Unload();
                errors.Add($"{Path.GetFileName(assemblyPath)}: {ex.Message}");
            }
        }

        return errors;
    }

    private static IEnumerable<Type> GetLoadableTypes(
        Assembly assembly,
        ICollection<string> errors,
        string assemblyPath)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            foreach (Exception loaderException in ex.LoaderExceptions.Where(item => item != null)!)
            {
                errors.Add(
                    $"{Path.GetFileName(assemblyPath)} dependency: {loaderException.Message}");
            }
            return ex.Types.Where(type => type != null).Cast<Type>();
        }
    }

    private static CloudProviderRegistry CreateDefault()
    {
        var registry = new CloudProviderRegistry();
        registry._providers.AddRange(new ICloudProviderDescriptor[]
        {
            new TeraBoxPublicShareProvider(),
            new DropboxPublicFileProvider(),
            new GoogleDrivePublicFileProvider(),
            new MegaPublicFolderProvider(),
            new YandexPublicFolderProvider(),
            new H5AiIndexProvider(),
            new TheTroveIndexProvider(),
            new AllsyncPublicShareProvider(),
            new HostIntegratedCloudProvider("qloud", "Qloud", CloudServiceType.QCloud,
                CloudProviderCapabilities.Browse | CloudProviderCapabilities.DirectDownload | CloudProviderCapabilities.Resume | CloudProviderCapabilities.Authentication,
                host => host.Split('.').Any(label => label.Equals("qloud", StringComparison.OrdinalIgnoreCase))),
            new DebridCloudProvider(CloudAccountProvider.AllDebrid, "alldebrid", "AllDebrid",
                Health("AllDebrid", "AllDebrid", "https://api.alldebrid.com/v4/user")),
            new DebridCloudProvider(CloudAccountProvider.RealDebrid, "real-debrid", "Real-Debrid",
                Health("RealDebrid", "Real-Debrid", "https://api.real-debrid.com/rest/1.0/user")),
            new DebridCloudProvider(CloudAccountProvider.DebridLink, "debrid-link", "Debrid-Link",
                Health("DebridLink", "Debrid-Link", "https://debrid-link.com/api/v2/account/infos")),
            new DebridCloudProvider(CloudAccountProvider.Premiumize, "premiumize", "Premiumize.me",
                Health("Premiumize", "Premiumize.me", "https://www.premiumize.me/api/account/info")),
            new DebridCloudProvider(CloudAccountProvider.TorBox, "torbox", "TorBox",
                Health("TorBox", "TorBox", "https://api.torbox.app/v1/api/user/me")),
            // Keep this fallback last: it lets any HTTP(S) hoster or direct
            // file URL enter the common downloader/debrid pipeline.
            new GenericHttpFileProvider()
        });
        return registry;
    }

    private static CloudProviderHealthDefinition Health(
        string routeKey,
        string displayName,
        string endpoint) => new(routeKey, displayName, new Uri(endpoint));

    private sealed class HostIntegratedCloudProvider : ICloudFolderProviderV2
    {
        private readonly Func<string, bool> _hostPredicate;
        private readonly Func<Uri, bool>? _uriPredicate;

        public string Id { get; }
        public string DisplayName { get; }
        public CloudServiceType ServiceType { get; }
        public CloudProviderCapabilities Capabilities { get; }
        public CloudProviderExecutionMode ExecutionMode => CloudProviderExecutionMode.HostIntegrated;
        public CloudAccountProvider? AccountProvider { get; }
        public CloudProviderHealthDefinition? HealthCheck { get; }

        public HostIntegratedCloudProvider(
            string id,
            string displayName,
            CloudServiceType serviceType,
            CloudProviderCapabilities capabilities,
            Func<string, bool> hostPredicate,
            Func<Uri, bool>? uriPredicate = null,
            CloudAccountProvider? accountProvider = null,
            CloudProviderHealthDefinition? healthCheck = null)
        {
            Id = id;
            DisplayName = displayName;
            ServiceType = serviceType;
            Capabilities = capabilities
                | (healthCheck == null ? CloudProviderCapabilities.None : CloudProviderCapabilities.HealthCheck);
            _hostPredicate = hostPredicate;
            _uriPredicate = uriPredicate;
            AccountProvider = accountProvider;
            HealthCheck = healthCheck;
        }

        public bool CanHandle(Uri uri) => _hostPredicate(uri.Host) || _uriPredicate?.Invoke(uri) == true;

        public Task<CloudFolder> LoadAsync(
            CloudProviderLoadContext context,
            string url,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) =>
            context.RequireHost(Id).LoadHostIntegratedAsync(
                Id, url, progress, cancellationToken);
    }

    private sealed class DebridCloudProvider : ICloudProviderV2
    {
        public string Id { get; }
        public string DisplayName { get; }
        public CloudServiceType ServiceType => CloudServiceType.Other;
        public CloudProviderCapabilities Capabilities =>
            CloudProviderCapabilities.AccountValidation |
            CloudProviderCapabilities.LinkResolver |
            CloudProviderCapabilities.HealthCheck |
            CloudProviderCapabilities.Authentication;
        public CloudAccountProvider? AccountProvider { get; }
        public CloudProviderHealthDefinition? HealthCheck { get; }

        public DebridCloudProvider(
            CloudAccountProvider accountProvider,
            string id,
            string displayName,
            CloudProviderHealthDefinition healthCheck)
        {
            AccountProvider = accountProvider;
            Id = id;
            DisplayName = displayName;
            HealthCheck = healthCheck;
        }

        public bool CanHandle(Uri uri) => false;

        public async Task ValidateAccountAsync(
            CloudAccountProfile profile,
            CancellationToken cancellationToken = default)
        {
            using var resolver = new DebridLinkResolver(profile);
            await resolver.ValidateAccountAsync(cancellationToken).ConfigureAwait(false);
        }

        public IDownloadLinkResolver? CreateLinkResolver(CloudAccountProfile profile) =>
            new DebridLinkResolver(profile);
    }

    private sealed class ProviderPluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public ProviderPluginLoadContext(string pluginAssemblyPath)
            : base($"CloudProvider:{Path.GetFileNameWithoutExtension(pluginAssemblyPath)}", isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(Path.GetFullPath(pluginAssemblyPath));
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            Assembly contractAssembly = typeof(ICloudProviderDescriptor).Assembly;
            if (AssemblyName.ReferenceMatchesDefinition(assemblyName, contractAssembly.GetName()))
                return contractAssembly;

            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path == null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path == null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
