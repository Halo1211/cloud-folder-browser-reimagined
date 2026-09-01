using System.Reflection;

namespace CloudFolderBrowser.Branding;

internal static class ApplicationBrand
{
    public const string ProductName = "Cloud Folder Browser Reimagined";
    public const string RepositoryUrl = "https://github.com/Halo1211/cloud-folder-browser-reimagined";
    public const string LatestReleaseUrl = RepositoryUrl + "/releases/latest";

    public static Icon CreateIcon()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "CloudFolderBrowser.Assets.AppIcon.ico")
            ?? throw new InvalidOperationException("The application icon resource is missing.");
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }
}
