# 🔐 Bastion

Bastion is a modern, offline-first Windows password vault built with WPF (.NET).
It securely stores passwords, recovery phrases, secure notes, file attachments, and password-health data — all encrypted locally.

Your data never leaves your device.

---

## ✨ Features

- Encrypted password manager
- Home dashboard with vault statistics
- Secure notes with tags, folders, inline image previews, and file attachments
- Password health reporting and stronger password scoring
- Browser extension autofill bridge for Chrome and Firefox
- Encrypted share export/import workflow
- Optional encrypted vault backup and restore workflow
- Fast search and filtering
- CSV import support
- Auto-lock on inactivity
- Clipboard timeout settings
- Tray/background mode with startup options
- Graph view for note relationship mapping
- Dark/light theme support with custom accent and graph colours
- Trash recovery and permanent deletion
- Clean modern UI

---

## 🖥️ System Requirements

- Windows 10 or Windows 11
- .NET 8.0 SDK or newer
- Visual Studio 2022

---

## 📦 Installation

### Option 1: Run from Source

1. Clone the repository

   git clone https://github.com/Hazza-uxdev/bastion.git  
   cd bastion

2. Open the solution

   Open Bastion.sln in Visual Studio 2022

3. Restore dependencies

   Build → Restore NuGet Packages

4. Run the app

   Press F5 or click Start

---

### Option 2: Build a Standalone Executable

1. Open the project in Visual Studio

2. Set build configuration

   Release | x64

3. Build the solution

   Build → Build Solution

4. Locate the executable

   bin/Release/net8.0-windows/

5. Run

   Bastion.exe

---

### Option 3: Use a GitHub Release Build

Download one of the Windows release assets:

- Bastion-v2.9.25-win-x64.zip
- Bastion-v2.9.25-win-x64.msi
- BastionSetup-v2.9.25-win-x64.exe

---

## 🚀 First Launch

- Create a master password
- This password encrypts your vault
- WARNING: There is no password recovery

---

## 📁 Data Storage

All encrypted vault data is stored locally at:

%APPDATA%/Bastion/

This includes:
- Encrypted vault data
- Secure notes
- Password entries
- File attachments
- User preferences
- Browser-extension bridge state
- Backup and share metadata

---

## 🔐 Security

- AES-256-GCM encrypted vault
- No cloud sync
- No telemetry
- Offline-first design
- Auto-lock on inactivity
- Optional clipboard timeout
- Password health reporting
- Encrypted share files require the export password to import
- Release builds embed Bastion branding as compiled WPF resources instead of loose image files

---

## 🧩 Tech Stack

- C# (.NET 8)
- WPF (XAML)
- Windows Forms tray integration
- Supabase-backed Citadel tracker integration docs
- Chrome/Firefox browser extension

---

## 🛠 Roadmap

- Secure file attachment refinements
- More browser extension polish
- Additional graph relevance tuning
- Password health reporting improvements
- Citadel bug and roadmap tracker workflow
- Cross-platform support planning

---

## 📝 Release Notes Policy

The in-app release timeline is reserved for meaningful feature work, bug fixes, security fixes, and packaging changes.
Small visual-only polish, wording tweaks, and image placement corrections should not bump the app version on their own.

After every real version update, the README should be refreshed with the current feature list, roadmap, and release asset names.

---

## 📄 License

MIT License

---

## ❤️ Credits

Myself of course,  
Built with security and simplicity in mind.
