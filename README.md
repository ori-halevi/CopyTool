# CopyTool

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![Platform](https://img.shields.io/badge/Platform-Windows-blue.svg)](https://www.microsoft.com/windows)
[![PowerShell](https://img.shields.io/badge/PowerShell-5.1%2B-blue.svg)](https://learn.microsoft.com/en-us/powershell/)

A fast and simple folder copy tool for Windows, powered by ROBOCOPY.

## Features

- Fast multi-threaded copy (16 parallel threads via ROBOCOPY)
- Optimized for both many small files and large files (videos)
- Pre-scan shows file count and total size before copying
- Hebrew path support
- Clean console UI with timing summary
- Auto-creates destination folder if missing

## Requirements

- Windows 10 / 11
- PowerShell 5.1 or later (built-in on Windows)

## Usage

### Option 1 — Run the script (recommended)

1. Download `CopyTool.ps1` and `Run-CopyTool.bat`
2. Place both files in the **same folder**
3. Double-click `Run-CopyTool.bat`
4. Follow the on-screen prompts

### Option 2 — Standalone EXE

1. Download `CopyTool.exe` from the [Releases](https://github.com/ori-halevi/CopyTool/releases) page
2. Double-click to run

> **Note:** The EXE is not digitally signed. Windows SmartScreen may show a warning — click **More info** → **Run anyway**.

## Building Your Own EXE

You can compile the PowerShell script into a standalone EXE using [ps2exe](https://github.com/MScholtes/PS2EXE):

```powershell
# Install ps2exe (one-time)
Install-Module -Name ps2exe -Scope CurrentUser

# Compile the script
Invoke-PS2EXE .\CopyTool.ps1 .\CopyTool.exe -title "Folder Copy Tool"
```

The resulting `CopyTool.exe` is fully portable — no need for `.ps1` or `.bat` files.

## License

[MIT](LICENSE) © Ori Halevi

---

<div dir="rtl">

## עברית

כלי העתקת תיקיות מהיר ופשוט עבור Windows, מבוסס על ROBOCOPY.

### תכונות

- העתקה מהירה עם 16 תהליכים מקביליים
- מותאם הן לקבצים קטנים רבים והן לקבצים גדולים (סרטונים)
- סריקה מקדימה: מציגה כמות קבצים ומשקל כולל לפני ההעתקה
- תמיכה בנתיבים בעברית
- ממשק קונסול נקי עם סיכום זמנים
- יצירה אוטומטית של תיקיית היעד אם לא קיימת

### דרישות

- Windows 10 / 11
- PowerShell 5.1 ומעלה (מובנה במערכת ההפעלה)

### שימוש

**אפשרות 1 — הפעלת הסקריפט (מומלץ)**

1. הורד את `CopyTool.ps1` ו-`Run-CopyTool.bat`
2. שמור את שני הקבצים באותה תיקייה
3. לחץ פעמיים על `Run-CopyTool.bat`
4. עקוב אחר ההנחיות במסך

**אפשרות 2 — EXE עצמאי**

1. הורד את `CopyTool.exe` מעמוד ה-[Releases](https://github.com/ori-halevi/CopyTool/releases)
2. לחץ פעמיים להפעלה

> **לתשומת ליבך:** קובץ ה-EXE אינו חתום דיגיטלית. ייתכן ש-SmartScreen של Windows יציג אזהרה — לחץ **מידע נוסף** ← **הפעל בכל זאת**.

### בניית EXE עצמאי

ניתן להמיר את הסקריפט ל-EXE באמצעות הכלי [ps2exe](https://github.com/MScholtes/PS2EXE):

</div>

```powershell
# התקנה חד פעמית
Install-Module -Name ps2exe -Scope CurrentUser

# המרה ל-EXE
Invoke-PS2EXE .\CopyTool.ps1 .\CopyTool.exe -title "Folder Copy Tool"
```

<div dir="rtl">

קובץ ה-`CopyTool.exe` שנוצר הוא נייד לחלוטין — אין צורך בקבצי `.ps1` או `.bat`.

### רישיון

[MIT](LICENSE) © אורי הלוי

</div>
