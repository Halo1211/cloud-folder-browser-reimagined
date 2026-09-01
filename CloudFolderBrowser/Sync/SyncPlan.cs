using Newtonsoft.Json;

namespace CloudFolderBrowser.Sync;

public enum SyncPlanAction
{
    Download,
    Overwrite,
    Skip,
    Rename
}

public enum SyncDifference
{
    Missing,
    UpToDate,
    SizeMismatch,
    ModifiedMismatch,
    ChecksumMismatch,
    Unverified
}

public readonly record struct SyncPlanProgress(int Processed, int Total, string CurrentFile);

public sealed class SyncPlanItem
{
    public CloudFile File { get; }
    public SyncDifference Difference { get; }
    public string Reason { get; }
    public string ExistingPath { get; }
    public SyncPlanAction Action { get; set; }
    public string TargetName { get; set; }

    public SyncPlanItem(
        CloudFile file,
        SyncDifference difference,
        SyncPlanAction action,
        string reason,
        string existingPath)
    {
        File = file;
        Difference = difference;
        Action = action;
        Reason = reason;
        ExistingPath = existingPath;
        TargetName = file.Name;
    }

    public CloudFile Apply()
    {
        File.PlannedAction = Action;
        File.LocalNameOverride = Action == SyncPlanAction.Rename
            ? GetSafeTargetName()
            : string.Empty;
        return File;
    }

    public string GetSafeTargetName()
    {
        return Utility.GetSafePathName(TargetName);
    }

    public string GetTargetCloudPath()
    {
        string path = Action == SyncPlanAction.Rename
            ? Utility.ApplyFileNameOverride(File.Path, GetSafeTargetName())
            : File.Path;
        return path.Replace('\\', '/');
    }
}
