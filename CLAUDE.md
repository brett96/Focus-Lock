# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

```powershell
dotnet build FocusLock.sln           # build everything (src + tests; installer needs publish output first)
dotnet test                          # run all tests
dotnet test --filter "FullyQualifiedName~TestName"  # single test

# Full release build → produces FocusLockSetup.msi
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
3. Builds `installer/FocusLock.Installer/` → `FocusLockSetup.msi`

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
├── installer/FocusLock.Installer/  WiX v4 — produces FocusLockSetup.msi
└── tests/                       xUnit test projects
```

### How the pieces connect

**UI ↔ Service** communicate via a Named Pipe (`\\.\pipe\FocusLockService`). All privileged operations (registry writes, hosts file, admin account management) are done inside the Service running as SYSTEM — the UI sends IPC requests and receives responses. See `src/FocusLock.Core/Ipc/` for the protocol.

**App blocking** uses two layers:
1. IFEO (Image File Execution Options) registry redirect: `HKLM\...\Image File Execution Options\<app.exe>\Debugger` points to `FocusLock.BlockerStub.exe`. When Windows launches the blocked app, it runs the stub instead.
2. Process kill monitor: the service polls `Process.GetProcesses()` every 2 seconds and kills any blocked processes (catches renamed exes and already-running apps).

**BlockerStub**: When invoked via IFEO, connects to the Named Pipe, sends `IsBlocked { ExeName }`, and if the service confirms the block it shows a WinForms MessageBox and exits. Fails open (exits silently) if the service is unreachable.

**Website blocking**: Appends a sentinel block to `C:\Windows\System32\drivers\etc\hosts` with `0.0.0.0 domain.com` entries, then calls `ipconfig /flushdns`. On session end, restores from a backup file.

**Strict mode**: Creates a hidden local admin account (`FocusLockAdmin`), then demotes all other admin accounts to standard users. The session cannot be ended early. On deadline, the service automatically restores all demoted accounts and deletes `FocusLockAdmin`.

### Key files

| File | Role |
|---|---|
| `src/FocusLock.Core/Models/FocusSession.cs` | Central data model — every project depends on it |
| `src/FocusLock.Core/Ipc/Messages.cs` | All IPC request/response record types |
| `src/FocusLock.Core/Ipc/PipeFraming.cs` | Length-prefixed JSON wire framing |
| `src/FocusLock.Service/SessionWorker.cs` | Service entry point: pipe server, monitor loop, deadline watcher |
| `src/FocusLock.Service/Blocking/AppBlocker.cs` | IFEO writes, process kill, IFEO repair |
| `src/FocusLock.Service/Blocking/WebsiteBlocker.cs` | Hosts file management |
| `src/FocusLock.Service/Blocking/StrictModeManager.cs` | Admin account lifecycle (highest-risk code) |
| `src/FocusLock.Core/Blocking/SystemProcessList.cs` | Shared exclusion list — processes that must never be blocked |
| `src/FocusLock.UI/Services/ServiceClient.cs` | UI-side Named Pipe client wrapper |
| `src/FocusLock.UI/ViewModels/*.cs` | MVVM ViewModels using CommunityToolkit.Mvvm |

### IPC message types

All messages use the envelope `PipeMessage(Type, Payload)` where `Payload` is inner JSON. Message type constants are in `PipeConstants`. The service replies with `PipeMessage("Reply", <serialized response>)`.

Types: `GetStatus`, `GetSessionInfo`, `StartSession`, `EndSession`, `IsBlocked`.

### Session persistence

`FocusSession` is serialized to `C:\ProgramData\FocusLock\session.json` by the Service. On service startup, it loads this file and resumes or cleans up any active session. The `StrictPasswordBlob` field holds a DPAPI-encrypted password blob — never sent over IPC.

### Two session modes

- **Regular**: Service enforces blocks; existing admins can still stop the service to end the session (acceptable tradeoff).
- **Strict**: Service creates `FocusLockAdmin`, demotes all other admins before saving state to disk (crash-safe ordering), and enforces the session until the deadline regardless of user attempts. The service restores accounts automatically on deadline.

### UI structure

WPF MVVM app using `CommunityToolkit.Mvvm` (`[ObservableProperty]`, `[RelayCommand]`) and `Microsoft.Extensions.DependencyInjection`. Navigation via `NavigationService` swapping `Page` instances in `MainWindow`'s `Frame`. DI container registered in `App.xaml.cs`.

Pages: `DashboardPage` (status + countdown + ⚙ settings button), `SetupPage` (new session wizard), `ActiveSessionPage` (read-only live view), `SettingsPage` (Start-with-Windows toggle, tray info).

`TrayManager` (`Services/TrayManager.cs`) wraps `System.Windows.Forms.NotifyIcon` — the UI project enables `<UseWindowsForms>true</UseWindowsForms>` for this. The window hides to tray on minimize and on close; Exit is only available from the tray context menu. Use `using Application = System.Windows.Application;` in any file that uses `Application` directly to resolve the WPF/WinForms ambiguity.

### Service logging

When running as a registered Windows Service (`WindowsServiceHelpers.IsWindowsService() == true`), the service clears the default providers and logs exclusively to Windows Event Log source `"FocusLock"`. During development (console mode), the default console logger is used. The event source is registered by the installer at `HKLM\SYSTEM\CurrentControlSet\Services\EventLog\Application\FocusLock`.

## Important Constraints

- **IFEO write/read requires SYSTEM** — always done in the Service, never the UI.
- **Strict mode operation order is critical**: create account → add to Admins → hide → save session.json → demote others. Do not reorder.
- **`SystemProcessList.IsSystemExe`** must be checked in the UI before any exe is added to the block list, and in the Service before writing any IFEO key.
- The BlockerStub must always exit 0 and never throw — fail-open is essential since a hanging stub blocks the original process from showing any UI.
- Session end cleanup must be idempotent — the service may call it after a crash recovery where partial cleanup already happened.
