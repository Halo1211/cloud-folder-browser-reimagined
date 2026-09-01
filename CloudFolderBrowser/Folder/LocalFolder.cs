using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CloudFolderBrowser
{
    public class LocalFolder : Folder
    {
        public List<FileInfo> Files { get; set; }

        public LocalFolder() { }

        public LocalFolder(DirectoryInfo di, CancellationToken cancellationToken = default)
            : this(di, new HashSet<string>(StringComparer.OrdinalIgnoreCase), cancellationToken)
        {
        }

        private LocalFolder(
            DirectoryInfo di,
            HashSet<string> visitedDirectories,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Name = di.Name;
            Path = di.FullName + @"\";
            Modified = di.LastWriteTime;
            Created = di.CreationTime;
            Subfolders = new List<IFolder>();
            Files = new List<FileInfo>();

            string canonicalPath = System.IO.Path.GetFullPath(di.FullName)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar);
            if (!visitedDirectories.Add(canonicalPath))
                return;

            foreach (DirectoryInfo subdi in EnumerateDirectoriesSafely(di))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (subdi.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        continue;
                    Subfolders.Add(new LocalFolder(subdi, visitedDirectories, cancellationToken));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    System.Diagnostics.Debug.WriteLine($"Skipping local folder {subdi.FullName}: {ex.Message}");
                }
            }
            foreach (FileInfo file in EnumerateFilesSafely(di))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Files.Add(file);
                SizeTopDirectoryOnly += file.Length;
            }
        }

        private static IEnumerable<DirectoryInfo> EnumerateDirectoriesSafely(DirectoryInfo directory)
        {
            try
            {
                return directory.EnumerateDirectories().ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"Unable to enumerate local folder {directory.FullName}: {ex.Message}");
                return Array.Empty<DirectoryInfo>();
            }
        }

        private static IEnumerable<FileInfo> EnumerateFilesSafely(DirectoryInfo directory)
        {
            try
            {
                return directory.EnumerateFiles().ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"Unable to enumerate files in {directory.FullName}: {ex.Message}");
                return Array.Empty<FileInfo>();
            }
        }

        public FileInfo[] GetFiles()
        {
            DirectoryInfo di = new DirectoryInfo(Path);
            return di.GetFiles("*", SearchOption.AllDirectories);
        }
    }

}
