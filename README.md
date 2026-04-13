# VS Code Square

VS Code Square is a small Windows panel for launching and arranging four VS Code windows in a 2x2 layout.

This repository currently implements Phase 1 from `IMPLEMENTATION_PLAN.md`:

- read slot settings from JSON
- launch missing VS Code windows with `code --new-window`
- bind newly created VS Code windows to slots
- arrange assigned windows in a 2x2 grid on the primary monitor work area
- focus an assigned window from the panel
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
