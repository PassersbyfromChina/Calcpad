<#
.SYNOPSIS
    Builds every Calcpad shipping target in one command.

.DESCRIPTION
    Produces, under <repo>\artifacts:
      win-x64\      published desktop head (Windows x64)
      win-arm64\    published desktop head (Windows on Arm)
      android\      signed-by-debug-keystore APK
      linux-x64\    published desktop head (optional, -Targets linux-x64)

    The version is read from Directory.Build.props; nothing is hard coded here.

.PARAMETER Targets
    Any of: win-x64, win-arm64, linux-x64, osx-arm64, android. Defaults to the three shipping
    targets: win-x64, win-arm64, android.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER SelfContained
    Publish self contained instead of framework dependent.

.PARAMETER OutputDirectory
    Overrides the artifacts directory. Defaults to <repo>\artifacts.

.PARAMETER Clean
    Removes the artifacts directory before building.

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File build\build.ps1
.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File build\build.ps1 -Targets win-x64,android -SelfContained
#>
[CmdletBinding()]
param(
    [string[]]$Targets = @('win-x64', 'win-arm64', 'android'),
    [string]$Configuration = 'Release',
    [switch]$SelfContained,
    [string]$OutputDirectory,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'artifacts'
}

function Write-Step {
    param([string]$Message)
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Ok {
    param([string]$Message)
    Write-Host "    $Message" -ForegroundColor Green
}

function Write-Warn {
    param([string]$Message)
    Write-Host "    $Message" -ForegroundColor Yellow
}

function Fail {
    param([string]$Message)
    Write-Host "FAILED: $Message" -ForegroundColor Red
    exit 1
}

# --------------------------------------------------------------------------- toolchain discovery

function Test-DotNet {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path $Path)) {
        return $false
    }

    try {
        $sdks = & $Path --list-sdks 2>&1
        if ($LASTEXITCODE -ne 0) {
            return $false
        }

        foreach ($line in @($sdks)) {
            if ("$line" -match '^\s*9\.') {
                return $true
            }
        }
    }
    catch {
        return $false
    }

    return $false
}

function Resolve-DotNet {
    $candidates = New-Object System.Collections.Generic.List[string]

    if (-not [string]::IsNullOrWhiteSpace($env:CALCPAD_DOTNET)) {
        $candidates.Add($env:CALCPAD_DOTNET)
    }

    if (-not [string]::IsNullOrWhiteSpace($env:DOTNET_ROOT)) {
        $candidates.Add((Join-Path $env:DOTNET_ROOT 'dotnet.exe'))
    }

    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        $candidates.Add($command.Source)
    }

    $candidates.Add((Join-Path (Split-Path -Parent $repoRoot) '_toolchain\dotnet\dotnet.exe'))

    foreach ($candidate in $candidates) {
        if (Test-DotNet $candidate) {
            return $candidate
        }
    }

    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path $candidate)) {
            Write-Warn "$candidate is on PATH but has no .NET 9 SDK; trying it anyway."
            return $candidate
        }
    }

    Fail 'A .NET 9 SDK was not found. Install it or set CALCPAD_DOTNET.'
}

function Resolve-AndroidSdk {
    foreach ($candidate in @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT)) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path $candidate)) {
            return $candidate
        }
    }

    $bundled = Join-Path (Split-Path -Parent $repoRoot) '_toolchain\android-sdk'
    if (Test-Path $bundled) {
        return $bundled
    }

    return $null
}

function Resolve-JavaHome {
    if (-not [string]::IsNullOrWhiteSpace($env:JAVA_HOME) -and (Test-Path $env:JAVA_HOME)) {
        return $env:JAVA_HOME
    }

    $bundled = Join-Path (Split-Path -Parent $repoRoot) '_toolchain\jdk17'
    if (Test-Path $bundled) {
        return $bundled
    }

    return $null
}

function Get-ProductVersion {
    $props = Join-Path $repoRoot 'Directory.Build.props'
    if (-not (Test-Path $props)) {
        return '0.0.0'
    }

    $match = Select-String -Path $props -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
    if ($null -eq $match) {
        return '0.0.0'
    }

    return $match.Matches[0].Groups[1].Value.Trim()
}

function Invoke-Native {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [string]$LogFile
    )

    Write-Host "    $FilePath $($Arguments -join ' ')" -ForegroundColor DarkGray
    $output = & $FilePath @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($null -ne $LogFile) {
        $output | Out-File -FilePath $LogFile -Encoding utf8
    }

    foreach ($line in $output) {
        Write-Host "      $line"
    }

    return $exitCode
}

# --------------------------------------------------------------------------- preflight

Write-Step 'Calcpad build'
$dotnet = Resolve-DotNet
$version = Get-ProductVersion
$desktopProject = Join-Path $repoRoot 'src\Calcpad.Desktop\Calcpad.Desktop.csproj'
$androidProject = Join-Path $repoRoot 'src\Calcpad.Android\Calcpad.Android.csproj'

Write-Host "    repository : $repoRoot"
Write-Host "    dotnet     : $dotnet"
Write-Host "    version    : $version"
Write-Host "    artifacts  : $OutputDirectory"
Write-Host "    targets    : $($Targets -join ', ')"

if (-not (Test-Path $desktopProject)) { Fail "Desktop project not found: $desktopProject" }
if (-not (Test-Path $androidProject)) { Fail "Android project not found: $androidProject" }

if ($Clean -and (Test-Path $OutputDirectory)) {
    Remove-Item -Recurse -Force $OutputDirectory
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$logDirectory = Join-Path $OutputDirectory 'logs'
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null

$selfContainedValue = 'false'
if ($SelfContained) { $selfContainedValue = 'true' }

$results = @()
$failed = $false

# --------------------------------------------------------------------------- desktop runtimes

$desktopTargets = @($Targets | Where-Object { $_ -ne 'android' })
foreach ($rid in $desktopTargets) {
    Write-Step "Desktop publish ($rid)"
    $destination = Join-Path $OutputDirectory $rid
    if (Test-Path $destination) { Remove-Item -Recurse -Force $destination }
    New-Item -ItemType Directory -Force -Path $destination | Out-Null

    $log = Join-Path $logDirectory "publish-$rid.log"
    $arguments = @(
        'publish', $desktopProject,
        '-c', $Configuration,
        '-r', $rid,
        '--self-contained', $selfContainedValue,
        '-p:PublishSingleFile=true',
        "-p:Version=$version",
        '-o', $destination,
        '--nologo'
    )

    $exit = Invoke-Native -FilePath $dotnet -Arguments $arguments -LogFile $log
    if ($exit -ne 0) {
        Write-Warn "publish failed for $rid (see $log)"
        $results += [pscustomobject]@{ Target = $rid; Status = 'FAILED'; Artifact = $log; Size = 0 }
        $failed = $true
        continue
    }

    $executable = Get-ChildItem -Path $destination -Filter 'Calcpad*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $executable) {
        $executable = Get-ChildItem -Path $destination -Filter 'Calcpad*' -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Extension -ne '.dll' -and $_.Extension -ne '.json' } |
            Select-Object -First 1
    }

    if ($null -eq $executable) {
        Write-Warn "publish succeeded but no executable was produced for $rid"
        $results += [pscustomobject]@{ Target = $rid; Status = 'NO-EXE'; Artifact = $destination; Size = 0 }
        $failed = $true
        continue
    }

    $sizeMb = [math]::Round($executable.Length / 1MB, 2)
    Write-Ok "$($executable.FullName) ($sizeMb MB)"
    $results += [pscustomobject]@{ Target = $rid; Status = 'OK'; Artifact = $executable.FullName; Size = $executable.Length }
}

# --------------------------------------------------------------------------- android

if ($Targets -contains 'android') {
    Write-Step 'Android APK'

    $androidSdk = Resolve-AndroidSdk
    $javaHome = Resolve-JavaHome
    if ([string]::IsNullOrWhiteSpace($androidSdk)) {
        Write-Warn 'Android SDK not found (set ANDROID_HOME or install _toolchain\android-sdk). Skipping android.'
        $results += [pscustomobject]@{ Target = 'android'; Status = 'SKIPPED'; Artifact = ''; Size = 0 }
        $failed = $true
    }
    elseif ([string]::IsNullOrWhiteSpace($javaHome)) {
        Write-Warn 'JDK not found (set JAVA_HOME or install _toolchain\jdk17). Skipping android.'
        $results += [pscustomobject]@{ Target = 'android'; Status = 'SKIPPED'; Artifact = ''; Size = 0 }
        $failed = $true
    }
    else {
        $env:ANDROID_HOME = $androidSdk
        $env:ANDROID_SDK_ROOT = $androidSdk
        $env:JAVA_HOME = $javaHome
        Write-Host "    android sdk: $androidSdk"
        Write-Host "    java home  : $javaHome"

        $log = Join-Path $logDirectory 'build-android.log'
        $arguments = @(
            'build', $androidProject,
            '-c', $Configuration,
            '-f', 'net9.0-android',
            "-p:AndroidSdkDirectory=$androidSdk",
            "-p:JavaSdkDirectory=$javaHome",
            "-p:Version=$version",
            '--nologo'
        )

        $exit = Invoke-Native -FilePath $dotnet -Arguments $arguments -LogFile $log
        if ($exit -ne 0) {
            Write-Warn "Android build failed (see $log)"
            $results += [pscustomobject]@{ Target = 'android'; Status = 'FAILED'; Artifact = $log; Size = 0 }
            $failed = $true
        }
        else {
            $searchRoots = @(
                (Join-Path $repoRoot 'src\Calcpad.Android\bin'),
                (Join-Path $repoRoot 'src\Calcpad.Android\obj')
            )
            $apk = Get-ChildItem -Path $searchRoots -Filter '*.apk' -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match [regex]::Escape($Configuration) } |
                Sort-Object LastWriteTime -Descending |
                Select-Object -First 1

            if ($null -eq $apk) {
                Write-Warn 'Android build succeeded but no APK was found.'
                $results += [pscustomobject]@{ Target = 'android'; Status = 'NO-APK'; Artifact = $log; Size = 0 }
                $failed = $true
            }
            else {
                $destination = Join-Path $OutputDirectory 'android'
                New-Item -ItemType Directory -Force -Path $destination | Out-Null
                $apkName = "Calcpad-$version-$($apk.Name)"
                $apkTarget = Join-Path $destination $apkName
                Copy-Item -Path $apk.FullName -Destination $apkTarget -Force
                $apkLength = (Get-Item $apkTarget).Length
                $sizeMb = [math]::Round($apkLength / 1MB, 2)
                Write-Ok "$apkTarget ($sizeMb MB)"
                $results += [pscustomobject]@{ Target = 'android'; Status = 'OK'; Artifact = $apkTarget; Size = $apkLength }
            }
        }
    }
}

# --------------------------------------------------------------------------- summary

Write-Step 'Summary'
$results | Format-Table -AutoSize | Out-String | Write-Host

$summaryPath = Join-Path $OutputDirectory 'build-summary.txt'
$results | Format-Table -AutoSize | Out-String | Out-File -FilePath $summaryPath -Encoding utf8
Write-Host "    summary: $summaryPath"

if ($failed) {
    Fail 'One or more targets did not produce an artifact.'
}

Write-Host 'All targets built successfully.' -ForegroundColor Green
exit 0
