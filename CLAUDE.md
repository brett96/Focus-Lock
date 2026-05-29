# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

```powershell
dotnet build FocusLock.sln           # build everything (src + tests; installer needs publish output first)
dotnet test                          # run all tests
dotnet test --filter "FullyQualifiedName~TestName"  # single test

# Full release build → produces Focus Lock.msi
.\build\build.ps1                    # publishes all apps + watchdog then builds the installer
.\build\build.ps1 -Configuration Debug

# Quick source-only build (no installer)
dotnet build -c Release
```

The UI and Service require Windows and admin rights to run meaningfully. The Service must be registered as a Windows Service (`sc create`) to run as SYSTEM.

## Installer

`build/build.ps1` orchestrates the full pipeline:
1. Publishes UI + BlockerStub + Service + Watchdog to `publish/FocusLock/`
2. Moves `FocusLock.Service.exe` to `publish/service-exe/` and `FocusLock.Watchdog.exe` to `publish/watchdog-exe/`
3. Builds `installer/FocusLock.Installer/` → `Focus Lock.msi` (`OutputName`: `Focus Lock`)
4. Signs published binaries + MSI when `signtool` is available; MSI uses `/d "Focus Lock"` for the UAC-friendly description (unsigned MSIs still show random `C:\Windows\Installer\` names)

The installer requires .NET Desktop Runtime (x64) 9.0+ on the target machine (`DotNetCompatibilityCheck` with roll-forward in Package.wxs; apps use `RollForward=Major` in Directory.Build.props) and Windows 10 build 17763+.  
WiX SDK: `WixToolset.Sdk/5.0.2` (pulled via NuGet — no separate WiX install needed).

**In-place upgrades:** `Package.wxs` uses a stable `UpgradeCode` plus `MajorUpgrade` (`Schedule="afterInstallValidate"`). Installing a newer MSI removes the previous product first (including stopping/uninstalling `FocusLockService`), then installs into the same `C:\Program Files\FocusLock\` path — not side-by-side. Manual components use fixed GUIDs for reliable upgrades.

## Versioning

**Current version:** 1.2.5 (see `Version.props`).

**Bump the version with every user-facing change** before a release build. Edit **`Version.props`** only:

- `FocusLockVersion` — semver `major.minor.patch` (e.g. `1.2.5`)
- `FocusLockAssemblyVersion` — four-part `major.minor.patch.0` (e.g. `1.2.5.0`)

Semver: **PATCH** for fixes, **MINOR** for features (reset patch to `0`), **MAJOR** for breaking changes (reset minor/patch to `0`). Always keep both properties in sync.

Root `Directory.Build.props` and `src/Directory.Build.props` import `Version.props` and apply versions to SDK projects. The WiX project reads the same props (`FocusLock.Installer.wixproj` + `DefineConstants` → `Package.wxs`).

**Also update manually:** `src/FocusLock.UI/app.manifest` `<assemblyIdentity version="…" />` → match `FocusLockAssemblyVersion`.

Do **not** change `UpgradeCode` in the installer (required for in-place upgrades).

## Project Architecture

Focus Lock is a Windows self-parental-control app: users lock themselves out of specific apps and websites until a deadline. The solution has four executables and a shared library:

```
FocusLock.sln
├── src/FocusLock.Core/          net9.0-windows — shared models, IPC protocol, storage
├── src/FocusLock.Service/       net9.0-windows — Windows Service (runs as SYSTEM)
├── src/FocusLock.Watchdog/      net9.0-windows — strict-mode watchdog service (runs as SYSTEM)
├── src/FocusLock.UI/            net9.0-windows — WPF app (asInvoker; no UAC for normal use)
├── src/FocusLock.BlockerStub/   net9.0-windows — tiny exe called by Windows via IFEO
├── installer/FocusLock.Installer/  WiX v4 — produces Focus Lock.msi
└── tests/                       xUnit test projects
```

### How the pieces connect

**UI ↔ Service** communicate via a Named Pipe (`\\.\pipe\FocusLockService`). All privileged operations (registry writes, hosts file, admin account management) are done inside the Service running as SYSTEM — the UI sends IPC requests and receives responses. See `src/FocusLock.Core/Ipc/` for the protocol.

**App blocking** uses two layers:
1. IFEO (Image File Execution Options) registry redirect: `HKLM\...\Image File Execution Options\<app.exe>\Debugger` points to `FocusLock.BlockerStub.exe`. When Windows launches the blocked app, it runs the stub instead.
2. Process kill monitor: the service polls `Process.GetProcesses()` every 2 seconds and kills any blocked processes (catches renamed exes and already-running apps).

**BlockerStub**: IFEO launches pass the target exe path as `args[0]`. The stub queries `IsBlocked` over the named pipe (retries + 5s connect timeout), then shows a **Windows toast** with a unique `Tag` per launch (avoids OS deduplication on repeat opens). Modes: IFEO (default), `--notify domain deadline`, `--message title body`. Fails open if the service is unreachable.

**Website blocking**: Appends a sentinel block to `C:\Windows\System32\drivers\etc\hosts` with `0.0.0.0 domain.com` entries, then calls `ipconfig /flushdns`. On session end, restores from a backup file.

**Session protection** (`SessionProtectionManager` + `FocusLockWatchdog`): Active for **all** sessions (regular and strict). (1) Service DACLs on both services deny `SERVICE_STOP`, pause, and change-config to **Administrators and LocalSystem**; poll loops re-apply if tampered. (2) **Mutual watchdog** restarts the other service if one dies. On session end, persist `session.json` cleared **before** `Deactivate`. Do **not** use `BreakOnTermination`/critical-process (causes bugcheck if `sc stop` runs while flag is set). Recovery: `scripts/Unlock-StuckServices.ps1` or `--unlock-for-setup` (refused during active Strict). **Uninstall/upgrade MSI** runs the same unlock before `StopServices` — fails while Strict is active. **Strict mode** additionally: cannot end early from UI; deny-write ACLs on IFEO keys and hosts file.

### Key files

| File | Role |
|---|---|
| `src/FocusLock.Core/Models/FocusSession.cs` | Central data model — every project depends on it |
| `src/FocusLock.Core/Ipc/Messages.cs` | All IPC request/response record types |
| `src/FocusLock.Core/Ipc/PipeFraming.cs` | Length-prefixed JSON wire framing |
| `src/FocusLock.Service/SessionWorker.cs` | Named-pipe IPC server + screen-time loop host |
| `src/FocusLock.Service/SessionManager.cs` | Session state, start/end handlers, blocking orchestration |
| `src/FocusLock.Service/ScreenTimeManager.cs` | Screen time during sessions: bedtimes, daily limits, per-app limits |
| `src/FocusLock.Service/AppMonitorWorker.cs` | Kills blocked processes, repairs IFEO keys |
| `src/FocusLock.Service/DeadlineWatcherWorker.cs` | Ends session when deadline is reached |
| `src/FocusLock.Service/Blocking/AppBlocker.cs` | IFEO writes, process kill, IFEO repair |
| `src/FocusLock.Service/Blocking/WebsiteBlocker.cs` | Hosts file management |
| `src/FocusLock.Service/Blocking/SessionProtectionManager.cs` | Session DACLs, watchdog start/stop |
| `src/FocusLock.Core/Services/ServiceDaclManager.cs` | SCM DACL deny for Administrators + LocalSystem |
| `src/FocusLock.Core/Services/ProcessProtection.cs` | Critical-process (BreakOnTermination) flag |
| `src/FocusLock.Watchdog/WatchdogWorker.cs` | Restarts main service during any active session |
| `src/FocusLock.Service/SessionWatchdogWorker.cs` | Restarts watchdog during any active session |
| `src/FocusLock.Core/Blocking/SystemProcessList.cs` | Shared exclusion list — processes that must never be blocked |
| `src/FocusLock.Core/Blocking/WebsiteCategories.cs` | Preset domain lists for category blocking on setup |
| `src/FocusLock.UI/Services/SessionStartConfirmationDialog.cs` | Start-session Yes/No summary dialog (deadline, mode, restrictions) |
| `src/FocusLock.UI/Services/ServiceClient.cs` | UI-side Named Pipe client wrapper |
| `src/FocusLock.UI/ViewModels/DashboardViewModel.cs` | Singleton dashboard VM — session countdown, blocks, live screen time |
| `src/FocusLock.UI/ViewModels/SetupViewModel.cs` | New-session wizard — blocks, categories, screen time add/edit, deadline validation |
| `src/FocusLock.Core/Models/DailyTimeLimit.cs` | One daily screen-time rule (id, minutes, schedule) |
| `src/FocusLock.Core/Models/BedtimeRule.cs` | One bedtime schedule (id + day/time range) |
| `src/FocusLock.Core/ScreenTime/BedtimeScheduleHelper.cs` | Bedtime active detection (incl. overnight), occurrence listing, schedule conflict checks |
| `src/FocusLock.Core/ScreenTime/ScreenTimeScheduleHelper.cs` | Schedule phases, next-start resolution, `ResolveDailyLimitDisplay` for dashboard focus rule |
| `src/FocusLock.Core/ScreenTime/ScreenTimeScheduleOverlap.cs` | Overlap detection for daily, app, and bedtime schedules |
| `src/FocusLock.Core/ScreenTime/DailyLimitDisplayContext.cs` | Which daily rule the dashboard/status should highlight |
| `src/FocusLock.UI/ViewModels/ScreenTimeViewModel.cs` | Standalone Screen Time settings page — add/edit/remove limits |
| `src/FocusLock.UI/ViewModels/DailyTimeLimitViewModel.cs` | List row VM for a daily limit (`EditCommand`, delete) |
| `src/FocusLock.UI/ViewModels/BedtimeViewModel.cs` | List row VM for a bedtime (`EditCommand`, delete) |
| `src/FocusLock.UI/Controls/TimePartSpinBox.xaml` | Hour/minute spinners for deadline and schedule times |

### IPC message types

All messages use the envelope `PipeMessage(Type, Payload)` where `Payload` is inner JSON. Message type constants are in `PipeConstants`. The service replies with `PipeMessage("Reply", <serialized response>)`.

Types: `GetStatus`, `GetSessionInfo`, `StartSession`, `EndSession`, `IsBlocked`, `GetScreenTimeConfig`, `SetScreenTimeConfig`, `GetScreenTimeStatus`, `ForceReset`.

### Session persistence

`FocusSession` is serialized to `C:\ProgramData\FocusLock\session.json` by the Service. On service startup, it loads this file and resumes or cleans up any active session. The `StrictPasswordBlob` field holds a DPAPI-encrypted password blob — never sent over IPC.

### Two session modes

- **Regular**: User can end the session early from the dashboard (with confirmation). Service/watchdog DACLs and mutual watchdog still apply until end.
- **Strict**: Session runs until deadline; UI hides End Session Early. Adds IFEO/hosts deny-write ACLs on top of session protection.

### Session start rules (UI + service)

Enforced in `SetupViewModel.StartSessionAsync` and `SessionManager.StartSessionAsync`:

- Deadline ≥ 5 minutes ahead (UI) / ≥ 1 minute (service)
- Deadline ≤ **1 year** ahead
- Strict mode requires consent checkbox
- At least one of: blocked apps, blocked sites, or screen time limits (`StDailyLimits` or `StAppLimits`)
- Device **daily** screen time limit: minimum **5 minutes** (`ScreenTimeConfig.MinDailyLimitMinutes`); per-app limits are unchanged (still ≥ 1 minute)
- After validation passes, `SessionStartConfirmationDialog.Show` must return Yes before `SetScreenTimeConfig` / `StartSession` IPC runs

Default deadline on setup page: **now + 1 hour**. `DatePicker` uses `DisplayDateEnd = Today + 1 year`.

### UI structure

WPF MVVM app using `CommunityToolkit.Mvvm` (`[ObservableProperty]`, `[RelayCommand]`) and `Microsoft.Extensions.DependencyInjection`. Navigation via `NavigationService` swapping `Page` instances in `MainWindow`'s `Frame`. DI container registered in `App.xaml.cs`.

- **`DashboardViewModel`**: registered **singleton** — avoids idle/active flicker when navigating back from settings.
- **`CanEndEarly`**: `IsActive && !IsStrictMode && !IsEndingSession` — do not tie to refresh flags (caused button flicker every 5s).
- **`IsEndingSession`**: shown for early end (after confirm) and when countdown hits zero (`BeginSessionEndingAsync`); polls until `GetSessionInfo` returns idle; `ShowNewSessionButton` = `IsIdle && !IsEndingSession`.
- **`HasSessionBlocks`**: shows blocked apps/sites panel; scrollable via inner `ScrollViewer` (`MaxHeight=160`).

Pages: `DashboardPage` (primary idle/active UI), `SetupPage` (wizard), `ActiveSessionPage` (legacy/alternate), `ScreenTimePage`, `SettingsPage`.

| `src/FocusLock.UI/ViewModels/AppTimeLimitViewModel.cs` | List row VM for a per-app limit (`EditCommand`, delete) |

**Bedtimes** (`BedtimeRule`, `BedtimeScheduleHelper`): Stored in `ScreenTimeConfig.Bedtimes`. Enforced by `ScreenTimeManager.TickBedtime` only while a focus session is active. Active bedtime → toast → `WTSDisconnectSession` after 5s; re-login repeats until window ends. `ShouldRunTick()` runs only during an active focus session. Overlap: `ScreenTimeScheduleOverlap.TryFindBedtimeLimitConflict` / `HasBedtimeLimitConflicts`. Overnight: end ≤ start on start day; morning tail uses previous day’s flag.

**Screen Time enforcement (sessions)**: `ScreenTimeManager` tracks multiple `DailyLimits` (each with its own schedule and per-rule usage in `DailyRuleUsage`). At most one daily rule is active at any instant; overlap is prevented at setup. Per-app limits use rule `Id` for state; same app may have multiple non-overlapping limits. App usage detection uses `BlockedAppMatcher.MatchesAppTimeLimit` (same matching rules as session app blocking).

**Screen time limit editing** (`SetupViewModel`, `ScreenTimeViewModel`): list rows use `DailyTimeLimitViewModel` / `AppTimeLimitViewModel` with `EditCommand` + delete callback. Edit sets `_editingDailyLimitId` / `_editingAppLimitId`, opens the add form pre-filled, and switches form title/button to “Edit …” / “Save”. Save preserves the existing rule `Id`. Overlap checks pass `excludeId` so the rule being edited does not conflict with itself. XAML: Edit button left of delete on `SetupPage` and `ScreenTimePage`.

**Screen time dashboard**: `GetScreenTimeStatus` includes schedule phase fields for daily limits plus `BedtimeActiveNow`, `ActiveBedtimeLabel`, and `UpcomingBedtimesDuringSession` (bedtime windows intersecting `[now, session deadline]`). `ScreenTimeScheduleHelper.ResolveDailyLimitDisplay` picks which daily rule to surface. Dashboard `Bedtimes` panel shows active bedtime and upcoming windows during the session.

**End session early**: `MessageBox` Yes/No, then `RequestEndSessionUntilAcknowledgedAsync` (up to 3 IPC attempts) and `WaitForSessionIdleAsync` (poll `GetSessionInfo` until idle, re-send `EndSession` if still active). Timers paused while ending. `ActiveSessionViewModel` still has a simpler end path if that page is used.

**Start session confirm** (`SessionStartConfirmationDialog`): After setup validation in `StartSessionAsync`, a Yes/No dialog summarizes end date/time, Regular vs Strict mode, blocked app/site counts, and bullet lists for daily limits, app limits, and bedtimes. Cancel returns to the wizard without IPC.

**Service reachability UI**: `ServiceClient.SendAsync` retries (`IpcRetryAttempts` / `IpcRetryDelayMs`). Dashboard requires **3** consecutive failed session polls before `IsServiceUnreachable`; a successful `GetStatus` or `GetScreenTimeStatus` clears the streak. Pipe `MaxConnections` = 16 for UI + BlockerStub bursts.

**Named pipe security** (`PipeSecurityHelper`): ACL allows `LocalSystem` and `InteractiveSid` only (no `WorldSid` / broad `AuthenticatedUser`). `ForceReset` requires an admin client token; other IPC is handled by the service as SYSTEM. IPC reads use a **3s** timeout (`PipeConstants.IpcReadTimeoutSeconds`) to prevent connection-slot exhaustion.

**Screen time warnings**: Service toasts at **5** and **1** minutes remaining for daily total and per-app limits (`MaybeNotifyRemaining`; skip 5‑min warning if limit &lt; 5 minutes).

**Notifications**: `SessionNotifier` spawns `BlockerStub` in the user session (`CreateProcessAsUser`) and waits up to 2.5s for toast delivery. `BlockPageServer` calls `ShowWebsiteBlocked` on HTTP hits (port 80). Website toasts require HTTP; app toasts use IFEO stub on each launch.

`TrayManager` (`Services/TrayManager.cs`) wraps `System.Windows.Forms.NotifyIcon` — the UI project enables `<UseWindowsForms>true</UseWindowsForms>` for this. The window hides to tray on minimize and on close; Exit is only available from the tray context menu. Use `using Application = System.Windows.Application;` in any file that uses `Application` directly to resolve the WPF/WinForms ambiguity.

### Service logging

When running as a registered Windows Service (`WindowsServiceHelpers.IsWindowsService() == true`), the service clears the default providers and logs exclusively to Windows Event Log source `"FocusLock"`. During development (console mode), the default console logger is used. The event source is registered by the installer at `HKLM\SYSTEM\CurrentControlSet\Services\EventLog\Application\FocusLock`.

## Important Constraints

- **IFEO write/read requires SYSTEM** — always done in the Service, never the UI.
- **Strict mode**: apply service DACL + IFEO/hosts ACL locks after blocks are applied; cleanup reverses ACLs on session end.
- **`SystemProcessList.IsProtectedFromBlocking`** must be checked in the UI before any exe is added to the block list, and in the Service before writing any IFEO key. Session blocking uses **`BlockedAppMatcher`** so all executables for an installed application name are blocked.
- The BlockerStub must always exit 0 and never throw — fail-open is essential since a hanging stub blocks the original process from showing any UI.
- Session end cleanup must be idempotent — the service may call it after a crash recovery where partial cleanup already happened.
