# Cove

A small private browser for **macOS** and **Windows**. Cove uses the engine already on the computer — system WebKit on a Mac, Microsoft Edge WebView2 on Windows — and keeps browsing data on that machine. There is no Cove account, and nothing is synced.

Linux and mobile are planned.

| Platform | Status |
| --- | --- |
| macOS | Supported |
| Windows | Supported |
| Linux | Planned |
| Mobile | Planned |

The build that runs on both macOS and Windows is the [`mac` branch](https://github.com/AidanFitzpatrick-priv/Cove/tree/mac). `master` is the first Windows-only version.

## Features

The list below is the current app, on the `mac` branch.

**Layout**

- Horizontal tabs, with the address bar underneath
- Suggestions from bookmarks and history as you type
- A slim sidebar that docks on the left, right, or bottom, and collapses to icons
- Workspaces that share logins, each with its own color
- Private windows that skip history, suggestions, and saved passwords

**Settings**

- Dark or light appearance
- Accent color
- Session restore
- Ad and tracker blocking, on or off

**Local data**

- Bookmarks, history, and downloads stored on this computer
- Passwords saved only on this computer, locked with a Cove password or this computer’s sign-in
- On a Mac, the vault can also unlock with Touch ID, and a sign-in or Touch ID vault keeps its key in the login keychain
- On Windows, sign-in unlock uses DPAPI for the current user

**While you browse**

- Built-in ad and tracker blocking, with a one-click allow for the current site
- A memory cap that puts background tabs to sleep
- Fullscreen video that hides the sidebar and the top bar
- A pop-out button on a video while the pointer is over it
- Unpacked extensions from a folder that contains `manifest.json`

Cove runs an extension’s content scripts. Chrome extension APIs and background pages are not available.

## Requirements

### macOS

- macOS 12.3 or later
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Xcode command line tools (`xcrun`, `clang`)

### Windows

- Windows 10 or later
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)

## Run

On the `mac` branch:

```bash
dotnet run --project Cove.csproj
```

If `dotnet` is not on your PATH on a Mac:

```bash
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
dotnet run --project Cove.csproj
```

The first macOS build compiles `native/cove_webkit.m` into `libCoveWebKit.dylib`.

On the `master` branch, Windows only:

```powershell
dotnet run --project Cove.csproj
```

## Where data lives

| System | Folder |
| --- | --- |
| macOS | `~/Library/Application Support/Cove` |
| Windows | `%LOCALAPPDATA%\Cove` |

That folder holds the session and the password vault. The WebView2 profile on Windows lives there too. Nothing leaves the machine.
