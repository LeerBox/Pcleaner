# Usage and troubleshooting

PCleaner is a portable Windows 10/11 (x64) application. It scans the cleanup rules you select, shows a preview, and only removes items after you confirm the result.

## First run

1. Download the `PCleaner-<version>-win-x64.zip` asset from [Releases](https://github.com/LeerBox/Pcleaner/releases).
2. Extract the complete ZIP to a folder you control. Keep the files together; do not run the executable from inside the archive.
3. Run `PCleaner.exe`. The package includes the .NET runtime and does not require an installer.
4. Select **Recommended**, then click **Analyze**.
5. Expand the results and review each selected rule, its explanation, and its preview. Click **Clean now** only when the selection is correct.

The application remembers rule selections and settings under `%LOCALAPPDATA%\PCleaner\settings.json`. A daily activity log is written under the same local application-data folder and can be opened from the **Activity log** page.

## Main pages

- **Dashboard** shows the analysis summary and the last cleanup result.
- **Windows** covers temporary files, caches, recycle-bin items, system logs, and supported recent-file lists.
- **Applications** covers supported application and graphics-driver caches and recent-file lists.
- **Web browsers** lists detected profiles. Extensions, bookmarks, passwords, and browser settings are protected.
- **Privacy & tracking** explains local cookies, website identifiers, browser storage, and Windows advertising ID findings.
- **Settings** controls confirmation, browser closing, elevation, profile coverage, and the minimum age of temporary files.
- **Activity log** records actions, skipped items, and errors.

## Privacy presets

The **Privacy & tracking** page provides two review-first presets:

- **Reset what sites know about me** selects cookies, website identifiers, website storage, history, and saved sessions for review.
- **Forget opened files** selects supported Windows and application recent-file lists and saved editor sessions.

These actions can sign you out, remove saved sessions, or discard unsaved editor tabs. Close affected applications and review every selected item before cleaning.

## Running applications and administrator mode

If an item is locked, PCleaner reports the application and skips the item until it can be handled safely. Enable **Ask browsers to close before cleaning** to request a normal close. In administrator mode, **Close blocking applications automatically** may end only windowless background processes after asking; applications with visible windows are left alone. **Restart as administrator** is required for protected system locations and automatic closing.

## Troubleshooting

### PCleaner will not start

- Confirm that Windows is 10 or 11 on a 64-bit machine.
- Extract the whole release archive and run the executable from the extracted folder.
- If Windows SmartScreen warns about the unsigned build, verify that the download came from the project’s [official Releases page](https://github.com/LeerBox/Pcleaner/releases) and compare the ZIP with the published SHA-256 file.
- Remove a partially extracted copy and extract the archive again if files are missing.

### Analysis finds nothing

Run **Refresh** or restart PCleaner after installing or opening a browser. The result depends on the current Windows account, installed applications, browser profiles, and the selected rules. System and guest browser profiles can be included from **Settings**; changing that option requires a refresh.

### An item is skipped or cannot be deleted

Close the named browser or application, including its tray and background processes, then run **Analyze** again. Some locked files can be scheduled for deletion at the next reboot when **Schedule locked files for reboot** is enabled in administrator mode. Protected paths, extension data, passwords, bookmarks, and unsupported formats are intentionally skipped.

### A browser signs me out

That is expected when cookies, website identifiers, local storage, or saved sessions are selected. Sign in again after cleaning. Do not select the privacy preset when you need to preserve active sessions.

### A setting appears not to take effect

Changes to temporary-file age and system/guest profile coverage require **Refresh** so the rule set can be rebuilt. Other settings are saved immediately. Check the Activity log for a warning if settings could not be written.

### Where to report a problem

Open an issue using the details in [Contributing](../CONTRIBUTING.md). Include the Windows version, PCleaner version, affected application or browser, steps to reproduce, and a redacted Activity log entry. Never upload cookies, complete browser profiles, account identifiers, or private document contents.
