<#
.SYNOPSIS
    Builds the single-file installer.

.DESCRIPTION
    Takes the build output and produces build\Install-CopyTool.ps1 - one file that
    carries every binary CopyTool needs, installs it, and uninstalls it. Nothing
    else from the repository has to travel with it.

    A generated artefact, not a hand-maintained one: the payload is whatever
    build\<Configuration>\ currently holds, so the installer can never drift from
    the binaries it claims to install.

.EXAMPLE
    .\build.ps1
    .\installer\pack.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [string] $Version = '0.1.0',
    # Build the .exe installers with Inno Setup. Needs ISCC.exe; the PowerShell
    # installer is always produced and never needs anything.
    [switch] $Inno,
    # Also build the variant that carries the .NET runtime. Slow (two publishes)
    # and about 70 MB, so it is opt-in.
    [switch] $Standalone
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $repoRoot 'scripts\common.ps1')

$source = Join-Path $repoRoot "build\$Configuration"
$output = Join-Path $repoRoot "build\Install-CopyTool.ps1"

# Exactly what the installer lays down. Named rather than globbed, so a stray file
# in the build directory can never end up shipped.
$payload = @(
    'CopyTool.ShellExt.dll'
    'CopyTool.Host.exe', 'CopyTool.Host.dll'
    'CopyTool.Host.runtimeconfig.json', 'CopyTool.Host.deps.json'
    'CopyTool.Elevated.exe', 'CopyTool.Elevated.dll'
    'CopyTool.Elevated.runtimeconfig.json', 'CopyTool.Elevated.deps.json'
    'CopyTool.Core.dll'
)

$missing = $payload | Where-Object { -not (Test-Path (Join-Path $source $_)) }
if ($missing) { throw "Not built: $($missing -join ', '). Run build.ps1 first." }

Write-Host "Packing $Configuration" -ForegroundColor Cyan

# Stage, so the archive holds exactly the payload and nothing around it.
$stage = Join-Path ([IO.Path]::GetTempPath()) ('copytool-pack-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
    foreach ($file in $payload) { Copy-Item (Join-Path $source $file) $stage -Force }

    $zip = "$stage.zip"
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal -Force
    $base64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($zip))
    Remove-Item $zip -Force

    $template = Get-Content (Join-Path $PSScriptRoot 'Install-CopyTool.template.ps1') -Raw
    $script = $template.
        Replace('@@CLSID@@',   (Get-CopyToolClsid)).
        Replace('@@VERSION@@', $Version).
        Replace('@@BUILT@@',   (Get-Date -Format 'yyyy-MM-dd HH:mm')).
        Replace('@@PAYLOAD@@', $base64)

    if ($script -match '@@\w+@@') { throw "Template placeholder left unreplaced: $($Matches[0])" }

    # ASCII only, deliberately: a .ps1 with non-ASCII needs a BOM or PowerShell 5.1
    # reads it as ANSI and mangles it. Keeping the installer's own text plain
    # sidesteps that on whatever machine it is carried to.
    [IO.File]::WriteAllText($output, $script, (New-Object Text.UTF8Encoding $false))
}
finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host 'Packed.' -ForegroundColor Green
Write-Host ('  {0}   ({1:N0} KB, carrying {2} files)' -f $output, ((Get-Item $output).Length / 1KB), $payload.Count)

if (-not $Inno) {
    Write-Host ''
    Write-Host 'Add -Inno for the .exe installers.'
    return
}

# Installed per-user by default, which is why it is not on PATH and not under
# Program Files. Newest first.
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe"
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) { throw 'ISCC.exe not found. Install Inno Setup, or omit -Inno.' }

function Invoke-Inno {
    param([string] $From, [string] $Name, [switch] $Carries)

    $defines = @("/DAppVersion=$Version", "/DBuildDir=$From", "/DOutputName=$Name")
    if ($Carries) { $defines += '/DStandalone' }

    & $iscc @defines (Join-Path $PSScriptRoot 'CopyTool.iss') | ForEach-Object {
        if ($_ -match 'error|Successful compile') { Write-Host "  $_" }
    }
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed for $Name (exit $LASTEXITCODE)." }

    $exe = Join-Path $repoRoot "build\$Name.exe"
    Write-Host ('  {0}   ({1:N1} MB)' -f $exe, ((Get-Item $exe).Length / 1MB)) -ForegroundColor Green
}

Write-Host ''
Write-Host "Compiling with $iscc" -ForegroundColor Cyan
Invoke-Inno -From $source -Name "CopyTool-$Version-win-x64-requires-dotnet9"

if ($Standalone) {
    $carry = Join-Path $repoRoot 'build\standalone'
    Remove-Item $carry -Recurse -Force -ErrorAction SilentlyContinue

    Write-Host ''
    Write-Host 'Publishing self-contained...' -ForegroundColor Cyan

    # Both into one folder on purpose: they share a runtime, and publishing them
    # separately would ship two copies of it. Their own assemblies are named
    # apart, so nothing collides.
    #
    # OutputPath is redirected because these projects send theirs to build\<config>,
    # and a self-contained publish would otherwise scatter 250 runtime files
    # through the very directory the small installer is packed from.
    foreach ($project in 'CopyTool.Host', 'CopyTool.Elevated') {
        $scratch = Join-Path ([IO.Path]::GetTempPath()) ('copytool-sc-' + [Guid]::NewGuid().ToString('N'))
        try {
            & dotnet publish (Join-Path $repoRoot "src\$project\$project.csproj") `
                -c $Configuration -r win-x64 --self-contained true `
                -p:OutputPath="$scratch\" -o $carry -v q --nologo
            if ($LASTEXITCODE -ne 0) { throw "publish failed for $project (exit $LASTEXITCODE)." }
        }
        finally { Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue }
        Write-Host "  published $project"
    }

    # The shell extension is native and has nothing to publish - it just comes along.
    Copy-Item (Join-Path $source 'CopyTool.ShellExt.dll') $carry -Force

    # Symbols are for debugging this build, not for running it on someone else's
    # machine, and they are the difference between shipping them and not.
    Get-ChildItem $carry -Recurse -Filter '*.pdb' | Remove-Item -Force

    $files = @(Get-ChildItem $carry -Recurse -File)
    Write-Host ('  {0} files, {1:N0} MB before compression' -f $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB))

    Write-Host ''
    Invoke-Inno -From $carry -Name "CopyTool-$Version-win-x64-standalone" -Carries
}

Write-Host ''
Write-Host 'On the target machine:'
Write-Host "  CopyTool-$Version-win-x64-requires-dotnet9.exe   needs the .NET 9 Desktop Runtime"
if ($Standalone) {
    Write-Host "  CopyTool-$Version-win-x64-standalone.exe        needs nothing"
}
Write-Host '  Install-CopyTool.ps1                            no wizard; needs the runtime'
