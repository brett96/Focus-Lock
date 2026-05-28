# Focus Lock

Focus Lock is a **Windows self-parental-control app**: you start a timed “focus session” that **blocks chosen apps and websites until a deadline**. The app is designed so that the **privileged enforcement** happens in a Windows Service running as **LocalSystem**, while the UI is a normal desktop app that talks to the service over IPC.

## What it does

- **App blocking**
  - Uses **IFEO (Image File Execution Options)** to redirect launches of blocked executables to a tiny stub (`FocusLock.BlockerStub.exe`).
  - When a blocked app is launched, a native Windows dialog appears: "Focus Lock has blocked access to [App]."
  - The service also runs a **process monitor loop** that kills blocked processes every ~2 seconds (helps catch renamed exes and already-running apps).
  - Apps are selected from a searchable dropdown of all installed applications (populated from the Windows App Paths registry); a Browse button handles apps not in the list.
- **Website blocking**
  - The service appends a sentinel block to the Windows **hosts file** (`C:\Windows\System32\drivers\etc\hosts`), redirecting blocked domains to `127.0.0.1`, and flushes DNS.
  - A lightweight HTTP server runs on port 80 for the duration of the session. When a blocked site is visited over HTTP, the browser shows a styled block page and a native Windows dialog popup appears ("Focus Lock has blocked access to this site"). Popup is debounced per domain (30-second cooldown). HTTPS connections are refused (still effectively blocked) but cannot show the styled page without a browser certificate.
- **Session enforcement**
  - Sessions are persisted to `C:\ProgramData\FocusLock\session.json` so the service can **recover after restart/crash** and keep enforcing until the deadline.
- **Strict mode**
  - Locks the session so it **cannot be ended early**. Before starting, the UI shows a warning panel and requires the user to check an explicit consent checkbox.
  - Three layers of enforcement are applied at session start and removed automatically on deadline:
    1. **Service DACL** — the service modifies its own Windows security descriptor to deny `SERVICE_STOP` and `SERVICE_PAUSE_CONTINUE` to `BUILTIN\Administrators`. `NT AUTHORITY\SYSTEM` (LocalSystem) is a distinct SID and is unaffected, so the service still stops cleanly on system shutdown and restarts normally.
    2. **IFEO registry ACLs** — each Image File Execution Options key written for the session gets a deny-write ACL for `Administrators`, preventing manual deletion or modification. The service (running as LocalSystem) can still repair keys via the monitor loop.
    3. **Hosts file ACL** — the hosts file gets a deny-write ACL for `Administrators`, preventing the sentinel block from being manually removed. The service retains full control so it can restore the file on session end.
  - The user keeps their admin rights for all other purposes — no risk of being locked out of their own machine.
- **Screen Time Limits** *(optional, independent of Focus Lock sessions)*
  - **Daily screen time limit** — set a maximum number of minutes the user can be logged into Windows per day. When the limit is reached the service disconnects the active session; every subsequent login for the rest of the day is disconnected after a 5-second notification, then resets at midnight.
  - **Schedule-based activation** — limits can be restricted to specific days of the week and/or a time-of-day window (e.g. Monday–Friday 9 AM–5 PM). Outside the schedule the limits are inactive and no enforcement occurs.
  - **Per-app time limits** — each app can have its own independent limit and schedule, with two enforcement modes:
    - *Daily Total*: the app may be used for at most X minutes within the schedule window each day. Once the quota is used the app is force-closed for the remainder of that window; it is unlocked automatically when the schedule ends (e.g. blocked at 2 PM under a 9 AM–5 PM schedule → unlocked at 5 PM; counter resets the next day).
    - *Interval*: the app may be used for at most X minutes per Y-minute interval, measured from the schedule start. When the quota for an interval is exhausted the app is force-closed until the next interval begins.
  - Each app limit can optionally override the global schedule with its own custom days/hours.
  - Configuration is saved to `C:\ProgramData\FocusLock\screen-time-config.json`; daily usage counters are saved to `screen-time-state.json` and reset automatically at midnight.
  - Screen Time is configured via the **⏱** button on the dashboard.

## Tech stack

- **Language/runtime**: C# on **.NET 9** (`net9.0-windows`)
- **UI**: **WPF** (MVVM via `CommunityToolkit.Mvvm`), plus a tray icon implemented with `System.Windows.Forms.NotifyIcon`
- **Service**: **Windows Service** built on `Microsoft.Extensions.Hosting` (`Microsoft.NET.Sdk.Worker`)
- **IPC**: **Named Pipes** (`\\.\pipe\FocusLockService`) with a length-prefixed JSON protocol
- **Installer**: **WiX Toolset v5** via `WixToolset.Sdk` (MSI output)
- **Tests**: xUnit test projects under `tests/`

## Repository layout

```
FocusLock.sln
src/
  FocusLock.Core/         Shared models, IPC protocol, persistence helpers
  FocusLock.Service/      Windows Service (runs as LocalSystem)
  FocusLock.UI/           WPF app (requires admin to run meaningfully)
  FocusLock.BlockerStub/  Small WinForms exe invoked by IFEO
installer/
  FocusLock.Installer/    WiX project producing FocusLockSetup.msi
tests/
  FocusLock.Core.Tests/
  FocusLock.Service.Tests/
build/
  build.ps1               Full publish + installer build orchestrator
```

Key data files written by the service to `C:\ProgramData\FocusLock\`:

| File | Contents |
|---|---|
| `session.json` | Active Focus Lock session (apps/sites blocked, deadline, mode) |
| `screen-time-config.json` | Screen Time limit configuration (persists across days) |
| `screen-time-state.json` | Today's usage counters; reset automatically at midnight |

## How it’s built (high-level architecture)

### UI ↔ Service (IPC)

- The **UI** opens a new Named Pipe connection per request and sends a `PipeMessage(Type, Payload)` envelope.
- The **Service** hosts a Named Pipe server and dispatches message types:
  - `GetStatus`, `GetSessionInfo`, `StartSession`, `EndSession`, `IsBlocked`

The wire framing is **4-byte little-endian length prefix + UTF-8 JSON**, implemented in `src/FocusLock.Core/Ipc/PipeFraming.cs`.

### Service runtime loops

The service runs four loops concurrently:

- **Pipe server**: handles UI/stub requests
- **Monitor loop**: kills blocked processes and repairs IFEO keys
- **Deadline watcher**: ends the session automatically when the deadline is reached
- **Screen Time tracker**: 1-second tick loop that accumulates user session time, enforces daily limits (via `WTSDisconnectSession`), and force-closes apps that have exceeded their daily or interval quota

### Blocker stub (IFEO target)

When Windows launches a blocked app, IFEO redirects it to `FocusLock.BlockerStub.exe`. The stub asks the service `IsBlocked { ExeName }` and:

- shows a message and exits when blocked
- **fails open** (exits silently) if the service can’t be reached

The stub also accepts `--notify <domain> <deadline>` (website block popup) and `--message <title> <body>` (generic notification, used by Screen Time when the daily limit is reached).

## Build, test, and release

### Prerequisites

- Windows 10 or 11
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9) (for building)
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9) (for running — the SDK includes this)

### Build everything (source only, no installer)

```powershell
dotnet build FocusLock.sln
```

### Run tests

```powershell
dotnet test

# Single test by name:
dotnet test --filter "FullyQualifiedName~MyTestName"
```

### Full release build — publishes all apps + produces the MSI

```powershell
.\build\build.ps1
```

`build.ps1` does three things in order:
1. Publishes `FocusLock.UI`, `FocusLock.BlockerStub`, and `FocusLock.Service` to `publish\FocusLock\`
2. Moves `FocusLock.Service.exe` to `publish\service-exe\` (required so WiX can attach `ServiceInstall` without a glob conflict)
3. Builds the WiX installer → `installer\FocusLock.Installer\bin\Release\FocusLockSetup.msi`

Debug variant:

```powershell
.\build\build.ps1 -Configuration Debug
```

## Developer workflow

### Running the UI during development

The UI requires administrator rights (it is declared `requireAdministrator` in its manifest). **Open your terminal as Administrator**, then:

```powershell
dotnet run --project src\FocusLock.UI
```

This compiles from source and launches the WPF window in-place. Because the Windows Service is not installed in a development environment, the dashboard will show "Cannot reach Focus Lock Service" — that is expected. All UI layout and navigation changes are fully visible without the service.

> **Note:** `dotnet run` compiles into `src\FocusLock.UI\bin\Debug\` and never touches the `publish\` directory. It does not update the MSI or the installed application.

### Updating the published executables

After editing code, rebuild the publish output and installer with:

```powershell
.\build\build.ps1
```

Then reinstall the updated MSI to pick up the changes in the installed application.

### End-to-end testing with the service

To test the full stack (UI + service enforcing blocks):

1. Run `.\build\build.ps1` to produce a fresh MSI.
2. Install `installer\FocusLock.Installer\bin\Release\FocusLockSetup.msi`.
   - This installs everything to `C:\Program Files\FocusLock\` and starts the service automatically.
   - The source code directory is **not needed** after installation.
3. Launch **Focus Lock** from the Start menu (UAC prompt expected).
4. To update after further code changes: run `build.ps1` again and reinstall the MSI.

### The MSI is the standalone distributable

`FocusLockSetup.msi` is a self-contained installer — it bundles all executables and DLLs. The only external prerequisite is the **.NET 9 Desktop Runtime**, which must be installed on the target machine before running the MSI. The installer will show an error and abort if the runtime is not present.

## Operational notes / safety

- **Windows-only**: the UI/service rely on Windows APIs (service control manager, registry IFEO, hosts file).
- **Admin/system privileges**:
  - The service is intended to run as a **Windows Service** under **LocalSystem**.
  - The UI requires admin rights to run meaningfully (it talks to the service, and the overall app is a machine-wide enforcement tool).
- **Don’t block critical processes**: `FocusLock.Core/Blocking/SystemProcessList.cs` defines an exclusion list to prevent locking out core Windows components.

