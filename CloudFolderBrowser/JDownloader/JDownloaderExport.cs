using System.IO.Compression;
using Newtonsoft.Json;

namespace CloudFolderBrowser.JDownloader;

internal static class JDownloaderExport
{
    internal static string CreateArchive(IReadOnlyList<CloudFile> files, string folderName, string applicationDirectory,
        Func<CloudFile, JDLink> createLink)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(createLink);
        string destination = Path.Combine(Path.GetFullPath(applicationDirectory), "linkcontainers", Utility.GetSafePathName(folderName));
        Directory.CreateDirectory(destination);
        string archivePath = Path.Combine(destination, "linkcollector" + Guid.NewGuid().ToString("N") + ".zip");
        string temporaryPath = archivePath + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var packages = new Dictionary<string, JDPackage>(StringComparer.Ordinal);
                foreach (CloudFile file in files)
                {
                    string folder = Path.GetDirectoryName(file.Path) ?? string.Empty;
                    if (!packages.TryGetValue(folder, out JDPackage? package))
                    {
                        package = new JDPackage(folder, folder) { numberId = packages.Count.ToString("D3"), downloadFolder = folder };
                        packages.Add(folder, package);
                        WriteEntry(archive, package.numberId, package);
                    }
                    WriteEntry(archive, package.numberId + "_" + package.linksCount.ToString("D3"), createLink(file));
                    package.linksCount++;
                }
            }
            File.Move(temporaryPath, archivePath);
            return archivePath;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void WriteEntry(ZipArchive archive, string name, object value)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(JsonConvert.SerializeObject(value));
    }
}
