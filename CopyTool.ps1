# ============================================================
#  Folder Copy Tool - Powered by ROBOCOPY
# ============================================================

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8
$Host.UI.RawUI.WindowTitle = "Folder Copy Tool"

function Show-Header {
    Clear-Host
    Write-Host ""
    Write-Host " ============================================================" -ForegroundColor Cyan
    Write-Host "                     FOLDER COPY TOOL"                         -ForegroundColor White
    Write-Host "                     Powered by ROBOCOPY"                      -ForegroundColor DarkGray
    Write-Host " ============================================================" -ForegroundColor Cyan
    Write-Host ""
}

function Read-FolderPath {
    param(
        [string]$Prompt,
        [string]$StepLabel,
        [bool]$MustExist
    )

    while ($true) {
        Write-Host " [ $StepLabel ]" -ForegroundColor Yellow
        Write-Host " ------------------------------------------------------------" -ForegroundColor DarkGray
        Write-Host ""
        $path = Read-Host "   $Prompt"
        Write-Host ""

        if ([string]::IsNullOrWhiteSpace($path)) {
            Write-Host "   [!] No path entered. Try again." -ForegroundColor Red
            Write-Host ""
            continue
        }

        $path = $path.Trim('"').Trim("'")

        if ($MustExist -and -not (Test-Path -LiteralPath $path -PathType Container)) {
            Write-Host "   [!] Folder does not exist:" -ForegroundColor Red
            Write-Host "       $path" -ForegroundColor Red
            Write-Host ""
            continue
        }

        return $path
    }
}

# ============================================================
#  MAIN LOOP
# ============================================================
:mainloop while ($true) {
    Show-Header

    $source = Read-FolderPath -Prompt "Enter SOURCE folder path: " -StepLabel "Step 1 of 3" -MustExist $true

    $dest = Read-FolderPath -Prompt "Enter DESTINATION folder path: " -StepLabel "Step 2 of 3" -MustExist $false

    if (-not (Test-Path -LiteralPath $dest -PathType Container)) {
        Write-Host "   [i] Destination does not exist - creating it..." -ForegroundColor DarkYellow
        try {
            New-Item -ItemType Directory -Path $dest -Force -ErrorAction Stop | Out-Null
        }
        catch {
            Write-Host "   [!] Could not create folder. Check permissions." -ForegroundColor Red
            Write-Host ""
            Read-Host "   Press Enter to continue"
            continue mainloop
        }
        Write-Host ""
    }

    Write-Host " [ Step 3 of 3 ]" -ForegroundColor Yellow
    Write-Host " ------------------------------------------------------------" -ForegroundColor DarkGray
    Write-Host ""
    Write-Host "   Scanning source folder..." -ForegroundColor DarkCyan
    Write-Host ""

    $files = Get-ChildItem -LiteralPath $source -Recurse -File -ErrorAction SilentlyContinue
    $fileCount = ($files | Measure-Object).Count
    $totalBytes = ($files | Measure-Object -Property Length -Sum).Sum
    if ($null -eq $totalBytes) { $totalBytes = 0 }
    $totalMB = [math]::Round($totalBytes / 1MB, 2)

    Write-Host "   ------------------------------------------------------------" -ForegroundColor DarkGray
    Write-Host "     Source:  $source"
    Write-Host "     Dest:    $dest"
    Write-Host "     Files:   $fileCount"
    Write-Host "     Size:    $totalMB MB"
    Write-Host "   ------------------------------------------------------------" -ForegroundColor DarkGray
    Write-Host ""

    $confirm = Read-Host "   Start copying? (Y/N)"
    if ($confirm -notmatch '^[Yy]') {
        Write-Host ""
        Write-Host "   Copy cancelled." -ForegroundColor Yellow
        Write-Host ""
        Read-Host "   Press Enter to continue"
        continue mainloop
    }

    Clear-Host
    Write-Host ""
    Write-Host " ============================================================" -ForegroundColor Cyan
    Write-Host "                       COPYING FILES..."                       -ForegroundColor White
    Write-Host " ============================================================" -ForegroundColor Cyan
    Write-Host ""

    $startTime = Get-Date

    $robocopyArgs = @(
        $source, $dest,
        '/E', '/COPY:DAT', '/MT:16', '/J',
        '/R:2', '/W:2', '/NP', '/NDL', '/ETA'
    )

    & robocopy @robocopyArgs
    $rc = $LASTEXITCODE

    $endTime = Get-Date
    $elapsed = $endTime - $startTime

    Write-Host ""
    Write-Host " ============================================================" -ForegroundColor Cyan
    Write-Host "                          SUMMARY"                             -ForegroundColor White
    Write-Host " ============================================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host ("   Started:   {0:HH:mm:ss}" -f $startTime)
    Write-Host ("   Finished:  {0:HH:mm:ss}" -f $endTime)
    Write-Host ("   Duration:  {0:hh\:mm\:ss}" -f $elapsed)
    Write-Host ""

    if ($rc -lt 8) {
        Write-Host "   [V] Copy completed successfully." -ForegroundColor Green
    } else {
        Write-Host "   [!] Errors occurred during copy. Code: $rc" -ForegroundColor Red
    }

    Write-Host ""
    Write-Host " ------------------------------------------------------------" -ForegroundColor DarkGray
    Write-Host ""

    $again = Read-Host "   Copy another folder? (Y/N)"
    if ($again -notmatch '^[Yy]') {
        break mainloop
    }
}

Write-Host ""
Write-Host "   Goodbye!" -ForegroundColor Cyan
Write-Host ""
Start-Sleep -Seconds 1
