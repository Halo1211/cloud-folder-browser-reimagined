[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
$repositoryPrefix = $repositoryRoot + [System.IO.Path]::DirectorySeparatorChar

$relativeDirectories = @(
    'artifacts',
    '.build',
    'Aga.Controls\bin',
    'Aga.Controls\obj',
    'CloudFolderBrowser\bin',
    'CloudFolderBrowser\obj',
    'CloudFolderBrowser.Tests\bin',
    'CloudFolderBrowser.Tests\obj',
    'MegaApiClient\bin',
    'MegaApiClient\obj',
    'WebDAVClient\bin',
    'WebDAVClient\obj',
    'YandexDiskSharp\bin',
    'YandexDiskSharp\obj',
    'tools\UiSnapshots\bin',
    'tools\UiSnapshots\obj'
)

foreach ($relativeDirectory in $relativeDirectories) {
    $target = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $relativeDirectory))
    if (-not $target.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a directory outside the repository: $target"
    }

    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force
        Write-Host "Removed $relativeDirectory"
    }
}

$localProjectFile = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'CloudFolderBrowser\CloudFolderBrowser.csproj.user'))
if (Test-Path -LiteralPath $localProjectFile) {
    Remove-Item -LiteralPath $localProjectFile -Force
    Write-Host 'Removed CloudFolderBrowser\CloudFolderBrowser.csproj.user'
}

Write-Host 'Repository build and diagnostic output has been cleaned.'
