using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using HtmlAgilityPack;
using CloudFolderBrowser.Providers;

namespace CloudFolderBrowser
{
    internal static class Utility
    {
        public static string GetApplicationDataDirectory()
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CloudFolderBrowser");
            Directory.CreateDirectory(directory);
            return directory;
        }

        static public string GetSafePathName(string name)
        {
            string safeName = name ?? string.Empty;
            foreach (var ch in Path.GetInvalidFileNameChars())
                safeName = safeName.Replace(ch, '_');           
            safeName = safeName.TrimEnd(' ', '.');

            if (string.IsNullOrWhiteSpace(safeName))
                safeName = "_";

            string baseName = safeName.Split('.')[0];
            string[] reservedNames =
            {
                "CON", "PRN", "AUX", "NUL",
                "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
            };
            if (reservedNames.Contains(baseName, StringComparer.OrdinalIgnoreCase))
                safeName = "_" + safeName;

            return safeName;
        }

        public static string GetSafeDownloadPath(string rootPath, string cloudPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
                throw new ArgumentException("A download root folder is required.", nameof(rootPath));
            if (string.IsNullOrWhiteSpace(cloudPath))
                throw new ArgumentException("A cloud file path is required.", nameof(cloudPath));

            string rootFullPath = Path.GetFullPath(rootPath);
            string[] segments = cloudPath
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
                throw new InvalidDataException($"Unsafe cloud file path: {cloudPath}");

            string relativePath = Path.Combine(segments
                .Select(segment => GetSafePathName(Uri.UnescapeDataString(segment)))
                .ToArray());
            string destination = Path.GetFullPath(Path.Combine(rootFullPath, relativePath));
            string rootPrefix = rootFullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Cloud file path escapes the download folder: {cloudPath}");

            return destination;
        }

        public static string ApplyFileNameOverride(string cloudPath, string? fileNameOverride)
        {
            if (string.IsNullOrWhiteSpace(fileNameOverride))
                return cloudPath;

            string normalized = cloudPath.Replace('\\', '/');
            int separator = normalized.LastIndexOf('/');
            return separator >= 0
                ? normalized[..(separator + 1)] + GetSafePathName(fileNameOverride)
                : GetSafePathName(fileNameOverride);
        }

        static public string[] ParsePath(string path, bool includeFilename = false)
        {
            string pattern = @"([^/]+)/";
            if (includeFilename) pattern = @"([^/]+)";

            MatchCollection matches = Regex.Matches(path, pattern);
            if (matches.Count == 0)
                return Array.Empty<string>();
            string[] folderNames = new string[matches.Count];

            for (int i = 0; i < folderNames.Length; i++)
            {
                folderNames[i] = matches[i].Groups[1].Value;
            }
            return folderNames;
        }

        public static bool IsBase64String(this string s)
        {
            s = s.Trim();
            return (s.Length % 4 == 0) && Regex.IsMatch(s, @"^[a-zA-Z0-9\+/]*={0,3}$", RegexOptions.None);

        }

        public static async Task<string?> GetFinalRedirect(string url, string userAgent)
        {
            if (string.IsNullOrWhiteSpace(url))
                return url;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var currentUri))
                return null;

            using var webRequestHandler = Networking.AppHttpClientFactory.CreateHandler(
                allowAutoRedirect: false,
                routeKey: "Other");
            using var httpClient = new HttpClient(webRequestHandler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                string.IsNullOrWhiteSpace(userAgent) ? "CloudFolderBrowser/1.0" : userAgent);

            const int maxRedirects = 5;
            for (int redirectCount = 0; redirectCount <= maxRedirects; redirectCount++)
            {
                try
                {
                    using HttpResponseMessage responseMessage = await httpClient.GetAsync(
                        currentUri,
                        HttpCompletionOption.ResponseHeadersRead);

                    switch (responseMessage.StatusCode)
                    {
                        case HttpStatusCode.OK:
                            return currentUri.AbsoluteUri;
                        case HttpStatusCode.Redirect:
                        case HttpStatusCode.MovedPermanently:
                        case HttpStatusCode.RedirectKeepVerb:
                        case HttpStatusCode.RedirectMethod:
                            Uri? location = responseMessage.Headers.Location;
                            if (location == null)
                                return currentUri.AbsoluteUri;
                            currentUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                            break;
                        default:
                            return currentUri.AbsoluteUri;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    return null;
                }
            }

            return currentUri.AbsoluteUri;
        }

        static byte[] GetHash(string inputString)
        {
            using (HashAlgorithm algorithm = SHA256.Create())
                return algorithm.ComputeHash(Encoding.UTF8.GetBytes(inputString));
        }

        public static string GetLastId(string url)
        {
            Regex uriRegex = new Regex(@"/(?<type>(file|folder))/(?<id>[^#]+)#(?<key>[^$/]+)(/folder/)?(?<lastid>[^/]+)?");
            Match match = uriRegex.Match(url);
            if (match.Success == false)
            {
                throw new ArgumentException(string.Format("Invalid uri. Unable to extract Id and Key from the uri {0}", url));
            }
            string lastId = match.Groups["lastid"].Value;
            return lastId;

        }


        public static string GetHashString(string inputString)
        {
            StringBuilder sb = new StringBuilder();
            foreach (byte b in GetHash(inputString))
                sb.Append(b.ToString("X2"));

            return sb.ToString();
        }

        public static CloudServiceType GetCloudServiceType(string url)
        {
            ICloudProviderDescriptor? provider = CloudProviderRegistry.Default.Resolve(url);
            if (provider != null)
                return provider.ServiceType;

            //using (var webpage = new WebClient())
            //{
            //    webpage.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0 (Windows NT 6.1; Win64; x64; rv:59.0) Gecko/20100101 Firefox/59.0";
            //    var data = webpage.DownloadString(url);
            //    HtmlWeb web = new HtmlWeb();
            //    HtmlAgilityPack.HtmlDocument htmlDoc = new HtmlAgilityPack.HtmlDocument();
            //    htmlDoc.LoadHtml(data);
            //    HtmlNode mdnode = htmlDoc.DocumentNode.SelectSingleNode("//meta[@name='description']");
            //    if (mdnode != null)
            //    {
            //        HtmlAttribute desc;
            //        desc = mdnode.Attributes["content"];
            //        string fulldescription = desc.Value;
            //        if (fulldescription.ToLower().Contains("powered by h5ai"))
            //            return CloudServiceType.h5ai;
            //    }
            //}

            return CloudServiceType.Other;
        }       
    }
}
