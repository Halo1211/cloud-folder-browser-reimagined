# Contributing

Thank you for improving Cloud Folder Browser Reimagined. Bug fixes, provider compatibility updates, tests, and focused interface improvements are welcome.

## Development setup

Install the .NET SDK selected by `global.json`, then run:

```powershell
dotnet restore CloudFolderBrowser.sln
dotnet build CloudFolderBrowser.sln -c Release --no-restore
dotnet test CloudFolderBrowser.Tests/CloudFolderBrowser.Tests.csproj -c Release --no-build
```

The project targets Windows because the desktop application uses Windows Forms and Windows user-scoped data protection.

## Pull requests

- Keep each pull request focused on one problem.
- Add or update tests when changing transfer, provider, account, sync, or network behavior.
- Do not commit access tokens, passwords, `portable.config`, crash dumps, download logs, or build output.
- Preserve compatibility with saved settings and download history unless the change includes a documented migration.
- Run the full test suite before opening the pull request.

## Provider plug-ins

New out-of-tree providers should implement `ICloudFolderProviderV2`, avoid UI dependencies, and use the host-provided network configuration where possible. Provider DLLs run in-process and must not treat the load context as a security boundary.

## Branding assets

Run `./tools/GenerateBrandAssets.ps1` to regenerate the PNG, multi-size Windows icon, and animated loading indicator. The script requires `ffmpeg` on `PATH` only for GIF generation.
