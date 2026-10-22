# Claude Multi

Run as many **Claude Desktop** instances as you like at the same time, each signed in to a different account, from one small app for **Windows, macOS and Linux**.

Unofficial. Not affiliated with or endorsed by Anthropic.

## What it does

- **Instances**: start, show, close, rename and delete extra Claude Desktop copies. Each one has its own data folder and its own sign-in.
- **Close completely**: stops an instance's windows, tray icon and background processes in one click.
- **API-key instances**: open Claude Code (CLI) in a terminal on an OpenRouter-style gateway with an API key instead of a claude.ai account.
- **Sync**: keeps chat lists and settings (theme, language, MCP servers) the same across instances. Sign-ins are never mixed. Settings files are backed up before they change.
- **Sign-in routing**: sends the browser's `claude://` "Open Claude" link to the instance you are signing in to instead of the main app.
- **Prompt cache timer**: shows how long the prompt cache of a Claude Code conversation has left and warns before it expires.
- **Transfer**: moves accounts, chats and settings to another computer in one password-protected bundle.

## What works where

| | Windows 10/11 | macOS | Linux |
| --- | --- | --- | --- |
| Claude Desktop instances | yes (installer or Store/MSIX build) | yes (`/Applications/Claude.app`) | only with a community build (see below) |
| API-key instances (Claude Code in a terminal) | yes (PowerShell) | yes (Terminal.app) | yes (gnome-terminal, konsole, xterm, …) |
| Close completely | yes | yes | yes |
| Sync, prompt cache timer | yes | yes | yes |
| Sign-in routing (`claude://`) | yes | **not yet** | yes (`xdg-mime`) |
| Transfer: chats and settings | yes | yes | yes |
| Transfer: sign-ins | yes | **no** | **no** |
| API key protection | Windows DPAPI | key file (0600) | key file (0600) |

Things to know:

- **Linux has no official Claude Desktop.** Desktop instances work only with a community build: put its launcher on `PATH` as `claude-desktop`, or set `CLAUDEMULTI_CLAUDE_EXE` to its executable. For "Close completely" of the *main* app, point that variable at the real executable the process runs (not a wrapper script). Everything that does not need Claude Desktop (API-key instances, the cache timer, Claude Code sync) works without it. The tray icon needs a StatusNotifier host (KDE, or GNOME with the AppIndicator extension); without one, closing the window quits the app.
- **Sign-ins cannot be transferred from or to macOS and Linux.** Claude Desktop encrypts its tokens with a key that Windows keeps in the `Local State` file, macOS in the Keychain and Linux in the desktop's secret service; only the Windows form can be handed over. Chats and settings transfer normally on every system. Settings carry the paths of the system they came from (for example MCP server commands); the import warns when the bundle was made on another operating system.
- **API keys on macOS and Linux** are sealed with AES-GCM using a random key in `~/.claude-account-switcher/protector.key` (readable only by you). That keeps a key out of plain text, backups and copied instance folders; like DPAPI on Windows it does not stop another program running as you from reading it.
- **macOS sign-in routing** needs the manager to be an app bundle that declares the `claude://` scheme; it is not packaged as one yet, so the page says it is unavailable.
- The macOS and Linux builds have been compiled and their platform logic is covered by self-tests, but they have **not been run on real macOS or Linux machines** yet. Please report problems.

## Requirements

- Claude Desktop (macOS, Windows), or a community build (Linux), for Desktop instances
- Claude Code (CLI) for API-key instances
- [.NET 9 SDK](https://dotnet.microsoft.com/download) to build from source

## Build and run

```bash
dotnet run --project ClaudeMulti/App/ClaudeMulti.App.csproj -c Release
```

Self-contained builds for each system:

```bash
dotnet publish ClaudeMulti/App/ClaudeMulti.App.csproj -c Release -r win-x64 --self-contained -o out/win-x64
dotnet publish ClaudeMulti/App/ClaudeMulti.App.csproj -c Release -r osx-arm64 --self-contained -o out/osx-arm64
dotnet publish ClaudeMulti/App/ClaudeMulti.App.csproj -c Release -r linux-x64 --self-contained -o out/linux-x64
```

(`osx-x64` and `linux-arm64` work the same way.) A downloaded macOS binary is quarantined by Gatekeeper; clear it with `xattr -cr out/osx-arm64`.

Run the self-tests (they use a throw-away folder and never start a real Claude). They work on every system; a few cases that only make sense on one system are skipped elsewhere:

```bash
dotnet run --project ClaudeMulti/SelfTest/ClaudeMulti.SelfTest.csproj -c Release
```

## Signing in with the Store (MSIX) build of Claude on Windows

Windows hands `claude://` links to a Store-installed Claude through its package registration, so a registry override alone is ignored.
On the first sign-in the app opens Windows Settings: choose **Choose default apps by protocol → CLAUDE → Claude Multi** once.
After that, the sign-in link goes to the instance you pressed **Sign in** on.

## Where things live

| What | Where |
| --- | --- |
| Extra instances | `~/ClaudeInstances/<name>` (Windows: `%USERPROFILE%\ClaudeInstances\<name>`) |
| Manager state and log | `~/ClaudeInstances/manager-state.json`, `manager.log` |
| Settings backups | `~/ClaudeInstances/backups` |
| Main Claude data | Windows `%APPDATA%\Claude` (or the Store package folder), macOS `~/Library/Application Support/Claude`, Linux `~/.config/Claude` |

## Command line

| Argument | What it does |
| --- | --- |
| `--minimized` | Start in the tray without showing the window |
| `--page N` | Open page N (0 instances … 5 transfer) |
| `--uri claude://…` | Used by the system for sign-in routing; forwards one link and exits |
| `--run-cli <id>` | Used inside the terminal window of an API-key instance (macOS, Linux) |

## Project layout

```
ClaudeMulti/
  Core/      instance catalog, launcher, closer, sync, protocol router, transfer bundle, cache math (no UI; all systems)
  App/       Avalonia user interface (views, view models, theme, tray icon)
  SelfTest/  console test runner
```

## Icon

The window and tray icon is `ClaudeMulti/App/Assets/app.png`; the Windows executable uses `ClaudeMulti/App/app.ico`.

## License

Apache 2.0, see [LICENSE](LICENSE). The interface uses [Avalonia](https://avaloniaui.net) (MIT) and Material Design icons (Apache 2.0).
