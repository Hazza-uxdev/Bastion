# Cross-Platform Support Plan

Bastion is currently a Windows WPF application. Cross-platform support should be treated as a migration, not a direct WPF tweak.

## Target Direction

- Keep the encrypted vault format portable and UI-independent.
- Move storage, crypto, password health, encrypted sharing, and graph relevance into shared .NET libraries.
- Keep the current WPF app as the Windows client while a new cross-platform client is built.
- Evaluate Avalonia UI first because it supports Windows, macOS, and Linux with C#/.NET and is the closest fit to the current WPF structure.

## Work Phases

1. Split core code from WPF:
   - `Bastion.Core` for vault models, storage, crypto, health scoring, graph scoring, and import/export.
   - `Bastion.Windows` for the existing WPF shell, tray integration, startup settings, and Windows installer.

2. Replace Windows-only dependencies behind interfaces:
   - Clipboard operations.
   - Tray/startup behavior.
   - File open/save dialogs.
   - Browser extension bridge/token storage.

3. Build a new cross-platform UI:
   - Prefer Avalonia for a .NET-native migration.
   - Reuse the vault file format and shared services.
   - Implement platform-specific tray/startup behavior separately per OS.

4. Package per platform:
   - Windows: MSI/EXE remains.
   - macOS: signed `.dmg` or `.pkg`.
   - Linux: AppImage or `.deb` first.

## Hold-Off Notes

Do not start the UI migration until the core services are split out and covered by build checks. The current WPF app can keep shipping roadmap features while the cross-platform foundation is prepared.
