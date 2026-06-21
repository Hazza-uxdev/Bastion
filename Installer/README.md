# Bastion Installer Packaging

This folder contains packaging files for GitHub release builds.

## Outputs

- `Bastion-v2.9.23-win-x64.zip`
- `Bastion-v2.9.23-win-x64.msi`
- `BastionSetup-v2.9.23-win-x64.exe`

## Requirements

- .NET 8 SDK
- WiX Toolset CLI 5.x for `.msi`
- Inno Setup 6 for `.exe`

The build script always creates the publish folder and zip. It creates the MSI and EXE installer only when the matching installer tool is installed. WiX 5.x is recommended for this packaging script.

## Build

Run from the project root:

```powershell
.\Installer\build-installers.ps1 -Version 2.9.23
```

The published app and installer assets are written to:

```text
release/
```

The installer payload includes `Bastion.exe`, `Bastion.ico`, and the `BrowserExtension/` folder.
