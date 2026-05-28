# Focus Lock

Focus Lock is a **Windows self-parental-control app**: you start a timed “focus session” that **blocks chosen apps and websites until a deadline**. The app is designed so that the **privileged enforcement** happens in a Windows Service running as **LocalSystem**, while the UI is a normal desktop app that talks to the service over IPC.

## What it does

- **App blocking**
  - Uses **IFEO (Image File Execution Options)** to redirect launches of blocked executables to a tiny stub (`FocusLock.BlockerStub.exe`).
  - The service also runs a **process monitor loop** that kills blocked processes every ~2 seconds (helps catch renamed exes and already-running apps).
- **Website blocking**
  - The service appends a sentinel block to the Windows **hosts file** (`C:\Windows\System32\drivers\etc\hosts`) and flushes DNS.
- **Session enforcement**
  - Sessions are persisted to `C:\ProgramData\FocusLock\session.json` so the service can **recover after restart/crash** and keep enforcing until the deadline.
- **Strict mode**
  - Creates a hidden local admin account (`FocusLockAdmin`) and demotes other admins to standard users so the session **cannot be ended early**. On deadline, the service restores accounts and deletes the strict-mode admin.

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

## How it’s built (high-level architecture)

### UI ↔ Service (IPC)

- The **UI** opens a new Named Pipe connection per request and sends a `PipeMessage(Type, Payload)` envelope.
- The **Service** hosts a Named Pipe server and dispatches message types:
  - `GetStatus`, `GetSessionInfo`, `StartSession`, `EndSession`, `IsBlocked`

The wire framing is **4-byte little-endian length prefix + UTF-8 JSON**, implemented in `src/FocusLock.Core/Ipc/PipeFraming.cs`.

### Service runtime loops

The service runs three loops concurrently:

- **Pipe server**: handles UI/stub requests
- **Monitor loop**: kills blocked processes and repairs IFEO keys
- **Deadline watcher**: ends the session automatically when the deadline is reached

### Blocker stub (IFEO target)

When Windows launches a blocked app, IFEO redirects it to `FocusLock.BlockerStub.exe`. The stub asks the service `IsBlocked { ExeName }` and:

- shows a message and exits when blocked
- **fails open** (exits silently) if the service can’t be reached

## Build, test, and release

### Prerequisites

- Windows machine
- .NET SDK installed (projects target **.NET 9**)

### Build everything (source)

```powershell
dotnet build FocusLock.sln
```

### Run tests

```powershell
dotnet test
```

### Full release build (publishes + MSI)

This publishes the apps to `publish/` and then builds the WiX installer.

```powershell
.\build\build.ps1
```

Debug variant:

```powershell
.\build\build.ps1 -Configuration Debug
```

After a successful run, the MSI is under `installer/FocusLock.Installer/bin/<Configuration>/` (the script prints the exact path).

## Operational notes / safety

- **Windows-only**: the UI/service rely on Windows APIs (service control manager, registry IFEO, hosts file).
- **Admin/system privileges**:
  - The service is intended to run as a **Windows Service** under **LocalSystem**.
  - The UI requires admin rights to run meaningfully (it talks to the service, and the overall app is a machine-wide enforcement tool).
- **Don’t block critical processes**: `FocusLock.Core/Blocking/SystemProcessList.cs` defines an exclusion list to prevent locking out core Windows components.

