# 🔐 Bastion

Bastion is a modern, offline-first Windows password vault built with WPF (.NET).
It securely stores passwords, recovery phrases, secure notes, encrypted attachments, and crypto tracking data — all encrypted locally.

Your data never leaves your device.

---

## ✨ Features

- Encrypted password manager
- Home dashboard with vault statistics
- Secure notes with tags and image/file attachments
- Relevance graph for connected notes
- Crypto portfolio tracking (CoinGecko)
- Fast search and filtering
- CSV import support
- Encrypted share import/export
- Optional encrypted vault backup workflow
- Auto-lock on inactivity
- Clipboard timeout settings
- Browser extension autofill bridge
- Tray/background mode for keeping autofill available while unlocked
- Windows startup and hidden-to-tray startup options
- Dark/light theme with custom accent and graph colors

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

3. Build the solution

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

All encrypted data is stored locally at:

%APPDATA%/Bastion/

This includes:
- Encrypted vault
- Secure notes
- Note attachments
- Crypto preferences
- Cached coin lists
- Browser extension session token

---

## 🔐 Security

- AES-256-GCM encrypted vault
- No cloud sync
- No telemetry
- Offline-first design
- Auto-lock on inactivity
- Clipboard timeout cleanup
- Encrypted backup and share workflows

---

## 🧩 Tech Stack

- C# (.NET 8)
- WPF (XAML)
- CoinGecko API

---

## 🛠 Roadmap

- Secure file attachment refinements
- More browser extension polish
- Additional graph relevance tuning
- Password health reporting improvements
- Cross-platform support

---

## 📄 License

MIT License

---

## ❤️ Credits

Myself of course,  
Built with security and simplicity in mind.
