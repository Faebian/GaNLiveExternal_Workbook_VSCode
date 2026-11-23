# GaNLiveExternal

**GaNLiveExternal** is an external UnicornOne workbook plugin for live GaN calibration and growth workflows. It packages the existing GaNLive form and task manager into a separately built DLL rather than changing the built-in **GaN Calibrate Live** workbook.

The project is a **C# 7.3 / .NET Framework 4.7.2 Windows Forms class library**, intended to be edited in **VS Code** and built with **Visual Studio 2022 MSBuild**. It is a full-source external-workbook migration pilot, **not** a standalone simulator or standalone executable.

## Features

- Original GaNLive calibration and growth workflow, including configuration, Check, Run, subtasks, cancellation and reset.
- Embedded RHEED widget, intensity tracing, calibration messages and controls.
- RHEED recording and related calibration/intensity exports through UnicornOne's existing services.
- Workbook-host integration through `IWorkbookPlugin` and `IWorkbookController`.
- Independent `GaNLiveExternal.dll` build and deployment, without modifying UnicornOne source code.

## Project layout

```text
GaNLiveExternal_Workbook_VSCode/
|-- .vscode/
|   `-- tasks.json
|-- src/
|   |-- GaNLiveExternalWorkbook.cs     # Workbook plugin and lifecycle adapter
|   `-- GaNLive/
|       |-- GaNLiveForm.cs              # WinForms workflow and UI
|       |-- GaNLiveForm.Designer.cs
|       `-- GaNLiveManager.cs           # Calibration and growth task logic
|-- GaNLiveExternal.csproj
|-- GaNLiveExternal.code-workspace
|-- Build-Deploy.ps1
|-- README.md
`-- .gitignore
```

`bin/` and `obj/` are generated locally and should not be committed.

## Requirements

- Windows, with Visual Studio 2022 or MSBuild Build Tools.
- .NET Framework **4.7.2 targeting/developer pack**.
- A built, compatible UnicornOne source tree containing:
  - `UnicornOne.Abstractions.dll`
  - `UnicornOne.Platform.dll`
  - `UnicornOne.Core.dll`
  - `Newtonsoft.Json.dll`
- A UnicornOne runtime with external workbook plugin discovery enabled.

**Dependency boundary:** The plugin currently references `UnicornOne.Core.dll` for the shared RHEED widget and WinForms controls, and `UnicornOne.Platform.dll` for device and recording services. It is external to the application, but is **not yet API-only or binary-independent** of those assemblies. Rebuild it when those dependencies change incompatibly.

## Build in VS Code

1. Build the main UnicornOne solution first so the required DLLs exist.
2. Open `GaNLiveExternal.code-workspace` in VS Code.
3. Press **Ctrl+Shift+B** to run the default **Build GaNLive External DLL** task, or run the script below from PowerShell.

```powershell
# Validate the MSBuild and UnicornOne dependencies; do not build/deploy
.\Build-Deploy.ps1 -CheckOnly

# Build without deploying
.\Build-Deploy.ps1

# Build and deploy to the configured plugin folder
.\Build-Deploy.ps1 -Deploy
```

The default UnicornOne source root configured by the project/script is:

```text
C:\Users\Bizmuth\source\repos\UnicornOne
```

If your checkout is elsewhere, pass a different path:

```powershell
.\Build-Deploy.ps1 -UnicornOneRoot 'D:\Source\UnicornOne' -Deploy
```

The script locates VS2022 MSBuild, checks the required compiled assemblies, builds `GaNLiveExternal.csproj`, and optionally deploys the DLL. `-CheckOnly` makes no changes. For Release builds use `-Configuration Release` (the matching UnicornOne reference assemblies must also exist).

## Deployment and loading

The script's default deployment destination is:

```text
C:\UnicornOne\Plugins\Workbooks\GaNLiveExternal.dll
```

**Important:** The runtime's workbook loader searches its own application base folder under `Plugins\Workbooks`. If launching a VS2022 Debug build rather than the installed application, specify its actual plugin folder:

```powershell
.\Build-Deploy.ps1 -Deploy -PluginDir 'C:\Users\Bizmuth\source\repos\UnicornOne\UnicornOne.RunTime\bin\Debug\Plugins\Workbooks'
```

Restart UnicornOne after replacing the DLL; loaded .NET Framework assemblies are not hot-swapped. Then open the external workbook from **Workbooks → + → Plugin → GaN Calibrate Live (External)** (the exact menu presentation depends on the host version).

The deploy script backs up an existing deployed DLL with a timestamped `.bak` suffix. It does **not** deploy or modify any UnicornOne source files.

## Workbook JSON configuration

The plugin accepts a workbook JSON configuration such as:

```json
{
  "RunMode": "Full",
  "WorkbookName": "GaN Calibrate Live"
}
```

| Property | Default | Purpose |
| --- | --- | --- |
| `RunMode` | `Full` | Passed through to the GaNLive form. |
| `WorkbookName` | `GaN Calibrate Live` | Identifies the workbook's existing configuration and output context. |

The default name intentionally matches the built-in workbook. Use a distinct `WorkbookName` to isolate settings and output for comparative testing. **Do not run the internal and external GaNLive workflows simultaneously**; both may interact with the same equipment and runtime services.

## Safe validation sequence

1. Start UnicornOne in **Simulation Mode** and load the external workbook.
2. Verify the RHEED viewer/widget, intensity chart, message log, configuration panels and workflow buttons appear.
3. Open **Config**, verify that settings load/save, and check the expected device/source mappings.
4. Test RHEED intensity updates and the recording/export workflow.
5. Test **Check**, then Run and subtasks in simulation before attempting any real-device operation.
6. Test cancel, workbook-tab closure and clean shutdown.

> **Safety:** `GaNLiveManager.Check()` can invoke `CloseAllShuttersWithConfirmation()`. It is **not a read-only dry run**. Only use it on real hardware when the required device configuration, interlocks and operating procedure have been verified.

## Implementation notes

The entry point is `src/GaNLiveExternalWorkbook.cs`, which implements `IWorkbookPlugin` and `IWorkbookController`. It creates the existing `GaNLiveForm`, embeds it as a non-top-level WinForms control using `IWorkbookHost.AddWidget(...)`, and forwards Run/Cancel/lifecycle notifications to the original workflow.

This design keeps the original GaNLive behaviour while testing UnicornOne's compiled external workbook extension point. A potential future change is to expose a Core-independent RHEED-widget factory and signal contract through UnicornOne.Abstractions, eliminating the direct reference to `UnicornOne.Core.dll`.

## Troubleshooting

- **Missing UnicornOne DLL:** Build the main UnicornOne solution with the same configuration, or specify `-UnicornOneRoot`.
- **MSBuild not found:** Install VS2022 Build Tools with .NET Framework development support, or launch a VS Developer PowerShell.
- **Plugin not listed:** Confirm `GaNLiveExternal.dll` is in the **running application's** `Plugins\Workbooks` directory, plugins are enabled, and UnicornOne has restarted.
- **Assembly/type load error:** Rebuild this plugin against the current UnicornOne assemblies, then restart the application.
- **Unexpected shared settings:** Set a different `WorkbookName` from the built-in workbook.

## Scope

This repository contains the external workbook code and build/deployment tooling only. It does not distribute UnicornOne runtime assemblies, configuration, experimental data, or hardware drivers.
