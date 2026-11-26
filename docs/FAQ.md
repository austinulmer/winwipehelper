# FAQ

**Getting started**
- [Why does Windows say it "protected my PC"?](#why-does-windows-say-it-protected-my-pc)
- [Do I need to install anything first?](#do-i-need-to-install-anything-first)
- [Which versions of Windows does it work on?](#which-versions-of-windows-does-it-work-on)
- [Does it need admin rights?](#does-it-need-admin-rights)

**Installing apps**
- [Where do the apps come from?](#where-do-the-apps-come-from)
- [Does it use the Microsoft Store?](#does-it-use-the-microsoft-store)
- [An app I want isn't in the list](#an-app-i-want-isnt-in-the-list)
- [What if an app is already installed?](#what-if-an-app-is-already-installed)
- [An app failed to install](#an-app-failed-to-install)
- [Does it install drivers?](#does-it-install-drivers)
- [Does it activate Office or Windows?](#does-it-activate-office-or-windows)

**Backups**
- [Will my files go back to the right place if my username changes?](#will-my-files-go-back-to-the-right-place-if-my-username-changes)
- [What isn't backed up?](#what-isnt-backed-up)
- [Can I back up more than 4 GB?](#can-i-back-up-more-than-4-gb)
- [Can I open the backup without WinWipeHelper?](#can-i-open-the-backup-without-winwipehelper)

**Privacy and files**
- [Does WinWipeHelper go online?](#does-winwipehelper-go-online)
- [Where are the logs and settings?](#where-are-the-logs-and-settings)
- [How do I remove WinWipeHelper?](#how-do-i-remove-winwipehelper)

---

## Getting started

### Why does Windows say it "protected my PC"?

The EXE isn't code-signed yet: signing certificates cost money every year, and free signing for open-source projects has been applied for. Windows SmartScreen warns about any unsigned program it hasn't seen often. Click **More info**, then **Run anyway**. Windows remembers your choice for that file.

If you'd rather not trust a download, you can build the EXE yourself from this source code; see [Building](../README.md#building).

### Do I need to install anything first?

No. It's a single EXE that runs on .NET Framework 4.8, which is already part of Windows. If winget (Windows' package manager) is missing or broken, WinWipeHelper fixes or installs it for you.

### Which versions of Windows does it work on?

Windows 10 version 1903 or newer, and Windows 11.

### Does it need admin rights?

Only for installing apps. WinWipeHelper starts as your normal user and asks for admin **once** per install batch. Backups, restores and scans run without admin. Restores should run as your normal user so the restored files belong to you.

---

## Installing apps

### Where do the apps come from?

From **winget**, Microsoft's package manager. winget downloads each app from its vendor's own site and checks the file's hash before installing. Download-only items come from the vendor's URL and must carry a valid digital signature from the expected company. Anything that fails that check goes into a `_NOT VERIFIED - do not run` folder.

### Does it use the Microsoft Store?

No. Apps install from winget's `winget` source, never the Store. Even when winget itself is missing, WinWipeHelper installs it from Microsoft's official winget-cli releases on GitHub instead of the Store.

### An app I want isn't in the list

Add it to your `apps.json`:

1. Find its winget ID: open PowerShell and run `winget search <app name>`.
2. Add a line to `apps.json` with that `id`, a `name` and a `category`. The [README](../README.md#your-own-app-list-appsjson) shows the format.

Or use **Scan this PC...** on a PC that already has the app: it adds everything it finds.

### What if an app is already installed?

winget notices and skips it. The summary shows it as "already installed".

### An app failed to install

1. Read the summary and the log (the `logs` folder next to the EXE). The log has winget's full output for each app.
2. Run the install again with just that app ticked. Downloads sometimes fail on a busy network.
3. If the summary says **restart needed**, restart Windows and try again.
4. Still failing? Try `winget install --id <id> --exact` in PowerShell to see winget's own error, or install the app from its website.

### Does it install drivers?

No. Drivers depend on your exact hardware, so WinWipeHelper gives you a page of links to the official driver sites at the end of each install instead.

### Does it activate Office or Windows?

No. Office installs through Microsoft's Office Deployment Tool, and you activate it by signing in to Office with the account that owns the license. WinWipeHelper never touches Windows activation.

---

## Backups

### Will my files go back to the right place if my username changes?

Yes. Desktop, Documents, Pictures, Videos, Music, Downloads and your user folder are saved by **folder type**, not by path. On restore they go into the new user's folders, even if the username is different or OneDrive has moved them. Other folders, like `D:\Projects`, go back to the same path.

### What isn't backed up?

- Anything you didn't tick.
- Links and junctions.
- OneDrive files that are online-only, because they're already safe in OneDrive.

Every skipped item is listed in `<backup>.skipped.txt` next to the zip. Files that are open in other programs are still backed up.

### Can I back up more than 4 GB?

Yes. Backups use Zip64, so there's no 4 GB limit.

### Can I open the backup without WinWipeHelper?

Yes. It's a normal `.zip` file that opens in Windows Explorer, 7-Zip or any other zip program.

---

## Privacy and files

### Does WinWipeHelper go online?

Only to do what you ask:

- winget downloads apps from their vendors, and download-only items come from their listed URLs. Items that point at a GitHub project ask GitHub for that project's newest release.
- If winget needs repairing, WinWipeHelper downloads it from Microsoft's GitHub releases.
- **App icons:** the first time it starts, WinWipeHelper asks whether it may download icons. Only if you say yes does it download the icon of each app that isn't installed yet from the app's own website, once per site. **Icons from app websites** changes your answer later.

There's no tracking, no account and no telemetry. Backups and restores never go online.

### Where are the logs and settings?

Next to the EXE: a `logs` folder, `settings.json` and your saved backup selections. If that folder can't be written to (for example, it's on a read-only drive), they go to `%LOCALAPPDATA%\WinWipeHelper` instead. Downloaded icons are kept in `%LOCALAPPDATA%\WinWipeHelper\icons`.

### How do I remove WinWipeHelper?

Delete the EXE. Optionally also delete the `logs` folder next to it and `%LOCALAPPDATA%\WinWipeHelper`. Nothing else is installed.
