# Cove

Cove is a small private browser for macOS. It uses the system WebKit already on the Mac, and it keeps browsing data on this machine.

## Features

- Horizontal tabs, with the address bar underneath
- A slim sidebar that docks on the left, right, or bottom, and collapses to icons
- Workspaces that share logins, with a color for each one
- Built-in ad and tracker blocking, with a one-click allow for the current site
- Bookmarks, history, and downloads stored locally
- Passwords saved only on this Mac, locked with a Cove password or this Mac's sign-in password
- A memory cap that unloads background tabs when Cove passes it
- Fullscreen video that hides the sidebar and the top bar
- A pop-out button on a video while the pointer is over it
- Unpacked extensions that supply content scripts (a folder that contains `manifest.json`)

Chrome extension APIs and background pages are not available. Blocking is done with WebKit content rules, so Cove does not show a per-request block count.

## Requirements

- macOS 12.3 or later
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Xcode command line tools (`xcrun`, `clang`)

WebKit ships with macOS. There is no separate runtime to install.

## Run

If `dotnet` is not on your PATH, the SDK in `~/.dotnet` works:

```bash
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
dotnet run --project Cove.csproj
```

The first build compiles `native/cove_webkit.m` into `libCoveWebKit.dylib` and copies it next to the app.

## Where data lives

Cove writes to `~/Library/Application Support/Cove`. That folder holds the session and the password vault. A vault unlocked with this Mac's sign-in password keeps its key in the login keychain. Nothing is synced, and the app does not sign you into an account.

A vault created on Windows with DPAPI cannot be opened here. A vault created with a Cove password can.
