# Testing in Windows Sandbox

Windows Sandbox is a throwaway, genuinely fresh copy of Windows: no winget that works, no Microsoft Store
sign-in, nothing installed. It's the closest thing to a friend's brand-new PC.

## One-time setup

Windows Sandbox comes with Windows 10/11 Pro, Enterprise and Education. Turn it on once, in an **admin** PowerShell:

```powershell
Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All
```

Restart Windows when it asks.

## Start a Sandbox with the build mapped in

```powershell
.\build.ps1
.\sandbox\make-wsb.ps1
```

`make-wsb.ps1` writes `sandbox\WinWipeHelper.local.wsb` (it holds this PC's absolute path, so it isn't committed)
and opens it. Inside the Sandbox, the build is copied to `Desktop\WinWipeHelper`, and that folder opens.

## Checklist

Run `WinWipeHelper.exe` from `Desktop\WinWipeHelper` and check each item.

| # | Check | How | Expected |
|---|---|---|---|
| 1 | Starts with nothing installed | Double-click the EXE | Window opens; no console window; `logs\` appears next to the EXE |
| 2 | Dry run | Install tab → tick **Dry run** → tick a few apps and Office → **Install selected** | Commands are listed; "winget is missing or broken. A real run would: ..."; `XML check: valid` for Office |
| 3 | Office XML | **Office options...** → change license and apps | The preview updates; no "Problems" section |
| 4 | winget bootstrap without the Store | Untick Dry run → tick only **7-Zip** → **Install selected** | Log shows step 1 (register) then step 2 (GitHub release, signatures `Valid ... Microsoft Corporation`), then `winget vX is ready`, then 7-Zip installs. The Store never opens |
| 5 | Small install | Same run as 4 | Summary "1 of 1 finished OK"; 7-Zip in the Start menu |
| 6 | Backup | Backup tab → tick Desktop (put a few test files there first) → **Back up** | Warns that the destination is on C: (the Sandbox has no D:); zip is created; skipped list if any |
| 7 | Restore | Delete the test files → **Restore from a backup...** → Yes → Restore | Files come back with their original modified dates |
| 8 | Conflicts | Restore again with **Keep both** | Copies named `name (restored).ext` |
| 9 | Scan | Install tab → **Scan this PC...** → **Start scan** | Finishes in well under 2 minutes; 7-Zip shows with id `7zip.7zip` and sources "winget list, Registry HKLM, Start Menu..." |
| 10 | Save scan | **Save as apps.json...** next to the EXE → restart the EXE | The list now comes from that `apps.json` |

Automated parts (if the self-test is in the build folder):

```bat
cd %USERPROFILE%\Desktop\WinWipeHelper\selftest
WinWipeHelper.SelfTest.exe office,names,backup,dryrun
WinWipeHelper.SelfTest.exe sandbox-bootstrap,sandbox-install,scan --out %USERPROFILE%\Desktop
```

`sandbox-bootstrap` and `sandbox-install` refuse to run outside the Sandbox, because they install App Installer and 7-Zip.
