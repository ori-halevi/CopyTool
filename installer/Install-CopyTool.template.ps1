<#
.SYNOPSIS
    CopyTool - self-contained installer. One file, no other files needed.

.DESCRIPTION
    Everything CopyTool needs is embedded in this script as a compressed payload.
    Copy this one file to any Windows 10/11 x64 machine and run it; nothing else
    from the repository has to come with it.

    Installs per-user by default: %LOCALAPPDATA%\CopyTool\bin plus one HKCU key.
    No administrator rights, no service, no scheduled task.

    The same file uninstalls. It copies itself to the data directory during
    install, so removal never depends on still having this copy.

.PARAMETER Uninstall
    Remove CopyTool instead of installing it.

.PARAMETER AllUsers
    Install to Program Files and register machine-wide. Needs an elevated shell,
    and buys nothing except availability to other accounts.

.PARAMETER InstallRuntime
    If the .NET Desktop Runtime is missing, fetch it with winget rather than just
    reporting it. Opt-in, because installing a runtime is a change to the machine
    and not something an app installer should do behind your back.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Install-CopyTool.ps1
    powershell -ExecutionPolicy Bypass -File .\Install-CopyTool.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [switch] $Uninstall,
    [switch] $AllUsers,
    [switch] $InstallRuntime,
    [switch] $NoRestartExplorer
)

$ErrorActionPreference = 'Stop'

# Must match ShellExt.h and dllmain.cpp.
$Clsid       = '@@CLSID@@'
$DropTargets = @('Directory', 'Drive')
$Version     = '@@VERSION@@'
$BuiltOn     = '@@BUILT@@'

$DataDir      = Join-Path $env:LOCALAPPDATA 'CopyTool'
$UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\CopyTool'

# --------------------------------------------------------------------------
#  helpers
# --------------------------------------------------------------------------
function Test-Elevated {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Stop-CopyTool {
    $running = Get-Process CopyTool.Host, CopyTool.Elevated -ErrorAction SilentlyContinue
    if (-not $running) { return }
    Write-Host '  stopping the running host...'
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 600
}

function Restart-Explorer {
    if ($NoRestartExplorer) { return }
    # Explorer keeps a loaded shell extension locked, and caches the ones it knows
    # about. Both problems are solved by restarting it.
    Write-Host '  restarting Explorer...'
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 1200
    if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) { Start-Process explorer.exe }
    Start-Sleep -Milliseconds 800
}

function Invoke-RegSvr32 {
    param([string] $Dll, [switch] $Unregister, [switch] $IgnoreErrors)

    # The path is quoted here on purpose: Start-Process joins -ArgumentList with
    # plain spaces, so a path containing one reaches regsvr32 split in two and
    # LoadLibrary fails with a misleading error.
    $arguments = @('/s')
    if ($Unregister) { $arguments += '/u' }
    $arguments += ('"{0}"' -f $Dll)

    $p = Start-Process regsvr32.exe -ArgumentList $arguments -Wait -PassThru -NoNewWindow
    if ($p.ExitCode -ne 0 -and -not $IgnoreErrors) {
        throw "regsvr32 failed for '$Dll' (exit $($p.ExitCode))."
    }
}

function Get-DropTargetKeys {
    param([string] $Hive)
    $DropTargets | ForEach-Object { "$Hive\Software\Classes\$_\shellex\DragDropHandlers\CopyTool" }
}

# --------------------------------------------------------------------------
#  the machine has to be able to run it
# --------------------------------------------------------------------------
function Test-Prerequisites {
    if (-not [Environment]::Is64BitOperatingSystem) {
        throw 'CopyTool is x64 only: the shell extension has to match explorer.exe.'
    }
    if ([Environment]::OSVersion.Version.Major -lt 10) {
        throw 'CopyTool needs Windows 10 or 11.'
    }

    # The runtime is the one thing not in this file - it is 60 MB and shared.
    $found = $false
    $shared = Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.WindowsDesktop.App'
    if (Test-Path $shared) {
        $found = @(Get-ChildItem $shared -Directory -ErrorAction SilentlyContinue |
                   Where-Object { $_.Name -match '^9\.' }).Count -gt 0
    }
    if ($found) { return }

    Write-Host ''
    Write-Warning '.NET 9 Desktop Runtime (x64) was not found.'

    if ($InstallRuntime -and (Get-Command winget -ErrorAction SilentlyContinue)) {
        Write-Host '  installing it with winget...'
        & winget install --id Microsoft.DotNet.DesktopRuntime.9 --architecture x64 `
                         --accept-source-agreements --accept-package-agreements
        if ($LASTEXITCODE -ne 0) { throw "winget failed (exit $LASTEXITCODE)." }
        return
    }

    Write-Host ''
    Write-Host '  Install it first, then run this again:' -ForegroundColor Yellow
    Write-Host '    winget install Microsoft.DotNet.DesktopRuntime.9'
    Write-Host '  or download from:'
    Write-Host '    https://dotnet.microsoft.com/download/dotnet/9.0'
    Write-Host ''
    Write-Host '  Or re-run this installer with -InstallRuntime to have it done for you.'
    throw 'Missing prerequisite: .NET 9 Desktop Runtime.'
}

# --------------------------------------------------------------------------
#  uninstall
# --------------------------------------------------------------------------
function Uninstall-CopyTool {
    Write-Host 'Removing CopyTool' -ForegroundColor Cyan

    $targets = @(
        (Join-Path $env:LOCALAPPDATA 'CopyTool\bin')
        (Join-Path $env:ProgramFiles 'CopyTool')
    ) | Where-Object { Test-Path $_ }

    Stop-CopyTool

    foreach ($dir in $targets) {
        $dll = Join-Path $dir 'CopyTool.ShellExt.dll'
        if (Test-Path $dll) {
            Write-Host "  unregistering $dll"
            Invoke-RegSvr32 -Dll $dll -Unregister -IgnoreErrors
        }
    }

    Restart-Explorer

    foreach ($dir in $targets) {
        Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path $dir) { Write-Warning "could not remove $dir (still in use?)" }
    }

    # Belt and braces: DllUnregisterServer clears these, but it cannot run once the
    # DLL is gone - and both hives, because an earlier machine-wide install would
    # otherwise keep pointing at a file that no longer exists.
    foreach ($hive in 'HKCU:', 'HKLM:') {
        foreach ($key in (Get-DropTargetKeys $hive)) {
            if (Test-Path $key) { Remove-Item $key -Recurse -Force -ErrorAction SilentlyContinue }
        }
        foreach ($key in @("$hive\Software\Classes\CLSID\$Clsid", "$hive\$UninstallKey")) {
            if (Test-Path $key) { Remove-Item $key -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }

    $pending = @(Get-ChildItem (Join-Path $DataDir 'jobs') -Filter '*.json' -ErrorAction SilentlyContinue).Count
    Write-Host ''
    Write-Host 'Uninstalled.' -ForegroundColor Green
    Write-Host "  kept $DataDir (log$(if ($pending) { ", $pending unfinished job file(s)" })) - delete it by hand if you want it gone."
}

# --------------------------------------------------------------------------
#  install
# --------------------------------------------------------------------------
function Install-CopyTool {
    Test-Prerequisites

    if ($AllUsers -and -not (Test-Elevated)) {
        throw 'Installing for all users needs an elevated shell. Omit -AllUsers for a per-user install.'
    }
    if (-not $AllUsers -and (Test-Elevated)) {
        # regsvr32 picks its hive from the token, so an elevated run would land in
        # HKLM and the per-user install would silently become machine-wide.
        Write-Warning 'Running elevated without -AllUsers: registration will land in HKLM, not HKCU.'
    }

    $target = if ($AllUsers) { Join-Path $env:ProgramFiles 'CopyTool' }
              else           { Join-Path $env:LOCALAPPDATA 'CopyTool\bin' }

    Write-Host "Installing CopyTool $Version" -ForegroundColor Cyan
    Write-Host "  built     $BuiltOn"
    Write-Host "  to        $target"

    # A registered DLL is locked by Explorer; let go of it before overwriting.
    $installed = Join-Path $target 'CopyTool.ShellExt.dll'
    if (Test-Path $installed) {
        Write-Host '  unregistering the previous version...'
        Invoke-RegSvr32 -Dll $installed -Unregister -IgnoreErrors
    }
    Stop-CopyTool
    Restart-Explorer

    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Expand-Payload -To $target

    Write-Host '  registering the drag-drop handler...'
    Invoke-RegSvr32 -Dll $installed

    # This script is the uninstaller, so it has to outlive wherever it was run
    # from. The data directory, not $target, because $target is what it deletes.
    New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
    $self = Join-Path $DataDir 'Install-CopyTool.ps1'
    Copy-Item -LiteralPath $PSCommandPath -Destination $self -Force

    $hive = if ($AllUsers) { 'HKLM:' } else { 'HKCU:' }
    $key  = "$hive\$UninstallKey"
    New-Item -Path $key -Force | Out-Null
    Set-ItemProperty $key 'DisplayName'     'CopyTool'
    Set-ItemProperty $key 'DisplayVersion'  $Version
    Set-ItemProperty $key 'Publisher'       'Ori Halevi'
    Set-ItemProperty $key 'InstallLocation' $target
    Set-ItemProperty $key 'NoModify'        1 -Type DWord
    Set-ItemProperty $key 'NoRepair'        1 -Type DWord
    Set-ItemProperty $key 'UninstallString' `
        "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$self`" -Uninstall"

    Restart-Explorer

    $hooked = Get-DropTargetKeys $hive | Where-Object { Test-Path $_ } |
              ForEach-Object { ($_ -split '\\')[3] }

    Write-Host ''
    Write-Host 'Installed.' -ForegroundColor Green
    Write-Host "  scope     $(if ($AllUsers) { 'all users (HKLM)' } else { 'current user (HKCU)' })"
    Write-Host "  hooked    $($hooked -join ', ')"
    Write-Host "  uninstall $self -Uninstall"
    Write-Host ''
    Write-Host 'Right-drag a file or folder onto another folder to use it.'
}

# --------------------------------------------------------------------------
#  payload
# --------------------------------------------------------------------------
function Expand-Payload {
    param([string] $To)

    Write-Host '  unpacking...'
    $zip = Join-Path $env:TEMP ('copytool-' + [Guid]::NewGuid().ToString('N') + '.zip')
    try {
        [IO.File]::WriteAllBytes($zip, [Convert]::FromBase64String($PayloadBase64))
        Expand-Archive -LiteralPath $zip -DestinationPath $To -Force
    }
    finally {
        Remove-Item $zip -Force -ErrorAction SilentlyContinue
    }
}

$PayloadBase64 = @'
@@PAYLOAD@@
'@

# --------------------------------------------------------------------------
if ($Uninstall) { Uninstall-CopyTool } else { Install-CopyTool }
