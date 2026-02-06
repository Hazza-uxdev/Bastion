# 🔐 Bastion

Bastion is a modern, offline-first Windows password vault built with WPF (.NET).
It securely stores passwords, recovery phrases, secure notes, and crypto tracking data — all encrypted locally.

Your data never leaves your device.

---

## ✨ Features

- Encrypted password manager
- Home dashboard with vault statistics
- Secure notes (recovery phrases, backups)
- Crypto portfolio tracking (CoinGecko)
- Fast search and filtering
- CSV import support
- Auto-lock on inactivity
- Clean modern UI

---

## 🖥️ System Requirements

- Windows 10 or Windows 11
- .NET 7.0 SDK or newer
- Visual Studio 2022

---

## 📦 Installation

### Option 1: Run from Source

1. Clone the repository

   git clone https://github.com/Hazza-uxdev/bastion.git  
   cd bastion  

2. Open the solution

   Open Bastion.sln in Visual Studio 2022

3. Restore dependencies (usually automatic)

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

   bin/Release/net7.0-windows/

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
- Crypto preferences
- Cached coin lists

---

## 🔐 Security

- AES-encrypted vault
- No cloud sync
- No telemetry
- Offline-first design
- Auto-lock on inactivity

---

## 🧩 Tech Stack

- C# (.NET 7)
- WPF (XAML)
- CoinGecko API

---

## 🛠 Roadmap

- Password strength indicators
- Secure file attachments
- Clipboard timeout settings
- Vault backup/export
- Theme switching
- Cross-platform support

---

## 📄 License

MIT License

---

## ❤️ Credits
Myself of course, 
Built with security and simplicity in mind.
