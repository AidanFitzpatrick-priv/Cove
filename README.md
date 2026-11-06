# Cove

Cove is a small private browser for Windows. It uses the WebView2 runtime already on the PC instead of shipping its own copy of Chromium, and it keeps browsing data on this machine.

## Features

- Horizontal tabs, with the address bar underneath
- A slim sidebar that docks on the left, right, or bottom, and collapses to icons
- Workspaces that share logins, with a color for each one
- Built-in ad and tracker blocking, with a one-click allow for the current site
- Bookmarks, history, and downloads stored locally
- Passwords saved only on this PC, locked with a Cove password or this PC's sign-in password
- A memory cap that puts background tabs to sleep
- Fullscreen video that hides the sidebar and the top bar
- A pop-out button on a video while the pointer is over it
- Unpacked extensions (a folder that contains `manifest.json`)

## Requirements

- Windows
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)

## Run

```powershell
dotnet run --project Cove.csproj
```

## Where data lives

Cove writes to `%LOCALAPPDATA%\Cove`. That folder holds the session, the password vault, and the WebView2 profile. Nothing is synced, and the app does not sign you into an account.
