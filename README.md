<h1 align="center">CopyTool</h1>

<p align="center">
  A fast, honest file copier for Windows — right-drag integration, a progress window
  that tells the truth, and a copy that never stops to ask.
</p>

---

## What it is

Right-drag a file or folder onto another folder and pick **Copy here with CopyTool**.
A progress window opens with live throughput, an ETA, pause/resume, and a row of
policy chips that decide in advance what happens if something goes wrong.

The design goal is not features — it is that **a copy you walk away from is finished
when you come back**.

## Why it is different

| | Windows Explorer | CopyTool |
|---|---|---|
| A name conflict | the whole job waits for an answer | the item is parked, **the copy continues**, you answer at the end |
| Needs admin rights | discovered when it fails, mid-copy | detected before the first byte, banner up at second one |
| A file is locked | "the file is in use by another program" | **names the program** holding it (Restart Manager) |
| Not enough space | discovered at 90% | refuses to start, and says by how much |
| Identical files | copied again | skipped — and never silently |
| Same-volume move | copy then delete | **rename: instant, any size** |
| Three drops in a row | three windows stacking up | one window, one list — same disk in turn, different disks at once |

## Speed

Measured C: → G: (two NVMe drives), against `robocopy /J /MT:8`:

| Workload | CopyTool | robocopy |
|---|---|---|
| One 2 GB file | **1.1–1.3 GB/s** | 1.0 GB/s |
| 3,000 small files | **183 MB/s** | 158 MB/s |
| 60,000 small files | 108 MB/s | 104 MB/s |

Large files go through an unbuffered, overlapped pipeline whose queue depth comes
from the device profile — deep for NVMe, shallow for a USB disk, where queuing
deep only causes seek thrashing and starves everything else on the machine.

There is also a **background I/O priority** mode. On an idle disk it costs nothing
measurable (2.95 s vs 2.98 s on a 4 GB copy); under contention it yields, so a
copy stops making video playback stutter.

## Verification

Optional, per job, from the **אימות** chip. Every copied file is read back and
compared to its source; a mismatch deletes the destination and records a failure,
because a file of the right name and the right length holding the wrong bytes is
the one corruption every later check misses — including this tool's own
"identical, skip it" fast path on the next run.

Measured on 1 GB, C: → G:

| | |
|---|---|
| off | 0.75 s |
| on | 2.33 s |

The bytes move through the disk three times instead of one, so roughly 3× is the
honest expectation. SHA-256 rather than a faster non-cryptographic hash: with
hardware acceleration it runs well ahead of any drive this tool will meet, so the
disk is the bottleneck either way — and Core keeps its zero dependencies.

A same-volume move reports nothing verified, and that is correct: a rename moves
no bytes, so reading the file back would be comparing it with itself.

## Architecture

```
CopyTool.ShellExt.dll     C++, in-process COM, loaded inside explorer.exe.
                          Captures the drop, writes a job file, hands it over.
                          ~250 lines, no dependency beyond the OS.
        │
CopyTool.Host.exe         C#/WPF, medium integrity. One instance per session.
                          Owns the queue, the engine and every window.
                          Idle: blocked on a pipe, 0% CPU, ~0.4 MB resident,
                          no tray icon, exits after 15 minutes.
        │
CopyTool.Elevated.exe     C#, launched only when a job needs admin rights, only
                          after the user consents. No window, no COM, no
                          listening endpoint. Dies with its parent.
```

**No service. No scheduled task. No persistent elevated component.** Installing
needs no administrator rights, and uninstalling leaves nothing behind.

## Requirements

- Windows 10 / 11, x64
- .NET 9 Desktop Runtime

## Install

```powershell
.\build.ps1
.\installer\install.ps1
```

Installs to `%LOCALAPPDATA%\CopyTool\bin` and registers for the current user only.
`-AllUsers` installs machine-wide and needs an elevated shell.

```powershell
.\installer\uninstall.ps1          # keeps the log and any unfinished job
.\installer\uninstall.ps1 -Purge   # removes those too
```

## Build

```powershell
.\build.ps1                        # Release, native + managed in one pass
.\build.ps1 -Test                  # and run the test suite
.\build.ps1 -Configuration Debug -RestartExplorer
```

Visual Studio's MSBuild is used for both halves: the dotnet CLI cannot import the
C++ targets. The .NET projects target `net9.0` specifically so that VS 2022 can
build them — .NET 10 requires MSBuild 18, which VS 2022 does not ship.

Explorer keeps the shell extension locked while loaded; `-RestartExplorer`
releases it.

## Development tools

`CopyTool.Bench` is a console harness for the engine, with no UI in the way:

```powershell
CopyTool.Bench profile  C:\                     # device type, queue depth, chunk size
CopyTool.Bench scan     <path>                  # tree walk and size histogram
CopyTool.Bench copy     <src> <dst> [--background]
CopyTool.Bench compare  <src> <dst>             # against robocopy
CopyTool.Bench conflict <workdir>               # every conflict policy over one fixture
CopyTool.Bench preflight <src> <dst>            # every pre-copy check
CopyTool.Bench elevation <path>                 # token state, writability
CopyTool.Bench whoislocking <file>              # Restart Manager lookup
```

The host writes to `%LOCALAPPDATA%\CopyTool\host.log` — it has no console, so that
file is the only way to see what it did.

## Tests

```powershell
dotnet test tests\CopyTool.Tests
```

61 tests over the real filesystem — the engine is almost entirely about what the
filesystem actually does, so an abstraction would only agree with whatever the
engine already believes.

The suite is weighted towards **`MoveSafetyTests`**, because a move that deletes
a source it never copied is the one bug class that destroys data. The engine
shipped with exactly that: it deleted the sources of files parked for a conflict
decision, waiting on elevation, or skipped because the destination was newer.
Reintroducing that bug fails five of these tests and no others.

## Status

Working: shell integration, copy engine, progress UI, the multi-job queue,
pause/resume/cancel, conflict policies and dialog, elevation, preflight,
install/uninstall.

Known gaps are tracked in [docs/PLAN.md](docs/PLAN.md) §8 and §9.

## License

MIT © Ori Halevi

---

CopyTool Lite — the original PowerShell edition — lives in `MyScripts/PS/CopyTool`.
