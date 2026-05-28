# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

```powershell
dotnet build FocusLock.sln           # build everything (src + tests; installer needs publish output first)
dotnet test                          # run all tests
dotnet test --filter "FullyQualifiedName~TestName"  # single test

# Full release build → produces Focus Lock.msi
.\build\build.ps1                    # publishes all three apps then builds the installer
.\build\build.ps1 -Configuration Debug

# Quick source-only build (no installer)
dotnet build -c Release
```

The UI and Service require Windows and admin rights to run meaningfully. The Service must be registered as a Windows Service (`sc create`) to run as SYSTEM.

## Installer

`build/build.ps1` orchestrates the full pipeline:
1. Publishes UI + BlockerStub + Service to `publish/FocusLock/`
2. Moves `FocusLock.Service.exe` to `publish/service-exe/` (required so WiX can attach `ServiceInstall` without a glob conflict)
3. Builds `installer/FocusLock.Installer/` → `Focus Lock.msi` (`OutputName`: `Focus Lock`)
4. Signs published binaries + MSI when `signtool` is available; MSI uses `/d "Focus Lock"` for the UAC-friendly description (unsigned MSIs still show random `C:\Windows\Installer\` names)

The installer requires .NET 9 Desktop Runtime on the target machine (enforced via `<Launch>` in Package.wxs).  
WiX SDK: `WixToolset.Sdk/5.0.2` (pulled via NuGet — no separate WiX install needed).

## Project Architecture

Focus Lock is a Windows self-parental-control app: users lock themselves out of specific apps and websites until a deadline. The solution has four executables and a shared library:

```
FocusLock.sln
├── src/FocusLock.Core/          net9.0-windows — shared models, IPC protocol, storage
├── src/FocusLock.Service/       net9.0-windows — Windows Service (runs as SYSTEM)
├── src/FocusLock.UI/            net9.0-windows — WPF app (requireAdministrator UAC)
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

**Strict mode** (`StrictModeManager`): Cannot end session early from the UI. Enforces via (1) service DACL denying `SERVICE_STOP` / `SERVICE_PAUSE_CONTINUE` to Administrators, (2) deny-write ACLs on IFEO keys, (3) deny-write ACL on the hosts file. Does **not** create/demote local admin accounts.

### Key files

| File | Role |
|---|---|
| `src/FocusLock.Core/Models/FocusSession.cs` | Central data model — every project depends on it |
| `src/FocusLock.Core/Ipc/Messages.cs` | All IPC request/response record types |
| `src/FocusLock.Core/Ipc/PipeFraming.cs` | Length-prefixed JSON wire framing |
| `src/FocusLock.Service/SessionWorker.cs` | Named-pipe IPC server + screen-time loop host |
| `src/FocusLock.Service/SessionManager.cs` | Session state, start/end handlers, blocking orchestration |
| `src/FocusLock.Service/ScreenTimeManager.cs` | Screen/app time limits — **only enforced while a session is active** |
| `src/FocusLock.Service/AppMonitorWorker.cs` | Kills blocked processes, repairs IFEO keys |
| `src/FocusLock.Service/DeadlineWatcherWorker.cs` | Ends session when deadline is reached |
| `src/FocusLock.Service/Blocking/AppBlocker.cs` | IFEO writes, process kill, IFEO repair |
| `src/FocusLock.Service/Blocking/WebsiteBlocker.cs` | Hosts file management |
| `src/FocusLock.Service/Blocking/StrictModeManager.cs` | Admin account lifecycle (highest-risk code) |
| `src/FocusLock.Core/Blocking/SystemProcessList.cs` | Shared exclusion list — processes that must never be blocked |
| `src/FocusLock.Core/Blocking/WebsiteCategories.cs` | Preset domain lists for category blocking on setup |
| `src/FocusLock.UI/Services/ServiceClient.cs` | UI-side Named Pipe client wrapper |
| `src/FocusLock.UI/ViewModels/DashboardViewModel.cs` | Singleton dashboard VM — session countdown, blocks, live screen time |
| `src/FocusLock.UI/ViewModels/SetupViewModel.cs` | New-session wizard — blocks, categories, screen time, deadline validation |
| `src/FocusLock.UI/Controls/TimePartSpinBox.xaml` | Hour/minute spinners for deadline and schedule times |

### IPC message types

All messages use the envelope `PipeMessage(Type, Payload)` where `Payload` is inner JSON. Message type constants are in `PipeConstants`. The service replies with `PipeMessage("Reply", <serialized response>)`.

Types: `GetStatus`, `GetSessionInfo`, `StartSession`, `EndSession`, `IsBlocked`, `GetScreenTimeConfig`, `SetScreenTimeConfig`, `GetScreenTimeStatus`, `ForceReset`.

### Session persistence

`FocusSession` is serialized to `C:\ProgramData\FocusLock\session.json` by the Service. On service startup, it loads this file and resumes or cleans up any active session. The `StrictPasswordBlob` field holds a DPAPI-encrypted password blob — never sent over IPC.

### Two session modes

- **Regular**: User can end the session early from the dashboard (with confirmation). Admins can still stop the service via SCM (acceptable tradeoff).
- **Strict**: Session runs until deadline; UI hides End Session Early. Service/IFEO/hosts ACLs prevent tampering; see `StrictModeManager`.

### Session start rules (UI + service)

Enforced in `SetupViewModel.StartSessionAsync` and `SessionManager.StartSessionAsync`:

- Deadline ≥ 5 minutes ahead (UI) / ≥ 1 minute (service)
- Deadline ≤ **1 year** ahead
- Strict mode requires consent checkbox
- At least one of: blocked apps, blocked sites, or screen time limits (`StEnableDailyLimit` or `StAppLimits`)
- Device **daily** screen time limit: minimum **5 minutes** (`ScreenTimeConfig.MinDailyLimitMinutes`); per-app limits are unchanged (still ≥ 1 minute)

Default deadline on setup page: **now + 1 hour**. `DatePicker` uses `DisplayDateEnd = Today + 1 year`.

### UI structure

WPF MVVM app using `CommunityToolkit.Mvvm` (`[ObservableProperty]`, `[RelayCommand]`) and `Microsoft.Extensions.DependencyInjection`. Navigation via `NavigationService` swapping `Page` instances in `MainWindow`'s `Frame`. DI container registered in `App.xaml.cs`.

- **`DashboardViewModel`**: registered **singleton** — avoids idle/active flicker when navigating back from settings.
- **`CanEndEarly`**: `IsActive && !IsStrictMode && !IsEndingSession` — do not tie to refresh flags (caused button flicker every 5s).
- **`IsEndingSession`**: shown for early end (after confirm) and when countdown hits zero (`BeginSessionEndingAsync`); polls until `GetSessionInfo` returns idle; `ShowNewSessionButton` = `IsIdle && !IsEndingSession`.
- **`HasSessionBlocks`**: shows blocked apps/sites panel; scrollable via inner `ScrollViewer` (`MaxHeight=160`).

Pages: `DashboardPage` (primary idle/active UI), `SetupPage` (wizard), `ActiveSessionPage` (legacy/alternate), `ScreenTimePage`, `SettingsPage`.

**Screen Time enforcement**: `ScreenTimeManager.OnSessionStarted()` / `OnSessionEnded()` from `SessionManager`. Tick loop no-op when idle. Per-app limits use `limit.Schedule ?? ScreenTimeSchedule.Always` — **never** inherit `DailySchedule` (device daily quota only). Process detection: running exe set (MainModule + process name) plus foreground window fallback. `HandleGetStatus` reads state under the same lock as `Tick`. Per-app over-limit uses `AppBlocker.ApplyScreenTimeBlock`. `IsBlockedResponse` includes optional `BlockMessage` for stub toasts.

**End session early**: `MessageBox` Yes/No, then `RequestEndSessionUntilAcknowledgedAsync` (up to 3 IPC attempts) and `WaitForSessionIdleAsync` (poll `GetSessionInfo` until idle, re-send `EndSession` if still active). Timers paused while ending. `ActiveSessionViewModel` still has a simpler end path if that page is used.

**Service reachability UI**: `ServiceClient.SendAsync` retries (`IpcRetryAttempts` / `IpcRetryDelayMs`). Dashboard requires **3** consecutive failed session polls before `IsServiceUnreachable`; a successful `GetStatus` or `GetScreenTimeStatus` clears the streak. Pipe `MaxConnections` = 16 for UI + BlockerStub bursts.

**Named pipe security** (`PipeSecurityHelper`): ACL allows `LocalSystem`, `BuiltinAdministrators`, and `AuthenticatedUser` (for `BlockerStub` / `IsBlocked` as standard user). **No `WorldSid`.** Privileged message types (`StartSession`, `EndSession`, `SetScreenTimeConfig`, `ForceReset`) require an admin token on the client connection (`ImpersonateNamedPipeClient`). IPC reads use a **3s** timeout (`PipeConstants.IpcReadTimeoutSeconds`) to prevent connection-slot exhaustion.

**Screen time dashboard**: `GetScreenTimeStatus` includes schedule phase fields (`DailyScheduleActiveNow`, `DailyScheduleWindowEndedForToday`, `DailyScheduleResumesAtLocal`). Daily limit enforcement (accumulation, lockout, disconnect) only runs inside the configured window; when the window ends, lockout is cleared and the UI shows that limits are no longer in effect.

**Screen time warnings**: Service toasts at **5** and **1** minutes remaining for daily total and per-app limits (`MaybeNotifyRemaining`; skip 5‑min warning if limit &lt; 5 minutes).

**Notifications**: `SessionNotifier` spawns `BlockerStub` in the user session (`CreateProcessAsUser`) and waits up to 2.5s for toast delivery. `BlockPageServer` calls `ShowWebsiteBlocked` on HTTP hits (port 80). Website toasts require HTTP; app toasts use IFEO stub on each launch.

`TrayManager` (`Services/TrayManager.cs`) wraps `System.Windows.Forms.NotifyIcon` — the UI project enables `<UseWindowsForms>true</UseWindowsForms>` for this. The window hides to tray on minimize and on close; Exit is only available from the tray context menu. Use `using Application = System.Windows.Application;` in any file that uses `Application` directly to resolve the WPF/WinForms ambiguity.

### Service logging

When running as a registered Windows Service (`WindowsServiceHelpers.IsWindowsService() == true`), the service clears the default providers and logs exclusively to Windows Event Log source `"FocusLock"`. During development (console mode), the default console logger is used. The event source is registered by the installer at `HKLM\SYSTEM\CurrentControlSet\Services\EventLog\Application\FocusLock`.

## Important Constraints

- **IFEO write/read requires SYSTEM** — always done in the Service, never the UI.
- **Strict mode**: apply service DACL + IFEO/hosts ACL locks after blocks are applied; cleanup reverses ACLs on session end.
- **`SystemProcessList.IsSystemExe`** must be checked in the UI before any exe is added to the block list, and in the Service before writing any IFEO key.
- The BlockerStub must always exit 0 and never throw — fail-open is essential since a hanging stub blocks the original process from showing any UI.
- Session end cleanup must be idempotent — the service may call it after a crash recovery where partial cleanup already happened.
