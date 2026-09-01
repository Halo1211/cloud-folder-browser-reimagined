using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Policy;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using CG.Web.MegaApiClient;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WebDAVClient;
using CloudFolderBrowser.Networking;
using CloudFolderBrowser.Sync;
using Exception = System.Exception;

namespace CloudFolderBrowser
{
    public class MainFormModel
    {
        public List<CloudFolder> AllFolders = new List<CloudFolder>();
        public CloudFolder CloudPublicFolder = new CloudFolder();
        bool debugMode = false; 
        public CloudServiceType CloudServiceType;

        public string PreferredDownloadRouteId { get; set; } = string.Empty;

        public bool LoadedFromFile = false;

        public string UserAgent = "";

        public bool ValidateFileSize = false;       

        public async Task<List<SyncPlanItem>> BuildSyncPlanAsync(
            List<CloudFolder> checkedFolders,
            List<CloudFolder> mixedFolders,
            string syncFolderPath,
            bool skipMatchingFiles,
            bool verifySha256,
            CancellationToken cancellationToken = default,
            IProgress<SyncPlanProgress>? progress = null)
        {
            var selectedFiles = checkedFolders
                .SelectMany(folder => folder.GetFlatFilesList())
                .Concat(mixedFolders.SelectMany(folder => folder.Files))
                .GroupBy(file => NormalizeCloudPath(file.Path), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            var plan = new List<SyncPlanItem>(selectedFiles.Count);
            for (int index = 0; index < selectedFiles.Count; index++)
            {
                CloudFile cloudFile = selectedFiles[index];
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new SyncPlanProgress(index, selectedFiles.Count, cloudFile.Name));
                string localPath = Utility.GetSafeDownloadPath(syncFolderPath, cloudFile.Path);
                if (!File.Exists(localPath))
                {
                    plan.Add(new SyncPlanItem(
                        cloudFile,
                        SyncDifference.Missing,
                        SyncPlanAction.Download,
                        "File is missing locally",
                        localPath));
                    continue;
                }

                var localFile = new FileInfo(localPath);
                // A legitimate empty cloud file has size 0. Providers that could
                // not obtain metadata explicitly mark the size as unknown.
                bool sameSize = !cloudFile.HasKnownSize || localFile.Length == cloudFile.Size;
                if (!sameSize)
                {
                    plan.Add(new SyncPlanItem(
                        cloudFile,
                        SyncDifference.SizeMismatch,
                        SyncPlanAction.Overwrite,
                        $"Size differs: local {localFile.Length:N0}, cloud {cloudFile.Size:N0} bytes",
                        localPath));
                    continue;
                }

                if (cloudFile.Modified > DateTime.MinValue
                    && cloudFile.Modified.ToUniversalTime() > localFile.LastWriteTimeUtc.AddSeconds(2))
                {
                    plan.Add(new SyncPlanItem(
                        cloudFile,
                        SyncDifference.ModifiedMismatch,
                        SyncPlanAction.Overwrite,
                        $"Cloud file is newer ({cloudFile.Modified:g})",
                        localPath));
                    continue;
                }

                if (verifySha256)
                {
                    bool? verified = await ChecksumManifestStore.Default.VerifyFileAsync(
                        localPath,
                        cancellationToken);
                    if (verified == false)
                    {
                        plan.Add(new SyncPlanItem(
                            cloudFile,
                            SyncDifference.ChecksumMismatch,
                            SyncPlanAction.Overwrite,
                            "Local SHA-256 differs from the stored manifest",
                            localPath));
                        continue;
                    }

                    if (verified == null)
                    {
                        if (skipMatchingFiles)
                            continue;

                        plan.Add(new SyncPlanItem(
                            cloudFile,
                            SyncDifference.Unverified,
                            SyncPlanAction.Overwrite,
                            "Size matches, but no SHA-256 baseline exists yet",
                            localPath));
                        continue;
                    }
                }

                if (skipMatchingFiles)
                    continue;

                plan.Add(new SyncPlanItem(
                    cloudFile,
                    SyncDifference.UpToDate,
                    SyncPlanAction.Overwrite,
                    verifySha256 ? "Size and SHA-256 are valid" : "File size matches",
                    localPath));
            }

            progress?.Report(new SyncPlanProgress(selectedFiles.Count, selectedFiles.Count, string.Empty));

            return plan;
        }

        private static string NormalizeCloudPath(string? path)
        {
            return Uri.UnescapeDataString(path ?? string.Empty)
                .Replace('\\', '/')
                .Trim();
        }

        public async Task<List<CloudFile>> GetMissingFiles(List<CloudFolder> checkedFolders, List<CloudFolder> mixedFolders, string syncFolderPath, bool ignoreExistingFiles, bool validateFileSize)
        {
            ValidateFileSize = validateFileSize;
            
            List<CloudFile> missingFiles = new List<CloudFile>();

            DirectoryInfo syncFolderDirectory = new DirectoryInfo(syncFolderPath);

            foreach (CloudFolder folder in mixedFolders)
            {
                DirectoryInfo di = new DirectoryInfo(syncFolderPath + folder.Path.Replace(@"/", @"\"));
                if (!di.Exists)
                {
                    missingFiles.AddRange(folder.Files);
                    continue;
                }
                FileInfo[] flatSyncFolderFilesList = di.GetFiles("*", SearchOption.TopDirectoryOnly);

                if (!ignoreExistingFiles)
                    missingFiles.AddRange(folder.Files);
                else
                    missingFiles.AddRange(await CompareFilesLists(folder.Files, flatSyncFolderFilesList, syncFolderPath, validateFileSize));
            }

            foreach (CloudFolder folder in checkedFolders)
            {
                DirectoryInfo di = new DirectoryInfo(syncFolderPath + folder.Path.Replace(@"/", @"\"));
                List<CloudFile> flatCloudFolderFilesList = folder.GetFlatFilesList();
                if (!di.Exists)
                {
                    missingFiles.AddRange(folder.GetFlatFilesList());
                    continue;
                }
                FileInfo[] flatSyncFolderFilesList = di.GetFiles("*", SearchOption.AllDirectories);
                if (!ignoreExistingFiles)
                    missingFiles.AddRange(folder.GetFlatFilesList());
                else
                    missingFiles.AddRange(await CompareFilesLists(flatCloudFolderFilesList, flatSyncFolderFilesList, syncFolderPath, validateFileSize));
            }
            return missingFiles;
        }

        async static Task<List<CloudFile>> CompareFilesLists(List<CloudFile> cloudFolderFileList, FileInfo[] syncFolderFileList, string syncFolderPath, bool validateSize)
        {
            List<CloudFile> missingFiles = new List<CloudFile>();

            await Task.Run(() =>
            {
                List<CloudFile> localFiles = syncFolderFileList.ToList().ConvertAll(
                    x => new CloudFile(x.Name, DateTime.Now, DateTime.Now, x.Length)
                    { 
                        Path = "/" + Path.GetRelativePath(syncFolderPath, x.FullName).Replace('\\', '/')
                    });

                missingFiles = cloudFolderFileList.Except(localFiles, new FileComparer() { CompareSize = validateSize }).ToList();
            });

            return missingFiles;
        }

        public async Task LoadMega(string url, IProgress<int>? progress = null)
        {
            var provider = new Providers.MegaPublicFolderProvider();
            CloudPublicFolder = await provider.LoadAsync(
                new Providers.CloudProviderLoadContext(),
                url,
                progress);
            AllFolders = new List<CloudFolder>();
            AddMegaFolders(CloudPublicFolder);
        }

        private void AddMegaFolders(CloudFolder folder)
        {
            AllFolders.Add(folder);
            foreach (CloudFolder child in folder.Subfolders.Cast<CloudFolder>())
                AddMegaFolders(child);
        }

        internal static Uri BuildMegaFileLink(string shareUrl, string nodeId)
            => Providers.MegaPublicFolderProvider.BuildFileLink(shareUrl, nodeId);

        public async Task LoadMega2(List<FogLinkFile> nodes, string originalString) //oldfoglink
        {
            if (nodes == null || nodes.Count == 0)
                throw new InvalidDataException("The FogLink response did not contain any files.");

            int filecount = 0;
            
                await Task.Run(() =>
                {
                    CloudPublicFolder = new CloudFolder(nodes[0].Name, nodes[0].CreationDate, DateTime.MinValue, 0);
                    CloudPublicFolder.Path = "/";
                    CloudPublicFolder.EncryptedUrl = nodes[0].EncryptedLink;
                    CloudPublicFolder.OriginalString = originalString;

                    Dictionary<string, CloudFolder> megaFolders = new Dictionary<string, CloudFolder>();
                    megaFolders.Add(nodes[0].Id, CloudPublicFolder);

                    AllFolders = new List<CloudFolder>() { CloudPublicFolder };

                    foreach (var node in nodes)
                    {
                        if (node.Type == NodeType.Directory)
                        {
                            CloudFolder subfolder = new CloudFolder(node.Name, node.CreationDate, DateTime.MinValue, node.Size2);
                            if (node.ParentId == null || !megaFolders.ContainsKey(node.ParentId))
                                continue;
                            CloudFolder parentFolder = megaFolders[node.ParentId];
                            subfolder.Path = parentFolder.Path + subfolder.Name + "/";
                            subfolder.EncryptedUrl = node.EncryptedLink;
                            megaFolders.Add(node.Id, subfolder);
                            parentFolder.AddSubfolder(subfolder);
                            AllFolders.Add(subfolder);
                            continue;
                        }
                        if (node.Type == NodeType.File)
                        {
                            CloudFile file = new CloudFile(node.Name, node.CreationDate, node.CreationDate, node.Size2);
                            if (node.ParentId == null || !megaFolders.TryGetValue(node.ParentId, out CloudFolder? parentFolder))
                                continue;
                            file.Path = parentFolder.Path + file.Name;
                            file.EncryptedUrl = node.EncryptedLink;
                            parentFolder.SizeTopDirectoryOnly += file.Size;
                            parentFolder.AddFile(file);
                            filecount++;
                            //parentFolder.Files.Add(file);
                        }
                    }

                });
            CloudPublicFolder.CalculateFolderSize();
             
          
        }

        public async Task LoadMega(List<FogLinkFile> nodes, string originalString) //foglink
        {
            if (nodes == null || nodes.Count == 0)
                throw new InvalidDataException("The FogLink response did not contain any files.");

            await Task.Run(() =>
            {             
                CloudPublicFolder = new CloudFolder("MEGA", nodes[0].ModificationDate, nodes[0].ModificationDate, 0);
                CloudPublicFolder.Path = "/";
                if (!nodes[0].IsFile)
                    CloudPublicFolder.EncryptedUrl = nodes[0].EncryptedLink;
                CloudPublicFolder.OriginalString = originalString;

                AllFolders = new List<CloudFolder>() { CloudPublicFolder };

                if (!nodes[0].IsFile)
                    BuildFolderStructure(CloudPublicFolder, nodes[0]);
                else
                {
                    var parentFolder = CloudPublicFolder;
                    var node = nodes[0];
                    CloudFile file = new CloudFile(node.Name, node.ModificationDate, node.ModificationDate, node.Size2);
                    file.Path = parentFolder.Path + file.Name;
                    file.EncryptedUrl = node.EncryptedLink;
                    parentFolder.SizeTopDirectoryOnly += file.Size;
                    parentFolder.AddFile(file);
                }
            });
            CloudPublicFolder.CalculateFolderSize();
        }

        void BuildFolderStructure(CloudFolder parentFolder, FogLinkFile parent)
        {            
            foreach (var node in parent.Children ?? Array.Empty<FogLinkFile>())
            {
                if (node.Type == NodeType.Directory)
                {
                    CloudFolder subfolder = new CloudFolder(node.Name, node.CreationDate, DateTime.MinValue, node.Size2);  
                    subfolder.Path = parentFolder.Path + subfolder.Name + "/";
                    subfolder.EncryptedUrl = node.EncryptedLink;                 
                    parentFolder.AddSubfolder(subfolder);
                    AllFolders.Add(subfolder);
                    BuildFolderStructure(subfolder, node);
                    continue;
                }
                if (node.Type == NodeType.File)
                {
                    CloudFile file = new CloudFile(node.Name, node.CreationDate, node.CreationDate, node.Size2);                    
                    file.Path = parentFolder.Path + file.Name;
                    file.EncryptedUrl = node.EncryptedLink;
                    parentFolder.SizeTopDirectoryOnly += file.Size;
                    parentFolder.AddFile(file);                                  
                }
            }
        }

        public Client webdavClient;
        string allsyncUrl = "https://allsync.com";
        public string allsyncRootFolderAddress = "";
        public Dictionary<string, string> savedPasswords = new Dictionary<string, string>();
        public string folderKey = "", password = "";
        public CloudflareSession? FlareSolverrSession { get; private set; }

        public async Task<bool> PreloadAllsync(string url, bool onlyCheck = false)
        {
            FlareSolverrSession = null;
            UserAgent = "";
            folderKey = "";
            password = "";
            CloudPublicFolder = new CloudFolder("", DateTime.Now, DateTime.Now, 0);
            CloudPublicFolder.OriginalString = url;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var shareUri)
                || (shareUri.Scheme != Uri.UriSchemeHttp && shareUri.Scheme != Uri.UriSchemeHttps))
            {
                MessageBox.Show("Invalid AllSync/Qloud URL.", "Invalid URL", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            var pathSegments = shareUri.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
            var shareMarkerIndex = Array.FindIndex(
                pathSegments,
                segment => segment.Equals("s", StringComparison.OrdinalIgnoreCase));
            if (shareMarkerIndex < 0 || shareMarkerIndex + 1 >= pathSegments.Length)
            {
                MessageBox.Show("The URL does not contain a public share key.", "Invalid URL", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            folderKey = Uri.UnescapeDataString(pathSegments[shareMarkerIndex + 1]);
            allsyncUrl = shareUri.GetLeftPart(UriPartial.Authority);
            allsyncRootFolderAddress = $"{allsyncUrl}/s/{Uri.EscapeDataString(folderKey)}?path=";

            var requestedPath = HttpUtility.ParseQueryString(shareUri.Query)["path"];
            requestedPath = HttpUtility.UrlDecode(requestedPath ?? "/")
                .Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(requestedPath))
                requestedPath = "/";
            if (!requestedPath.StartsWith('/'))
                requestedPath = "/" + requestedPath;
            if (!requestedPath.EndsWith('/'))
                requestedPath += "/";

            CloudPublicFolder.Path = requestedPath;
            CloudPublicFolder.Name = requestedPath == "/"
                ? ""
                : requestedPath.TrimEnd('/').Split('/').Last();

            WriteToLog($"\n{DateTime.Now}\n Searching for password for {folderKey} \n\n");
            if (savedPasswords.ContainsKey(folderKey))
            {
                password = savedPasswords[folderKey];
                WriteToLog($"\n{DateTime.Now}\n Found a saved password for {folderKey}\n\n");

            }

            if (Properties.Settings.Default.flareSolverrEnabled)
            {
                try
                {
                    var timeoutSeconds = Math.Clamp(
                        Properties.Settings.Default.flareSolverrTimeoutSeconds, 10, 300);
                    using var flareSolverr = new FlareSolverrClient(
                        Properties.Settings.Default.flareSolverrUrl,
                        TimeSpan.FromSeconds(timeoutSeconds));
                    FlareSolverrSession = await flareSolverr.GetSessionAsync(
                        shareUri, CancellationToken.None);
                    UserAgent = FlareSolverrSession.UserAgent;
                    WriteToLog($"\n{DateTime.Now}\n FlareSolverr session ready ({FlareSolverrSession.Cookies.Count} cookies)\n\n", true);
                }
                catch (FlareSolverrException ex)
                {
                    MessageBox.Show(
                        ex.Message + "\n\nStart FlareSolverr or disable it in Sync Settings to use direct mode.",
                        "FlareSolverr connection failed",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    WriteToLog($"\n{DateTime.Now}\n FlareSolverr error: {ex.Message}\n\n", true);
                    return false;
                }
            }
            CreateUpdateWebdavClient(folderKey, password);              

            if (onlyCheck)
            {
                var success = await CheckAllsyncFolder();
                WriteToLog($"\n{DateTime.Now}\n Storing password for {folderKey}\n\n");
                WebdavCredential = new NetworkCredential { UserName = folderKey, Password = password };
                return success;
            }     
            return true;
        }

        public async Task<int> LoadAllsync(string folderKey, string password = "", IProgress<int>? progress = null)
        {
            WebDAVClient.Model.Item[] items;
            try
            {
                //ServicePointManager.Expect100Continue = true;
                //ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
                // Skip validation of SSL/TLS certificate
                //ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };

                if (password != "")
                    CreateUpdateWebdavClient(folderKey, password);
                else
                    CreateUpdateWebdavClient(folderKey, "null");

                items = (await webdavClient.ListShared(CloudPublicFolder.Path, 999))?.ToArray()
                    ?? Array.Empty<WebDAVClient.Model.Item>();
                if (items.Length == 0)
                {
                    progress?.Report(1);
                    return 0;
                }
            }
            catch (WebDAVClient.Helpers.WebDAVException ex)
            {
                return ex.GetHttpCode();                
            }            
            catch (Exception ex)
            {
                WriteToLog($"\n{DateTime.Now:O}\nUnable to list AllSync share: {ex}\n", true);
                MessageBox.Show("Bad url or no connection");
                return 0;
            }

            password = webdavClient.Credentials.Password;
            if (savedPasswords.ContainsKey(folderKey))
                savedPasswords[folderKey] = password;
            else
                savedPasswords.Add(folderKey, password);
            Properties.Settings.Default.savedPasswordsJson = JsonConvert.SerializeObject(savedPasswords);
            Properties.Settings.Default.Save();

            WebdavCredential = new NetworkCredential { UserName = folderKey, Password = password };
            WriteToLog($"\n{DateTime.Now}\n Stored password for {folderKey}\n\n");

            List<CloudFolder> allFolders = new List<CloudFolder> { CloudPublicFolder };
            AllFolders = new List<CloudFolder>() { CloudPublicFolder };
            foreach (var item in items)
            {
                if (item.IsCollection)
                {
                    string path = HttpUtility.UrlDecode(item.Href).Replace("/public.php/webdav", "");                    
                    if (!allFolders.ConvertAll(x => x.Path).Contains(path))
                    {
                        CloudFolder newFolder = new CloudFolder(item.DisplayName, DateTime.MinValue, item.LastModified ?? DateTime.MinValue, 0);
                        newFolder.Path = path;
                        allFolders.Add(newFolder);
                        AllFolders.Add(newFolder);
                    }
                }
            }
            try
            {
                foreach (var folder in allFolders)
                {
                    if (folder.Path == CloudPublicFolder.Path || folder.Path == "")
                        continue;
                    string parentFolderPath = GetParentCloudPath(folder.Path);
                    CloudFolder? parentFolder = allFolders.Find(x => x.Path == parentFolderPath);
                    if (parentFolder == null)
                        throw new InvalidDataException($"Unable to find parent folder for {folder.Path}.");
                    parentFolder.AddSubfolder(folder);
                }
            }
            catch (Exception ex)
            {
                WriteToLog($"\n{DateTime.Now:O}\nUnable to build AllSync folder structure: {ex}\n", true);
                MessageBox.Show("Error during folder structure building.");
                return 9999;
            }

            foreach (var item in items)
            {
                if (!item.IsCollection)
                {
                    string encodedPath = item.Href.Replace("/public.php/webdav", "/download?path=");
                    string path = HttpUtility.UrlDecode(item.Href).Replace("/public.php/webdav", "");
                    string parentFolderPath = GetParentCloudPath(path);
                    string url = allsyncRootFolderAddress.Replace("?path=", "") + encodedPath;
                    CloudFile file = new CloudFile(
                            item.DisplayName,
                            DateTime.MinValue,
                            item.LastModified ?? DateTime.MinValue,
                            item.ContentLength ?? 0
                            );
                    file.Path = path;
                    file.PublicUrl = new Uri(url);
                    //file.PublicUrl = (webdavClient as Client).GetServerUrl(path, false).Result.Uri;

                    CloudFolder? parentFolder;
                    if (parentFolderPath != "")
                        parentFolder = allFolders.Find(x => x.Path == parentFolderPath);
                    else
                        parentFolder = CloudPublicFolder;
                    if (parentFolder == null)
                        throw new InvalidDataException($"Unable to find parent folder for {path}.");

                    parentFolder.AddFile(file);
                    parentFolder.SizeTopDirectoryOnly += file.Size;
                }
            }
            //Array.Clear(items);
            //items = null;
            //GC.Collect();
            if (CloudPublicFolder.Subfolders.Count == 0 && CloudPublicFolder.Files.Count == 0)
                return 9999;

            CloudPublicFolder.CalculateFolderSize();
            return 200;
        }

        private static string GetParentCloudPath(string path)
        {
            string normalized = (path ?? string.Empty).Replace('\\', '/').TrimEnd('/');
            int separatorIndex = normalized.LastIndexOf('/');
            return separatorIndex < 0 ? string.Empty : normalized[..(separatorIndex + 1)];
        }

        async Task<bool> CheckAllsyncFolder()
        {           
            try
            {
                var items = await webdavClient.ListShared(CloudPublicFolder.Path, 1);
                if (items.Count() > 0)
                    return true;                
            }
            catch (Exception ex)
            {
                WriteToLog($"\n{DateTime.Now:O}\nUnable to check AllSync share: {ex}\n", true);
                MessageBox.Show("Cannot retrieve data from URL. The link may be unavailable.");
                return false;
            }
            return false;
        }

        public void WriteToLog(string message, bool force = false)
        {
            if (debugMode || force)
            {
                try
                {
                    var logFileName = $"download-log-{DateTime.Now:MM-dd-yyyy}.txt";
                    File.AppendAllText(logFileName, message);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Debug.WriteLine($"Unable to write application log: {ex.Message}");
                }
            }
        }

        public NetworkCredential? WebdavCredential;

        void CreateUpdateWebdavClient(string folderKey, string password = "")
        {
            NetworkCredential webdavCredential = new NetworkCredential { UserName = folderKey, Password = password };

            webdavClient = NetworkClientAdapters.CreateWebDavClient(webdavCredential);
            webdavClient.Server = allsyncUrl;
            webdavClient.BasePath = $"/public.php/webdav/";
            Dictionary<string, string> customHeaders = new Dictionary<string, string>();
            customHeaders.Add("X-Requested-With", "XMLHttpRequest");
            //customHeaders.Add("Accept-Encoding", "gzip, deflate, br, zstd");
            customHeaders.Add("Accept", @"*/*");
            customHeaders.Add("Accept-Language", "en-US,en;q=0.5");

            if (FlareSolverrSession != null)
            {
                var cookieHeader = FlareSolverrSession.GetCookieHeader(new Uri(allsyncUrl));
                if (!string.IsNullOrWhiteSpace(cookieHeader))
                    customHeaders.Add("Cookie", cookieHeader);
            }

            webdavClient.UserAgent = string.IsNullOrWhiteSpace(UserAgent)
                ? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CloudFolderBrowser/1.0"
                : UserAgent;
           
            webdavClient.CustomHeaders = customHeaders;           
        }
    }

    class FileComparer : IEqualityComparer<CloudFile>
    {
        public bool CompareSize = false;
        public int ErrorMargin = 1;
        public bool Equals(CloudFile? x, CloudFile? y)
        {
            if (ReferenceEquals(x, y))
                return true;
            if (x == null || y == null)
                return false;

            var a = NormalizePath(x.Path);
            var b = NormalizePath(y.Path);

            if(CompareSize)
            {
                bool correctSize = !x.HasKnownSize || !y.HasKnownSize || Math.Abs(x.Size - y.Size) <= 10;
                return StringComparer.OrdinalIgnoreCase.Equals(a, b) && correctSize;
            }

            return StringComparer.OrdinalIgnoreCase.Equals(a, b);
        }

        public int GetHashCode(CloudFile x)
        {
            return StringComparer.OrdinalIgnoreCase.GetHashCode(NormalizePath(x.Path));
        }

        private static string NormalizePath(string? path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;

            return Uri.UnescapeDataString(path)
                .Replace('\\', '/')
                .Trim();
        }
    }
}
