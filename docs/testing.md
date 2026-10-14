# Testing and known limits

## Run the checks

Run on Windows with the SDK selected by `global.json`:

```powershell
dotnet restore CloudFolderBrowser.sln
dotnet build CloudFolderBrowser.sln -c Release --no-restore -p:TreatWarningsAsErrors=true
dotnet test CloudFolderBrowser.Tests/CloudFolderBrowser.Tests.csproj -c Release --no-build --no-restore
```

The v1.0.4 suite contains 469 test cases. Tests use temporary files, local HTTP
servers, Windows data protection, and STA WinForms fixtures. Cloud credentials
are not required.

The suite covers transfers and resume, segment assembly, checksums, routing,
provider parsing, Windows path validation, synchronization, persisted data,
encrypted backups, DNS/proxy configuration, UI workflows, and archive exports.

Generate local UI snapshots with:

```powershell
dotnet run --project tools/UiSnapshots/UiSnapshots.csproj -c Release -- artifacts/ui-snapshots
```

## Known limits

- Live OAuth, MEGA sessions, every cloud endpoint, real proxies, browser
  challenges, and external tools are not all tested end-to-end.
- UI fixtures do not cover every display scale, resolution, or interaction.
- Single-stream resume cannot prove representation identity across sessions
  when no remote version validator has been retained.
- Filesystem checks reject existing junctions and symbolic links; they do not
  provide an OS-enforced defense against another process racing to change paths.
- Provider plug-ins execute inside the application and are not sandboxed.

Passing tests do not establish that every provider is available or that the
application has no remaining defects.
