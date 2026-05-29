# Focus Lock

**Current version:** 1.1.0

Focus Lock is a **Windows self-focus assistant app**: you start a timed “focus session” that **blocks chosen apps and websites until a deadline**. The app is designed so that the **privileged enforcement** happens in a Windows Service running as **LocalSystem**, while the UI is a normal desktop app that talks to the service over IPC.

## What it does

- **App blocking**
  - Uses **IFEO (Image File Execution Options)** to redirect launches of blocked executables to a tiny stub (`FocusLock.BlockerStub.exe`).
  - When a blocked app is launched, a **Windows toast** appears (via `BlockerStub` over IFEO). Each launch uses a unique toast tag so repeat attempts after dismissing still notify. IPC to the service retries on timeout.
  - The service also runs a **process monitor loop** that kills blocked processes every ~2 seconds (helps catch renamed exes and already-running apps).
  - Apps are selected from a searchable dropdown populated from the Windows **Installed applications** list (Uninstall registry — same source as Settings → Apps). One entry per application name; blocking applies to all related executables for that app. System-critical Windows processes and apps under `Windows\System32` cannot be blocked. A Browse button handles apps not in the list.
- **Website blocking**
  - The service appends a sentinel block to the Windows **hosts file** (`C:\Windows\System32\drivers\etc\hosts`), redirecting blocked domains to `127.0.0.1`, and flushes DNS.
  - A lightweight HTTP server runs on port 80 for the duration of the session. When a blocked site is visited over **HTTP**, the browser shows a styled block page and a **Windows toast** is shown via `BlockerStub` (debounced ~5 seconds per domain). **HTTPS** visits are still blocked via the hosts file but cannot show the block page or toast without TLS interception.
- **Session enforcement**
  - Sessions are persisted to `C:\ProgramData\FocusLock\session.json` so the service can **recover after restart/crash** and keep enforcing until the deadline.
  - **Service protection** (regular and strict sessions): at session start, both `FocusLockService` and `FocusLockWatchdog` get SCM DACLs that deny stop, pause, and reconfiguration to **Administrators and LocalSystem** (blocks `sc stop` and services.msc even from a PsExec SYSTEM prompt). Poll loops re-apply DACLs if tampered. Each service polls every ~1.5s and restarts the other if killed. **Do not** use `sc stop` on these services while protection is active on older builds that marked the process critical — that can blue-screen Windows; use `scripts\Unlock-StuckServices.ps1` instead.
- **Strict mode**
  - Locks the session so it **cannot be ended early**. Before starting, the UI shows a warning panel and requires the user to check an explicit consent checkbox.
  - Adds two more layers on top of service protection:
    1. **IFEO registry ACLs** — each Image File Execution Options key written for the session gets a deny-write ACL for `Administrators`, preventing manual deletion or modification. The service (running as LocalSystem) can still repair keys via the monitor loop.
    2. **Hosts file ACL** — the hosts file gets a deny-write ACL for `Administrators`, preventing the sentinel block from being manually removed. The service retains full control so it can restore the file on session end.
  - The user keeps their admin rights for all other purposes — no risk of being locked out of their own machine.
- **Screen Time Limits** *(optional; enforced only during an active Focus Lock session)*
  - Limits are configured in the **New Focus Session** wizard or on the **Screen Time** settings page (saved ahead of time) and **only apply while a focus session is running** — same lifecycle as app/website blocking. When the session ends, counters reset and enforcement stops.
  - **Daily screen time limit** — maximum logged-in time during the session. You can add **multiple daily limits** with different day/time schedules (e.g. weekdays 9–5 vs. Saturday morning). Overlapping daily schedules are rejected at setup. Each limit row has **Edit** and **Delete**; Edit loads the limit into the form and **Save** preserves its ID.
  - **Schedule-based activation** — limits can be restricted to specific days of the week and/or a time-of-day window (e.g. Monday–Friday 9 AM–5 PM). Outside the schedule window, limits do not accumulate or enforce.
  - **Per-app time limits** — each app can have its own limit and optional schedule (defaults to always on). These do **not** inherit the device daily-limit schedule unless you set a per-app schedule. Multiple limits for the same app are allowed when their schedules do not overlap. Each row supports **Edit** and **Delete** like daily limits.
    - *Daily Total*: at most X minutes of use within the app's schedule window during the session.
    - *Interval*: at most X minutes per Y-minute interval within the app's schedule window.
  - Usage is tracked while the app's process is running (or in the foreground). The dashboard polls the service every second during an active session.
  - **Website categories** — block preset groups of sites (Adult, Entertainment, Social, etc.) from the session setup screen.
  - Configuration persists in `C:\ProgramData\FocusLock\screen-time-config.json`. Usage counters are session-scoped (reset when a session starts and cleared when it ends).
  - During an active session, the **dashboard** shows live countdowns for the session deadline, screen-time usage, remaining quota per limit, interval-reset times for interval-based app limits, and scrollable lists of blocked apps/sites.
  - When the daily limit uses a **schedule window**, the dashboard shows that window and whether tracking is active now. With **multiple daily limits on the same day**, the dashboard highlights one rule at a time: the **currently active** window first; if none is active, the **next upcoming** window today (“Next daily limit window” / “Starts at …”); if all of today’s windows have ended, the **most recently ended** window (“Last window ended at …”). Outside any window, the remaining-time line shows when tracking resumes.
  - **Warning toasts** at 5 and 1 minutes remaining for the daily total and per-app limits (5‑minute warning skipped if the limit is under 5 minutes).
  - **Bedtime warning toasts** at 5 and 1 minutes before a bedtime window starts (during an active session).
  - When a screen-time-limited app is blocked, or a site is blocked over HTTP, the user gets a **native notification** (via `BlockerStub` and `IsBlockedResponse.BlockMessage`).
- **Bedtimes** *(optional; enforced only during an active Focus Lock session)*
  - Add **multiple bedtime schedules** on the **Screen Time** settings page or in the session setup wizard. Each bedtime is a day/time range (overnight ranges supported, e.g. 10 PM–7 AM). **Edit** and **Delete** on each row.
  - While a **focus session is running** and a bedtime window is active, the service **signs the user out** (`WTSDisconnectSession`) and blocks re-login until the window ends. Re-login during an active bedtime repeats the sign-out flow.
  - Bedtimes **cannot overlap** each other or any daily/app screen time limit schedule (same overlap rules as other screen time restrictions).
  - During an active session, the dashboard lists **upcoming bedtimes** before the session deadline and shows when a bedtime is **active now**. Outside a session, bedtimes are not enforced.
- **Session setup (New Focus Session wizard)**
  - Default end time is **one hour from now** (editable with date picker + `TimePartSpinBox` hour/minute spinners).
  - End date must be **at least 5 minutes** in the future and **no more than one year** ahead (enforced in UI and service).
  - **Blocked apps and websites are optional** if you enable a daily screen-time limit and/or add per-app time limits.
  - **Website categories** — preset domain groups (Adult, Entertainment, Social, etc.) can be toggled on the setup screen.
  - **Screen time limits** — add, **edit**, or remove daily, per-app, and **bedtime** schedules in the wizard; overlap validation runs on add/save (the rule being edited is excluded from the check).
  - At least one restriction is required to start: blocked apps, blocked sites, or screen time limits.
  - **Start Session** shows a Yes/No confirmation listing the session end date/time, mode (Regular or Strict), counts of blocked apps and websites, and details of screen time limits and bedtimes before the session is sent to the service.
- **Dashboard UX**
  - Main active-session view is `DashboardPage` (not `ActiveSessionPage`).
  - **End Session Early** (regular mode only) shows a Yes/No confirmation, then the same ending progress UI until the session is fully idle.
  - When the **countdown reaches zero**, the same ending progress UI appears while the service cleans up (no second click needed).
  - End-session uses retries on the IPC call and polls `GetSessionInfo` until idle so a second attempt is not required.
  - Idle/active panels stay stable during normal background refresh (no “Loading session…” flicker).
  - The “Cannot reach Focus Lock Service” banner only appears after **several consecutive** failed polls (not a single timeout). IPC calls retry automatically; successful screen-time polls also clear a transient warning.

## Tech stack

- **Language/runtime**: C# on **.NET 9** (`net9.0-windows`)
- **UI**: **WPF** (MVVM via `CommunityToolkit.Mvvm`), plus a tray icon implemented with `System.Windows.Forms.NotifyIcon`
- **Service**: **Windows Service** built on `Microsoft.Extensions.Hosting` (`Microsoft.NET.Sdk.Worker`)
- **IPC**: **Named Pipes** (`\\.\pipe\FocusLockService`) with a length-prefixed JSON protocol. The pipe ACL does **not** grant Everyone access; privileged commands require an administrator token. Client reads time out after 3 seconds to avoid connection exhaustion.
- **Installer**: **WiX Toolset v5** via `WixToolset.Sdk` (MSI output)
- **Tests**: xUnit test projects under `tests/`

## Repository layout

```
FocusLock.sln
src/
  FocusLock.Core/         Shared models, IPC protocol, persistence helpers
  FocusLock.Service/      Windows Service (runs as LocalSystem)
  FocusLock.Watchdog/     Session watchdog service (runs as LocalSystem)
  FocusLock.UI/           WPF app (requires admin to run meaningfully)
  FocusLock.BlockerStub/  Small WinForms exe invoked by IFEO
installer/
  FocusLock.Installer/    WiX project producing Focus Lock.msi
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
| `screen-time-state.json` | Session usage counters (written during active sessions; cleared on session end) |

## How it’s built (high-level architecture)

### UI ↔ Service (IPC)

- The **UI** opens a new Named Pipe connection per request and sends a `PipeMessage(Type, Payload)` envelope.
- The **Service** hosts a Named Pipe server and dispatches message types:
  - `GetStatus`, `GetSessionInfo`, `StartSession`, `EndSession`, `IsBlocked`

The wire framing is **4-byte little-endian length prefix + UTF-8 JSON**, implemented in `src/FocusLock.Core/Ipc/PipeFraming.cs`.

### Service runtime loops

The service runs four loops concurrently:

- **Pipe server**: handles UI/stub requests
- **App monitor** (`AppMonitorWorker`): kills blocked processes, repairs IFEO keys, verifies screen-time IFEO
- **Deadline watcher** (`DeadlineWatcherWorker`): ends the session automatically when the deadline is reached
- **Screen Time tracker** (`ScreenTimeManager`): 1-second tick loop while a focus session is active — bedtimes (sign-out), daily limits (`WTSDisconnectSession`), and per-app IFEO blocks when quotas are exceeded.

### Blocker stub (IFEO target)

When Windows launches a blocked app, IFEO redirects it to `FocusLock.BlockerStub.exe`. The stub asks the service `IsBlocked { ExeName }` and:

- shows a message and exits when blocked
- **fails open** (exits silently) if the service can’t be reached

The stub also accepts `--notify <domain> <deadline>` (website block popup) and `--message <title> <body>` (generic notification, used by Screen Time when the daily limit is reached).

## Build, test, and release

### Versioning

**Bump the version with every user-facing update** (features, fixes, installer changes) before building a release MSI.

The canonical version lives in **`Version.props`** at the repository root:

| Property | Example | Used for |
|----------|---------|----------|
| `FocusLockVersion` | `1.1.0` | Semver (docs, informational version) |
| `FocusLockAssemblyVersion` | `1.1.0.0` | Assemblies, file version, MSI `Package/@Version` |

**Semver rules (consistent bumps):**

| Change type | Bump | Example |
|-------------|------|---------|
| Bug fixes only, no new behavior | **PATCH** | `1.1.0` → `1.1.1` |
| New features, backward compatible | **MINOR** (reset patch to `0`) | `1.1.2` → `1.2.0` |
| Breaking changes or major redesign | **MAJOR** (reset minor/patch to `0`) | `1.2.3` → `2.0.0` |

When bumping, update **both** properties in `Version.props` so the semver and four-part values stay aligned (`1.2.0` ↔ `1.2.0.0`).

**What updates automatically** (via `Version.props` imports):

- All projects under `src/` (`src/Directory.Build.props` → assembly/file/informational version)
- Test projects and the WiX installer (repo-root `Directory.Build.props` / `FocusLock.Installer.wixproj` → MSI version)

**Manual step after editing `Version.props`:**

- Sync `src/FocusLock.UI/app.manifest` → `<assemblyIdentity version="…" />` must match `FocusLockAssemblyVersion` (four parts).

**Verify after bumping:**

```powershell
dotnet build FocusLock.sln -c Release
# Optional: inspect a built exe
[System.Diagnostics.FileVersionInfo]::GetVersionInfo("src\FocusLock.UI\bin\Release\net9.0-windows10.0.17763.0\FocusLock.UI.exe").FileVersion
```

Then run `.\build\build.ps1` to produce the versioned MSI.

### Prerequisites

- Windows 10 version **1809** (build 17763) or later, or Windows 11 (**64-bit**)
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9) (for building)
- [.NET 9 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/9.0) on machines running the installed app — the MSI checks for this before setup

**Windows 10 install issues:** Confirm the PC is on build **17763+** (`Settings` → `System` → `About`) and that the **.NET Desktop Runtime (x64), 9.0 or newer**, is installed — not only the ASP.NET Core runtime. The MSI uses `DotNetCompatibilityCheck` with roll-forward, so **.NET 10** is acceptable; published apps also roll forward to newer major runtimes. If install fails with *“Focus Lock Service failed to start”* after prerequisites look correct, check **Event Viewer → Windows Logs → Application** for `.NET Runtime` errors (often “framework 9.0.0 not found” when roll-forward was missing in older builds).

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

`build.ps1` publishes four executables, then builds the MSI:
1. Publishes `FocusLock.UI`, `FocusLock.BlockerStub`, `FocusLock.Service`, and `FocusLock.Watchdog` to `publish\FocusLock\`
2. Moves `FocusLock.Service.exe` to `publish\service-exe\` and `FocusLock.Watchdog.exe` to `publish\watchdog-exe\` (required so WiX can attach `ServiceInstall` without a glob conflict)
3. Builds the WiX installer → `installer\FocusLock.Installer\bin\Release\Focus Lock.msi`
4. **Code-signs** published executables and the MSI when `signtool` and a signing cert are available (`build.ps1` creates/trusts a dev self-signed cert). The MSI is signed with `/d "Focus Lock"` so the UAC prompt shows that name instead of a random cached filename under `C:\Windows\Installer\`.

Debug variant:

```powershell
.\build\build.ps1 -Configuration Debug
```

## Developer workflow

### Running the UI during development

The UI runs at the normal user privilege level (`asInvoker`); privileged work is done by the service. For development:

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

Then reinstall the updated MSI to pick up the changes in the installed application (see **Upgrading an existing install** below).

### Upgrading an existing install

Running a newer `Focus Lock.msi` over an older install **replaces it in place** — it does **not** install a second copy under a different folder or leave two entries in Settings → Apps.

How it works:

1. All releases share the same **`UpgradeCode`** (never change this GUID).
2. Each release has a unique **`ProductCode`** and a higher **`Package` version** (from `Version.props`).
3. WiX **`MajorUpgrade`** (`Schedule="afterInstallValidate"`) detects the older product, **fully uninstalls 1.0.0** (stops and removes `FocusLockService`, replaces files under `C:\Program Files\FocusLock\`), then installs 1.1.0.

User data in `C:\ProgramData\FocusLock\` is preserved (session/screen-time config components are not removed on upgrade).

### Uninstall or upgrade fails with “could not be stopped”

During an active focus session, the installer is blocked from stopping `FocusLockService` because service DACLs deny **stop** to Administrators (this is intentional session protection; it is **not** the watchdog being marked critical).

**MSI from a build that includes the unlock custom action** runs `FocusLock.Service.exe --unlock-for-setup` automatically before stopping services.

If you are stuck on an older build:

1. **Build or copy a current `FocusLock.Service.exe`** (the unlock logic must match the installed service).
2. Run in **Administrator PowerShell or CMD** (either is fine):

```powershell
& "C:\Program Files\FocusLock\FocusLock.Service.exe" --unlock-for-setup
```

The tool first asks the **running service** (over the named pipe) to end any session and remove protection safely as LocalSystem. That avoids “Access denied” when clearing strict-mode critical flags from an external admin process.

If you see **Permission denied** and the PC **blue-screens** when the command exits, an older unlock build tried to **stop a process that was still marked critical** — do not run that old command again. Reboot, deploy the updated `FocusLock.Service.exe`, ensure the **Focus Lock Service** is **Running** in `services.msc`, then run `--unlock-for-setup` once more before uninstalling.

If `sc.exe stop FocusLockWatchdog` returns **Access is denied**, the installed service is still an older build and may be re-applying stop-denies. Run `--unlock-for-setup` from a **new build** (project root commands above), or reset DACLs manually **as Administrator**:

From the repo root, in **Administrator PowerShell**:

```powershell
.\scripts\Unlock-StuckServices.ps1
```

**Do not** chain `sc.exe stop` after `sdset` — on older strict-mode builds that can **blue-screen** the PC. Use `.\scripts\Unlock-StuckServices.ps1`, which disables the services until reboot instead of stopping them in place.

To upgrade from 1.0.0 to 1.1.0: run the 1.1.0 MSI as administrator — no manual uninstall required. If upgrade fails (e.g. service locked), stop the service first:

```powershell
sc stop FocusLockService
```

Then run the MSI again.

### End-to-end testing with the service

To test the full stack (UI + service enforcing blocks):

1. Run `.\build\build.ps1` to produce a fresh MSI.
2. Install `installer\FocusLock.Installer\bin\Release\Focus Lock.msi`.
   - This installs everything to `C:\Program Files\FocusLock\` and starts the service automatically.
   - The source code directory is **not needed** after installation.
3. Launch **Focus Lock** from the Start menu (UAC prompt expected).
4. To update after further code changes: run `build.ps1` again and reinstall the MSI (in-place upgrade).

### The MSI is the standalone distributable

`Focus Lock.msi` is a self-contained installer — it bundles all executables and DLLs. The only external prerequisite is the **.NET 9 Desktop Runtime**, which must be installed on the target machine before running the MSI. The installer will show an error and abort if the runtime is not present.

## Operational notes / safety

- **Windows-only**: the UI/service rely on Windows APIs (service control manager, registry IFEO, hosts file).
- **Admin/system privileges**:
  - The service is intended to run as a **Windows Service** under **LocalSystem**.
  - The UI requires admin rights to run meaningfully (it talks to the service, and the overall app is a machine-wide enforcement tool).
- **Don’t block critical processes**: `FocusLock.Core/Blocking/SystemProcessList.cs` defines an exclusion list to prevent locking out core Windows components.

