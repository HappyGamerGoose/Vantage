# Vantage

Vantage is a native Windows desktop control panel for an AI assistant. It combines a calm WinUI 3 chat surface with direct computer-use capabilities so an assistant can inspect and operate the PC, not only answer questions.

- **UI**: WinUI 3, Windows App SDK 1.8, .NET 9, Windows 11 Mica, light and dark themes, and an eight-accent palette
- **Computer use**: Win32 input, Windows UI Automation, screenshots, keyboard and mouse actions, dragging, scrolling, clipboard, PowerShell, processes, apps, and windows
- **Efficient runs**: small deterministic UI sequences can be emitted as one `batch` action, while observations are consumed once so stale coordinates are not reused
- **Long-running tasks**: the multimodal context uses a bounded sliding window; older screenshots are discarded while a persistent text-only goal, to-do list, and last-action summary remain available
- **Search**: every saved message and agent-run summary is searchable from the sidebar
- **Local first**: history, settings, provider configuration, and task state stay under `%LOCALAPPDATA%\Vantage`; provider API keys are protected with Windows DPAPI
- **Packaging**: self-contained x64 VeloPack installer and update packages
- **Current version**: 1.5.88

> The current runtime sends computer-use input and runs requested PowerShell actions without per-action approval dialogs. Use only providers and instructions you trust. Stop or Escape cancels an active run.

## Requirements

- Windows 11, or Windows 10 build 22000 or later
- .NET 9 SDK
- Windows SDK 10.0.26100 for the UI Automation interop reference

The VeloPack CLI is restored from the repository tool manifest and pinned to version 1.2.0.

## Build

Restore dependencies, then build the x64 desktop app:

```powershell
dotnet restore
dotnet build .\src\Vantage.csproj --configuration Release --no-restore -p:Platform=x64 -p:RuntimeIdentifier=win-x64
```

To produce a self-contained VeloPack installer:

```powershell
.\scripts\pack-velopack.ps1
```

The script regenerates the shell icon, publishes the app, signs the generated binaries when the local signing properties are available, and writes the installer and update metadata to `releases\velopack`. Release outputs are intentionally ignored by Git.

## Run

Run the generated per-user installer:

```powershell
.\releases\velopack\HappyGamerGoose.Vantage-win-Setup.exe
```

VeloPack installs the program under `%LOCALAPPDATA%\HappyGamerGoose.Vantage` and creates a direct Start menu shortcut to the current executable. User data is stored separately, so updating or uninstalling the program does not remove conversation history or provider settings.

Open **Settings**, add a provider with its endpoint, API key, and model, then enter a goal in the composer. Vantage keeps the conversation visible while the agent works through the desktop.

## Local data

The application data directory is `%LOCALAPPDATA%\Vantage`:

- `history.json` stores conversations and rendered agent-run summaries
- `providers.json` stores provider configuration; API keys are DPAPI-protected for the current Windows user
- `settings.json` stores preferences such as theme, accent, selected model, and sidebar state
- `agent-context\<conversation-id>.json` stores the text-only goal, to-do items, completion state, and last-action summary used during long runs

The first conversation is created when the first prompt is sent. Deleting a conversation also removes its persistent task context.

## Computer use

The agent has two complementary action surfaces:

- **Desktop actions** use the current screenshot for visual grounding and cover clicks, typing, key chords, scrolling, dragging, clipboard, app/window lifecycle, and PowerShell.
- **Window-scoped actions** use `list_windows` and `get_window_state` to obtain a verified window identity and accessibility tree. Element and coordinate observations are single-use and must be refreshed after an action that could change focus or layout.

Use `batch` for short mechanical sequences that do not require an intermediate observation, such as focusing an address bar, typing a URL, and pressing Enter. Stop the batch before an action whose target depends on seeing the result.

## Tests

The probe project exercises the non-visual runtime without opening the main UI:

```powershell
$probe = ".\src\probe-worldstate\probe-worldstate.csproj"
dotnet run --project $probe -- --input-self-test
dotnet run --project $probe -- --context-self-test
dotnet run --project $probe -- --launch-match-self-test
dotnet run --project $probe -- --powershell-self-test
dotnet run --project $probe -- --computer-use-prompt-self-test
```

The optional `--computer-use-self-test` checks window enumeration, accessibility observation, and single-use observation expiry against a visible desktop window.

## Repository layout

```text
src/
  Vantage.csproj                 WinUI 3 desktop project
  App.xaml(.cs), Program.cs      application entry points
  Assets/                        application and installer artwork
  Common/                        shared Win32, JSON, and logging helpers
  Models/                        conversation, message, provider, and run models
  Services/                      history, providers, themes, shell, and input
    Agent/                       context, prompts, actions, automation, and probes
  Views/                         MainWindow and focused partial views
  probe-worldstate/              non-visual runtime probe project
scripts/
  build-app-icon.ps1             generates the app artwork icon
  build-shell-icon.ps1           generates the native shell ICO
  pack-velopack.ps1              reproducible publish and packaging command
.config/dotnet-tools.json        pinned VeloPack CLI manifest
releases/                        local VeloPack output; contents are gitignored
```

## License

[MIT](LICENSE) - use it, fork it, and ship it; keep the copyright line.
