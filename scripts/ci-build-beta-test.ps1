#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$InputBundle,
    [Parameter(Mandatory = $true)] [string]$BetaAssetsPath,
    [Parameter(Mandatory = $true)] [string]$OutputRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$profile = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release-profiles/Beta.json') -Raw | ConvertFrom-Json
if ([string]$profile.PackageName -cne 'WinoMail.Beta') { throw "Unexpected Beta package identity: $($profile.PackageName)" }
if ([string]::IsNullOrWhiteSpace([string]$profile.Publisher)) { throw 'Beta publisher identity is missing.' }

$manifestPath = Join-Path $PSScriptRoot '../src/Wino.Mail.WinUI/Package.appxmanifest'
$sourceManifest = [xml](Get-Content -LiteralPath $manifestPath -Raw)
$version = [string]$sourceManifest.Package.Identity.Version
if ($version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)\.0$') { throw "Invalid application version: $version" }

function Get-ArchiveXml {
    param([string]$Path, [string]$EntryName)
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $archive.GetEntry($EntryName)
        if ($null -eq $entry) { throw "Package is missing ${EntryName}: $Path" }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { return [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally { $archive.Dispose() }
}

function Get-PriCandidates {
    param([xml]$Dump, [string]$Layout)
    $items = [Collections.Generic.List[string]]::new()
    foreach ($resource in $Dump.SelectNodes("//*[local-name()='NamedResource']")) {
        $resourceName = ([string]$resource.GetAttribute('uri')) -replace '^ms-resource://[^/]+/', ''
        foreach ($candidate in $resource.SelectNodes("*[local-name()='Candidate']")) {
            $value = $candidate.SelectSingleNode("*[local-name()='Value' or local-name()='Base64Value']")
            if ($null -eq $value) { throw "PRI candidate has no value: $resourceName" }
            if ($candidate.GetAttribute('type') -eq 'Path') {
                $resolved = [IO.Path]::GetFullPath((Join-Path $Layout $value.InnerText))
                $root = ([IO.Path]::GetFullPath($Layout)).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
                if (-not $resolved.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "PRI references an unsafe asset outside the package: $($value.InnerText)"
                }
            }
            $qualifiers = @($candidate.SelectNodes(".//*[local-name()='Qualifier']") | ForEach-Object {
                '{0}={1}:{2}:{3}' -f $_.GetAttribute('name'), $_.GetAttribute('value'), $_.GetAttribute('priority'), $_.GetAttribute('scoreAsDefault')
            } | Sort-Object)
            $items.Add((@($resourceName, $candidate.GetAttribute('type'), ($qualifiers -join '|'), $value.InnerXml) | ConvertTo-Json -Compress))
        }
    }
    if ($items.Count -eq 0) { throw 'Application PRI contains no resource candidates.' }
    return @($items | Sort-Object)
}

function Get-BrandingPaths {
    param([string]$Layout)
    @(Get-ChildItem -LiteralPath $Layout -File -Recurse | ForEach-Object {
        [IO.Path]::GetRelativePath($Layout, $_.FullName).Replace('\', '/')
    } | Where-Object {
        ($_ -match '^Assets/AppEntries/.+\.(png|svg)$' -and $_ -notmatch '/_Sources/') -or
        $_ -match '^Assets/(Wino_Icon\.ico|WinoLogo\.png|StoreLogo[^/]*\.png)$' -or
        $_ -eq 'EML/eml.png'
    } | Sort-Object)
}

function Set-BetaManifest {
    param([xml]$Manifest)
    $Manifest.Package.Identity.Name = [string]$profile.PackageName
    $Manifest.Package.Identity.Publisher = [string]$profile.Publisher
    $Manifest.Package.Properties.DisplayName = [string]$profile.DisplayNames.Mail
    $entries = @{ App='Mail'; CalendarApp='Calendar'; ContactsApp='People'; ToDoApp='Tasks'; MailNotificationHost='Mail'; CalendarNotificationHost='Calendar'; PeopleNotificationHost='People'; ToDoNotificationHost='Tasks' }
    foreach ($app in $Manifest.Package.Applications.Application) {
        $mode = $entries[[string]$app.Id]
        if (-not $mode) { throw "Unknown packaged application: $($app.Id)" }
        $name = [string]$profile.DisplayNames.$mode
        $visual = $app.SelectSingleNode("*[local-name()='VisualElements']")
        if ($null -eq $visual) { throw "Application is missing VisualElements: $($app.Id)" }
        $visual.SetAttribute('DisplayName', $name)
        foreach ($startup in $app.SelectNodes(".//*[local-name()='StartupTask']")) { $startup.SetAttribute('DisplayName', "$name Startup Service") }
        foreach ($node in $app.SelectNodes(".//*[local-name()='ToastNotificationActivation']")) { $node.SetAttribute('ToastActivatorCLSID', [string]$profile.NotificationActivatorIds.$mode) }
        foreach ($node in $app.SelectNodes(".//*[local-name()='Class']")) { $node.SetAttribute('Id', [string]$profile.NotificationActivatorIds.$mode); $node.SetAttribute('DisplayName', "$name notification host") }
        foreach ($node in $app.SelectNodes(".//*[local-name()='ExeServer']")) { $node.SetAttribute('DisplayName', "$name notification host") }
    }
}

function Invoke-Tool {
    param([string]$Executable, [string[]]$Arguments)
    Write-Host "Running $([IO.Path]::GetFileName($Executable))"
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$([IO.Path]::GetFileName($Executable)) failed with exit code $LASTEXITCODE." }
}

if (-not (Test-Path -LiteralPath $InputBundle -PathType Leaf)) { throw "Input MSIXBundle was not found: $InputBundle" }
$resolvedOutput = [IO.Path]::GetFullPath($OutputRoot)
$workRoot = Join-Path $resolvedOutput 'work'
$layout = Join-Path $workRoot 'layout'
$packagesRoot = Join-Path $workRoot 'packages'
$priInput = Join-Path $workRoot 'pri-input'
$priConfig = Join-Path $workRoot 'priconfig.xml'
$null = New-Item -ItemType Directory -Path $layout, $packagesRoot, $priInput -Force

$bundleManifest = Get-ArchiveXml $InputBundle 'AppxMetadata/AppxBundleManifest.xml'
$x64Packages = @($bundleManifest.Bundle.Packages.Package | Where-Object { $_.Type -eq 'application' -and [string]$_.Architecture -ieq 'x64' })
if ($x64Packages.Count -ne 1) { throw "Expected exactly one x64 application package, found $($x64Packages.Count)." }
$bundleArchive = [IO.Compression.ZipFile]::OpenRead($InputBundle)
try {
    $entry = $bundleArchive.GetEntry([string]$x64Packages[0].FileName)
    if ($null -eq $entry) { throw "Input bundle references missing x64 package: $($x64Packages[0].FileName)" }
    $innerMsix = Join-Path $workRoot 'stable-x64.msix'
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $innerMsix, $true)
} finally { $bundleArchive.Dispose() }

[xml]$manifest = Get-ArchiveXml $innerMsix 'AppxManifest.xml'
if ([string]::IsNullOrWhiteSpace([string]$manifest.Package.Identity.Name)) { throw 'Input package identity is missing.' }
[IO.Compression.ZipFile]::ExtractToDirectory($innerMsix, $layout, $true)
Set-BetaManifest $manifest
$manifest.Save((Join-Path $layout 'AppxManifest.xml'))

$brandingPaths = @(Get-BrandingPaths $layout)
if ($brandingPaths.Count -eq 0) { throw 'No Beta branding assets were found in the input package.' }
foreach ($relative in $brandingPaths) {
    $source = [IO.Path]::GetFullPath((Join-Path $BetaAssetsPath $relative))
    $root = [IO.Path]::GetFullPath($BetaAssetsPath).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $source.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing Beta artwork: $relative" }
    Copy-Item -LiteralPath $source -Destination ([IO.Path]::GetFullPath((Join-Path $layout $relative))) -Force
}
$profile | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $layout 'release-profile.json') -Encoding utf8

$priCandidates = @(Get-ChildItem -LiteralPath $layout -File -Recurse | Where-Object { $_.Name -in @('resources.pri', 'application.pri') })
if ($priCandidates.Count -eq 0) {
    $topLevelEntries = @(Get-ChildItem -LiteralPath $layout -Force | ForEach-Object {
        [IO.Path]::GetRelativePath($layout, $_.FullName).Replace('\', '/')
    })
    throw "The input application package is missing resources.pri/application.pri. Top-level entries: $($topLevelEntries -join ', ')"
}

$applicationPri = $priCandidates |
    Sort-Object @{ Expression = { if ($_.Name -ieq 'resources.pri') { 0 } else { 1 } } }, @{ Expression = { [IO.Path]::GetRelativePath($layout, $_.FullName).Length } } |
    Select-Object -First 1 -ExpandProperty FullName
$applicationPriName = [IO.Path]::GetFileName($applicationPri)
Write-Host "Using application resource PRI: $([IO.Path]::GetRelativePath($layout, $applicationPri))"
Copy-Item -LiteralPath $applicationPri -Destination (Join-Path $priInput $applicationPriName) -Force
@' 
<?xml version="1.0" encoding="utf-8"?>
<resources targetOsVersion="10.0.0" majorVersion="1">
  <index root="." startIndexAt="">
    <default><qualifier name="Language" value="en-US" /></default>
    <indexer-config type="folder" foldernameAsQualifier="true" filenameAsQualifier="true" qualifierDelimiter="." />
    <indexer-config type="PRI" />
  </index>
</resources>
'@ | Set-Content -LiteralPath $priConfig -Encoding utf8

$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
$makePri = Get-ChildItem -LiteralPath $sdkRoot -Filter makepri.exe -File -Recurse | Where-Object { $_.FullName -match '\\x64\\makepri\.exe$' } | Sort-Object FullName -Descending | Select-Object -First 1
$makeAppx = Get-ChildItem -LiteralPath $sdkRoot -Filter makeappx.exe -File -Recurse | Where-Object { $_.FullName -match '\\x64\\makeappx\.exe$' } | Sort-Object FullName -Descending | Select-Object -First 1
if ($null -eq $makePri -or $null -eq $makeAppx) { throw 'Windows SDK makepri.exe/makeappx.exe were not found.' }

$beforeDump = Join-Path $workRoot 'pri-before.xml'
$afterDump = Join-Path $workRoot 'pri-after.xml'
Invoke-Tool $makePri.FullName @('dump','/if',$applicationPri,'/of',$beforeDump,'/dt','detailed','/o')
Invoke-Tool $makePri.FullName @('new','/pr',$priInput,'/cf',$priConfig,'/in',$profile.PackageName,'/of',$applicationPri,'/o')
Invoke-Tool $makePri.FullName @('dump','/if',$applicationPri,'/of',$afterDump,'/dt','detailed','/o')
$before = [xml](Get-Content -LiteralPath $beforeDump -Raw)
$after = [xml](Get-Content -LiteralPath $afterDump -Raw)
$resourceMap = $after.SelectSingleNode("//*[local-name()='ResourceMap']")
if ($null -eq $resourceMap -or $resourceMap.GetAttribute('name') -cne [string]$profile.PackageName) { throw 'The Beta PRI resource-map identity is incorrect.' }
if (@(Compare-Object @(Get-PriCandidates $before $layout) @(Get-PriCandidates $after $layout) -CaseSensitive).Count -gt 0) { throw 'Beta PRI resource candidates differ from the compiled application resources.' }

$betaMsix = Join-Path $workRoot "WinoMail_Beta_${version}_x64.msix"
Invoke-Tool $makeAppx.FullName @('pack','/d',$layout,'/p',$betaMsix,'/h','SHA256')
$packageForBundle = Join-Path $packagesRoot (Split-Path $betaMsix -Leaf)
Copy-Item -LiteralPath $betaMsix -Destination $packageForBundle -Force
$betaBundle = Join-Path $resolvedOutput "WinoMail_Beta_${version}.msixbundle"
Invoke-Tool $makeAppx.FullName @('bundle','/d',$packagesRoot,'/p',$betaBundle,'/bv',$version)

$bundleIdentity = Get-ArchiveXml $betaBundle 'AppxMetadata/AppxBundleManifest.xml'
if ([string]$bundleIdentity.Bundle.Identity.Name -cne [string]$profile.PackageName) { throw 'Final Beta bundle identity name is incorrect.' }
if ([string]$bundleIdentity.Bundle.Identity.Publisher -cne [string]$profile.Publisher) { throw 'Final Beta bundle publisher is incorrect.' }
Write-Host "Beta package created: $betaBundle"
Write-Host "Beta identity: $($profile.PackageName)"
Write-Host "Beta display name: $($profile.DisplayNames.Mail)"