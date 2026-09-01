[CmdletBinding()]
param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repositoryRoot 'CloudFolderBrowser\CloudFolderBrowser.csproj'
$solutionPath = Join-Path $repositoryRoot 'CloudFolderBrowser.sln'
$testProjectPath = Join-Path $repositoryRoot 'CloudFolderBrowser.Tests\CloudFolderBrowser.Tests.csproj'
$releaseDirectory = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'release'))
$buildDirectory = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot '.build\release\win-x64'))
$packageDirectory = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot '.build\package'))

if ([System.IO.Path]::GetDirectoryName($releaseDirectory) -ne $repositoryRoot) {
    throw "Refusing to clean an unexpected release directory: $releaseDirectory"
}

[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$versionPropertyGroup = $project.Project.PropertyGroup | Where-Object { $null -ne $_.Version } | Select-Object -First 1
$version = ([string]$versionPropertyGroup.Version).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "The project version '$version' is not a three-part semantic version."
}

[System.IO.Directory]::CreateDirectory($releaseDirectory) | Out-Null
Get-ChildItem -LiteralPath $releaseDirectory -Force |
    Where-Object Name -ne 'README.md' |
    Remove-Item -Recurse -Force

foreach ($path in @($buildDirectory, $packageDirectory)) {
    if (Test-Path -LiteralPath $path) {
        $resolved = [System.IO.Path]::GetFullPath($path)
        if (-not $resolved.StartsWith((Join-Path $repositoryRoot '.build'), [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean an unexpected build directory: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    [System.IO.Directory]::CreateDirectory($path) | Out-Null
}

Push-Location $repositoryRoot
try {
    dotnet restore $solutionPath
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

    dotnet restore $projectPath -r win-x64
    if ($LASTEXITCODE -ne 0) { throw 'The Windows x64 runtime restore failed.' }

    if (-not $SkipTests) {
        dotnet test $testProjectPath -c Release --no-restore -p:TreatWarningsAsErrors=true
        if ($LASTEXITCODE -ne 0) { throw 'The release test run failed.' }
    }

    dotnet publish $projectPath `
        -c Release `
        -r win-x64 `
        --self-contained true `
        --no-restore `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishTrimmed=false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:TreatWarningsAsErrors=true `
        -o $buildDirectory
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
}
finally {
    Pop-Location
}

$publishedExecutable = Join-Path $buildDirectory 'CloudFolderBrowser.exe'
if (-not (Test-Path -LiteralPath $publishedExecutable)) {
    throw "The published executable was not found at $publishedExecutable"
}

$releaseExecutable = Join-Path $releaseDirectory 'CloudFolderBrowser.exe'
[System.IO.File]::Copy($publishedExecutable, $releaseExecutable, $true)
[System.IO.File]::Copy($publishedExecutable, (Join-Path $packageDirectory 'CloudFolderBrowser.exe'), $true)

$publishedConfiguration = Join-Path $buildDirectory 'CloudFolderBrowser.dll.config'
if (Test-Path -LiteralPath $publishedConfiguration) {
    [System.IO.File]::Copy($publishedConfiguration, (Join-Path $releaseDirectory 'CloudFolderBrowser.dll.config'), $true)
    [System.IO.File]::Copy($publishedConfiguration, (Join-Path $packageDirectory 'CloudFolderBrowser.dll.config'), $true)
}

$archiveName = "CloudFolderBrowser-Reimagined-v$version-win-x64.zip"
$archivePath = Join-Path $releaseDirectory $archiveName
Compress-Archive -Path (Join-Path $packageDirectory '*') -DestinationPath $archivePath -CompressionLevel Optimal

$checksumFiles = @($releaseExecutable, $archivePath)
$checksumLines = foreach ($file in $checksumFiles) {
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([System.IO.Path]::GetFileName($file))"
}
[System.IO.File]::WriteAllLines((Join-Path $releaseDirectory 'SHA256SUMS.txt'), $checksumLines)

Write-Host "Release v$version created in $releaseDirectory"
Get-ChildItem -LiteralPath $releaseDirectory -File | Select-Object Name, Length
