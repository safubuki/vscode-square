# VS Code Square

VS Code Square is a small Windows panel for launching and arranging four VS Code windows in a 2x2 layout.

This repository currently implements Phase 1 from `IMPLEMENTATION_PLAN.md`:

- read slot settings from JSON
- launch missing VS Code windows with `code --new-window`
- bind newly created VS Code windows to slots
- arrange assigned windows in a 2x2 grid on the primary monitor work area
- focus an assigned window from the panel, then toggle it back to the 2x2 layout
- edit and persist slot display titles from the panel
- mark assigned windows as `Missing` when the HWND disappears
- use slot-specific VS Code user data directories when needed to avoid requests being absorbed into an existing VS Code instance

AI status monitoring is intentionally left as a later phase. The panel exposes an `Unknown` AI badge so the UI shape can grow without changing the slot model.

## Requirements

- Windows
- .NET 10 SDK
- VS Code command line launcher available as `code`

The current workspace can still be edited from VS Code without the SDK, but build and run commands require the SDK.

## Build

```powershell
dotnet build .\src\VscodeSquare.Panel\VscodeSquare.Panel.csproj
```

## Run

```powershell
dotnet run --project .\src\VscodeSquare.Panel\VscodeSquare.Panel.csproj
```

## Configuration

Copy the example file and adjust the paths:

```powershell
Copy-Item .\config\vscode-square.example.json .\config\vscode-square.json
```

The app searches for configuration in this order:

1. `config\vscode-square.json`
2. `config\vscode-square.example.json`
3. built-in default slots

The build copies the example config into the output folder.

For deployment, keep app configuration next to the executable:

```text
VscodeSquare.Panel.exe
config/
  vscode-square.json
```

Use this file for stable settings such as slot names, workspace paths, launch timeout, and whether slot-specific VS Code user data directories are used.

Leave a slot `path` empty when you want VS Code to decide what to restore. On first launch that opens the VS Code welcome/no-folder state. After you open a folder or workspace inside that slot, VS Code stores it in the slot-specific user data directory and can restore it on later launches.

Runtime state is stored separately under the configured `stateDirectory` value. By default this is:

```text
%LOCALAPPDATA%\VscodeSquare\
```

That state directory is for machine-local data such as slot user-data folders, saved HWND assignments, custom panel titles, logs, and future helper-extension status files. Keep workspace settings in `config\vscode-square.json`; keep volatile runtime state out of the app folder.
