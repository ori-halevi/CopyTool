<#
.SYNOPSIS
    Shared helpers for the build, register and install scripts.

.DESCRIPTION
    Dot-source this file rather than repeating the logic:

        . (Join-Path $PSScriptRoot 'common.ps1')

    Every one of these had drifted between copies — the Explorer restart existed
    six times with six different sleep values, and half the regsvr32 calls ignored
    the exit code the other half threw on.
#>

# The shell extension's CLSID. Must match ShellExt.h and dllmain.cpp.
$script:CopyToolClsid = '{D8E59DEE-51C8-4D84-979A-0D5004B4F07B}'

# Shell classes the drag-drop handler hooks.
$script:CopyToolDropTargets = @('Directory', 'Drive')

function Get-CopyToolClsid { $script:CopyToolClsid }
function Get-CopyToolDropTargets { $script:CopyToolDropTargets }

function Get-CopyToolDragDropKeys {
    <#
    .SYNOPSIS
        The DragDropHandlers registry paths under a given hive.
    #>
    param([ValidateSet('HKCU:', 'HKLM:')] [string] $Hive = 'HKCU:')
    $script:CopyToolDropTargets | ForEach-Object {
        "$Hive\Software\Classes\$_\shellex\DragDropHandlers\CopyTool"
    }
}

function Stop-CopyToolProcesses {
    <#
    .SYNOPSIS
        Stops the host and any elevated worker, then waits for handles to close.
    .DESCRIPTION
        Safe by design: the host exits on idle anyway and queued jobs live on disk,
        so the next drop simply starts a fresh one. Skipping the wait is what
        produces MSB3027 on the next build.
    #>
    $running = Get-Process CopyTool.Host, CopyTool.Elevated -ErrorAction SilentlyContinue
    if (-not $running) { return $false }

    Write-Host "Stopping CopyTool ($($running.ProcessName -join ', '))..." -ForegroundColor Yellow
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    return $true
}

function Restart-Explorer {
    <#
    .SYNOPSIS
        Restarts Explorer so it releases (or reloads) the shell extension.
    .DESCRIPTION
        Explorer normally respawns itself; the guard covers the case where it does
        not. One set of timings, so a machine that needs longer is fixed once.
    #>
    Write-Host 'Restarting Explorer...' -ForegroundColor Yellow
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 1200
    if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) {
        Start-Process explorer.exe
    }
    Start-Sleep -Milliseconds 800
}

function Get-MsBuildPath {
    <#
    .SYNOPSIS
        Full MSBuild from the newest Visual Studio install.
    .DESCRIPTION
        Needed because the dotnet CLI cannot import the C++ targets (MSB4278).
    #>
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) { throw 'vswhere.exe not found. Is Visual Studio installed?' }

    $msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild `
                          -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if (-not $msbuild) {
        throw 'MSBuild not found. Install the "Desktop development with C++" workload.'
    }
    return $msbuild
}

function Invoke-RegSvr32 {
    <#
    .SYNOPSIS
        Registers or unregisters a COM DLL, and fails loudly if it did not work.
    .DESCRIPTION
        The path is quoted here on purpose: Windows PowerShell's Start-Process joins
        -ArgumentList with plain spaces, so a path containing a space (this repo
        lives under "...\C Sharp\...") reaches regsvr32 split in two and LoadLibrary
        fails with a misleading error.
    #>
    param(
        [Parameter(Mandatory)] [string] $Dll,
        [switch] $Unregister,
        [switch] $IgnoreErrors
    )

    $arguments = @('/s')
    if ($Unregister) { $arguments += '/u' }
    $arguments += ('"{0}"' -f $Dll)

    $p = Start-Process regsvr32.exe -ArgumentList $arguments -Wait -PassThru -NoNewWindow
    if ($p.ExitCode -ne 0 -and -not $IgnoreErrors) {
        throw "regsvr32 $(if ($Unregister) { '/u ' })failed for '$Dll' (exit $($p.ExitCode))."
    }
    return $p.ExitCode
}

function Test-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}
