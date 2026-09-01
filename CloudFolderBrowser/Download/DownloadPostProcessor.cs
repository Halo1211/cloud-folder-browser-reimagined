using System.Diagnostics;
using System.IO.Compression;
using CloudFolderBrowser.Networking;

namespace CloudFolderBrowser;

internal sealed record PostProcessingResult(bool Processed, string Message)
{
    public static PostProcessingResult None { get; } = new(false, string.Empty);
}

internal sealed class DownloadPostProcessor
{
    private static readonly HashSet<string> ArchiveExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".zip", ".7z", ".rar" };

    public async Task<PostProcessingResult> ProcessAsync(
        string downloadedPath,
        CancellationToken cancellationToken)
    {
        if (!Properties.Settings.Default.archivePostProcessingEnabled)
            return PostProcessingResult.None;

        string extension = Path.GetExtension(downloadedPath);
        if (Properties.Settings.Default.par2RepairEnabled)
        {
            string? par2 = FindPar2(downloadedPath);
            if (par2 != null)
                await RunPar2Async(par2, cancellationToken).ConfigureAwait(false);
        }

        if (!ArchiveExtensions.Contains(extension))
            return extension.Equals(".par2", StringComparison.OrdinalIgnoreCase)
                ? new PostProcessingResult(true, "PAR2 recovery completed")
                : PostProcessingResult.None;

        string destination = Path.Combine(
            Path.GetDirectoryName(downloadedPath)!,
            Utility.GetSafePathName(Path.GetFileNameWithoutExtension(downloadedPath)));
        Directory.CreateDirectory(destination);

        string tool = Properties.Settings.Default.archiveToolPath?.Trim() ?? string.Empty;
        string password = ProxySecretProtector.Unprotect(
            Properties.Settings.Default.protectedArchivePassword);
        if (!string.IsNullOrWhiteSpace(tool))
        {
            await RunSevenZipAsync(tool, downloadedPath, destination, password, cancellationToken)
                .ConfigureAwait(false);
            return new PostProcessingResult(true, $"Archive extracted to {destination}");
        }

        if (!extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return new PostProcessingResult(
                false,
                "Archive extraction skipped: configure a 7-Zip executable for RAR/7z files.");
        }
        if (!string.IsNullOrEmpty(password))
        {
            return new PostProcessingResult(
                false,
                "Password-protected ZIP extraction requires a configured 7-Zip executable.");
        }

        await ExtractZipSafelyAsync(downloadedPath, destination, cancellationToken)
            .ConfigureAwait(false);
        return new PostProcessingResult(true, $"Archive extracted to {destination}");
    }

    private static string? FindPar2(string downloadedPath)
    {
        if (Path.GetExtension(downloadedPath).Equals(".par2", StringComparison.OrdinalIgnoreCase))
            return downloadedPath;
        string directory = Path.GetDirectoryName(downloadedPath)!;
        string baseName = Path.GetFileNameWithoutExtension(downloadedPath);
        return Directory.EnumerateFiles(directory, "*.par2", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => Path.GetFileNameWithoutExtension(path)
                .StartsWith(baseName, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task ExtractZipSafelyAsync(
        string archivePath,
        string destination,
        CancellationToken cancellationToken)
    {
        string destinationRoot = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        await Task.Run(() =>
        {
            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
                if (!target.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Archive entry would escape the extraction directory.");
                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RunSevenZipAsync(
        string tool,
        string archivePath,
        string destination,
        string password,
        CancellationToken cancellationToken)
    {
        ValidateTool(tool, "7-Zip");
        var start = new ProcessStartInfo
        {
            FileName = tool,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = !string.IsNullOrEmpty(password)
        };
        start.ArgumentList.Add("x");
        start.ArgumentList.Add(archivePath);
        start.ArgumentList.Add("-y");
        start.ArgumentList.Add("-o" + destination);
        await RunProcessAsync(start, "7-Zip extraction", cancellationToken, password)
            .ConfigureAwait(false);
    }

    private static async Task RunPar2Async(string par2Path, CancellationToken cancellationToken)
    {
        string tool = Properties.Settings.Default.par2ToolPath?.Trim() ?? string.Empty;
        ValidateTool(tool, "PAR2");
        var start = new ProcessStartInfo
        {
            FileName = tool,
            WorkingDirectory = Path.GetDirectoryName(par2Path)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("r");
        start.ArgumentList.Add(par2Path);
        await RunProcessAsync(start, "PAR2 repair", cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateTool(string path, string displayName)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException($"{displayName} executable is not configured or was not found.", path);
    }

    private static async Task RunProcessAsync(
        ProcessStartInfo start,
        string operation,
        CancellationToken cancellationToken,
        string standardInput = "")
    {
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException($"Unable to start {operation}.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        if (start.RedirectStandardInput)
        {
            await process.StandardInput.WriteLineAsync(standardInput.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            process.StandardInput.Close();
        }
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
        string output = await stdout.ConfigureAwait(false);
        string error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            string detail = string.IsNullOrWhiteSpace(error) ? output : error;
            if (detail.Length > 500)
                detail = detail[..500] + "…";
            throw new InvalidDataException($"{operation} failed with exit code {process.ExitCode}: {detail}");
        }
    }
}
