#requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string]$CertificateThumbprint,

    [string]$BetaAssetsPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$releaseScript = Join-Path $PSScriptRoot '..\..\scripts\build-releases.ps1'
. $releaseScript -NonInteractive -Beta -Architectures x64 -BetaAssetsPath $BetaAssetsPath

$certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint" -ErrorAction Stop
if (-not $certificate.HasPrivateKey) {
    throw "The Beta test certificate does not have an accessible private key: $CertificateThumbprint"
}

$selection = [pscustomobject]@{
    Store = $false
    Beta = $true
    Sideload = $false
    Architectures = @('x64')
}

$plan = New-ReleasePlan $selection
$tools = Get-ReleaseTools $selection
$distribution = Get-ReleaseDistributionConfiguration @{}

function Sign-SideloadRelease {
    param(
        [string]$Bundle,
        [object]$Plan,
        [object]$Tools,
        [object]$Signing,
        [string]$Staging,
        [string]$Channel = 'Sideload'
    )

    $profile = Get-ReleaseProfile $Plan $Channel
    $manifest = Get-ArchiveXml $Bundle 'AppxMetadata/AppxBundleManifest.xml'
    Assert-ReleaseIdentity $manifest.Bundle.Identity $profile.PackageName $certificate.Subject $Plan.Version

    $channelInfo = @($Plan.SideloadChannels | Where-Object { $_.Name -eq $Channel } | Select-Object -First 1)
    if ($channelInfo.Count -ne 1) {
        throw "Release channel '$Channel' was not found in the release plan."
    }

    $readyFolder = Join-Path $Staging "ready/$($channelInfo[0].FolderName)"
    $null = New-Item -ItemType Directory -Path $readyFolder -Force

    $logPath = Join-Path $Staging 'logs/sign-beta-test.log'
    Invoke-ReleaseTool $Tools.SignTool @(
        'sign',
        '/fd', 'SHA256',
        '/sha1', $certificate.Thumbprint,
        $Bundle
    ) $logPath

    $signature = Get-AuthenticodeSignature -LiteralPath $Bundle
    if ($null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Thumbprint -cne $certificate.Thumbprint) {
        throw 'The signed Beta bundle does not contain the expected test certificate.'
    }

    $certificatePath = Join-Path $readyFolder 'WinoMail_Beta_TestCertificate.cer'
    Export-Certificate -Cert $certificate -FilePath $certificatePath -Type CERT -Force | Out-Null
    if (-not (Test-Path -LiteralPath $certificatePath -PathType Leaf)) {
        throw "The Beta test certificate was not exported: $certificatePath"
    }
}

$signing = [pscustomobject]@{
    Distributions = @{
        Beta = $distribution
    }
    Configuration = [pscustomobject]@{
        PublisherSubject = $certificate.Subject
    }
}

$symbolsPath = Invoke-ReleaseBuild $plan $tools $signing $null

$releaseDir = Join-Path $plan.OutputRoot "WinoMail_Beta_$($plan.Version)"
$bundlePath = Join-Path $releaseDir "WinoMail_Beta_$($plan.Version).msixbundle"
$certificatePath = Join-Path $releaseDir 'WinoMail_Beta_TestCertificate.cer'

if (-not (Test-Path -LiteralPath $bundlePath -PathType Leaf)) {
    throw "Beta MSIXBundle was not produced: $bundlePath"
}

if (-not (Test-Path -LiteralPath $certificatePath -PathType Leaf)) {
    throw "Beta test certificate was not produced: $certificatePath"
}

Write-Host "Beta bundle: $bundlePath"
Write-Host "Beta test certificate: $certificatePath"
Write-Host "Beta release directory: $releaseDir"
