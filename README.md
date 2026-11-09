# Cove

Cove is a small private browser for macOS and Windows. On a Mac it uses system WebKit. On Windows it uses the Edge WebView2 runtime. Browsing data stays on this machine.

## Features

- Horizontal tabs, with the address bar underneath and autocomplete from bookmarks and history
- A slim sidebar that docks on the left, right, or bottom, and collapses to icons
- Workspaces that share logins, with a color for each one
- Settings for dark or light appearance, accent color, session restore, and blocking
- Built-in ad and tracker blocking, with a one-click allow for the current site
- Bookmarks, history, and downloads stored locally
- Passwords saved only on this computer, locked with a Cove password, this computer's sign-in, or Touch ID on Mac
- A memory cap that puts background tabs to sleep
- Fullscreen video that hides the sidebar and the top bar
- A pop-out button on a video while the pointer is over it
- Unpacked extensions that supply content scripts (a folder that contains `manifest.json`)

Chrome extension APIs and background pages are not available.

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

## Where data lives

- macOS: `~/Library/Application Support/Cove`
- Windows: `%LOCALAPPDATA%\Cove`

That folder holds the session and the password vault. On Mac, a vault unlocked with sign-in or Touch ID keeps its key in the login keychain. On Windows, sign-in unlock uses DPAPI for the current user. Nothing is synced, and the app does not sign you into an account.
