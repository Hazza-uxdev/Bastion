# 🔐 Bastion

Bastion is a modern, offline-first Windows password vault built with WPF (.NET).
It securely stores passwords, recovery phrases, secure notes, browser autofill data, and vault history — all encrypted locally.

Your data never leaves your device.

---

## ✨ Features

- Encrypted password manager
- Home dashboard with vault statistics
- Secure notes for recovery phrases and backups
- Browser extension support for local autofill
- TOTP / 2FA code support
- Password generator
- Fast search, tags, and filtering
- CSV import support
- Encrypted share export and import
- Security insights and breach checks
- Trash recovery and version history
- Auto-lock on inactivity
- Light and dark theme support
- Release timeline inside the app

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

2. Open the project

   Open Bastion.csproj in Visual Studio 2022

3. Restore dependencies

   Build → Restore NuGet Packages

4. Run the app

   Press F5 or click Start

---

### Option 2: Build a Standalone Executable

1. Open the project in Visual Studio

2. Set build configuration

   Release | x64

3. Build the project

   Build → Build Solution

4. Locate the executable

   bin/Release/net8.0-windows/

5. Run

   Bastion.exe

---

## 🚀 First Launch

- Create a master password
- This password encrypts your vault
- WARNING: There is no password recovery

---

## 📁 Data Storage

Vault data is stored locally on your device.

Main vault file:

   vault.dat

App preferences and browser-extension session data are stored at:

   %APPDATA%/Bastion/

This may include:
- Encrypted vault data
- Saved settings
- Browser extension session token
- Graph and UI preferences

---

## 🔐 Security

- AES-256-GCM encrypted vault data
- PBKDF2 key derivation
- Local-only browser extension API
- No cloud sync
- No telemetry
- Offline-first design
- Auto-lock on inactivity
- Encrypted share exports
- Clipboard safety handling

---

## 🧩 Browser Extension

Bastion includes a browser extension folder:

   BrowserExtension/

Load it as an unpacked extension in Chrome or Firefox, then enable browser autofill in Bastion settings.

The extension talks only to the local Bastion desktop app while it is running.

---

## 🧩 Tech Stack

- C# (.NET 8)
- WPF (XAML)
- Local browser extension JavaScript
- AES-256-GCM encryption
- PBKDF2 key derivation

---

## 🛠 Roadmap

- Installer packaging
- Stronger password health scoring
- More detailed encrypted share guidance
- Secure file attachments
- Clipboard timeout settings
- Optional vault backup workflow
- More browser extension polish
- Cross-device sync research

---

## 📄 License

MIT License

---

## ❤️ Credits

Myself of course,  
Built with security and simplicity in mind.
