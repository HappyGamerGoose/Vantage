# Vantage

An autonomous Windows desktop agent. Type a goal in plain English, Vantage drives the desktop — opening apps, clicking through UIs, copying values into dialogs — and reasons about what it sees via a vision-capable LLM.

- **UI**: WinUI 3 / Windows App SDK 1.8, .NET 9, compact single-window design with light/dark themes and an 8-accent palette
- **Packaging**: VeloPack Setup executable and update packages, self-contained runtime
- **Engines**: Anthropic + OpenAI-compatible (Azure / Groq / OpenRouter / OpenAI / anything else) — chosen per-conversation
- **Computer use**: direct Win32/UI Automation input plus PowerShell, process, app, and window controls
- **Persistence**: local JSON in `%LOCALAPPDATA%\Vantage`, separate from the program installation
- **Current version**: 1.5.88

> Vantage 1.5.88 is an unrestricted desktop agent. It executes requested computer-use actions without per-action approval prompts, including shell commands and consequential UI actions. Use it only with providers and instructions you trust. Stop or Escape cancels an active run.

## Build

Prerequisites: Windows 10 build 22000+ or Windows 11 and the **.NET 9 SDK**. The Windows App SDK and VeloPack restore from NuGet.

```powershell
.\scripts\pack-velopack.ps1
```

The script publishes a clean self-contained x64 build and writes `Setup.exe`, the full update package, and release metadata to `releases\velopack`. The VeloPack CLI and runtime are both pinned to 1.2.0.

## Run

Run the generated per-user installer:

```powershell
.\releases\velopack\Setup.exe
```

VeloPack installs the program under `%LOCALAPPDATA%\HappyGamerGoose.Vantage` and creates a Start menu shortcut. User data remains in `%LOCALAPPDATA%\Vantage`, so program updates and uninstalls do not delete it.

Open *Vantage*, click *Settings → Add Provider*, enter your endpoint URL + API key, pick a model, save. From then on you can launch the app, type a task, and watch the agent drive your desktop.

## Notes

- Vantage ships with **no built-in providers** — every endpoint is yours to add. Provider configuration lives in `%LOCALAPPDATA%\Vantage\providers.json`.
- The first conversation is created when you send your first prompt; you can delete the last conversation and the app will sit at an EmptyState.
- A task runs until completion, failure, or an explicit Stop/Escape cancellation; the activity card streams phase-by-phase progress.
- All screenshot capture uses **logical pixels** (DPI-correct), so the agent's click coordinates land where it expects them on high-DPI displays.

## Repo layout

```
src/                    source — the project we work on
  Vantage.csproj          SDK-style project (x64 self-contained)
  Package.appxmanifest    legacy MSIX manifest retained for migration history
  App.xaml(.cs)           WinUI 3 entry point
  Assets/                 application and installer artwork built from ICON.png
  Common/                 cross-cutting helpers (Win32 P/Invokes, JSON, log writer)
  Models/                 ViewModels and DTOs
  Services/               Provider dispatch, history store, vision, theme manager
    Agent/                LMMAgent, LMMEngine, WorldSnapshot, VantageACI, prompts
  Views/                  MainWindow.xaml + focused partials (Sidebar, Chat, Settings, ...)
  Regenerate-Icons.ps1    one-shot helper that rebuilds Assets/ from ICON.png
  ICON.png                master icon for ICON.pdf regeneration
scripts/
  pack-velopack.ps1       reproducible publish + VeloPack release command

releases/               VeloPack artifacts land here (gitignored except for .gitkeep)
  .gitkeep                folder marker — keeps releases/ in the repo

README.md               this file
.gitignore              per-folder ignores for bin/obj/AppPackages/releases
LICENSE                 MIT license terms
```

## License

[MIT](LICENSE) — use it, fork it, ship it; just keep the copyright line.
