# Vantage

An autonomous Windows desktop agent. Type a goal in plain English, Vantage drives the desktop — opening apps, clicking through UIs, copying values into dialogs — and reasons about what it sees via a vision-capable LLM.

- **UI**: WinUI 3 / Windows App SDK 1.8, .NET 9, single-window Mica-less design with an 8-accent theme palette
- **Packaging**: signed MSIX, self-contained runtime (no separate runtime install)
- **Engines**: Anthropic + OpenAI-compatible (Azure / Groq / OpenRouter / OpenAI / anything else) — chosen per-conversation
- **In-process**: no subprocess, no MCP — all desktop interaction is direct in-process Win32
- **Persistence**: `LocalSettings` survives MSIX in-place upgrades; conversation history + providers JSON in `%LOCALAPPDATA%\Vantage`
- **Current version**: 1.0

> The MSIX schema pins `Identity/@Version` to a 4-segment `Major.Minor.Build.Revision` string. The manifest keeps `1.0.0.0` to satisfy the schema; everywhere else — folder name, MSIX filename, README, release notes — we write **v1.0**.

## Build

Prerequisites: Windows 10 22000+ or Windows 11, **.NET SDK 9**, the *Windows App SDK 1.8* NuGet packages restore themselves.

```
cd src
dotnet publish -c Release -r win-x64 --self-contained ^
  -p:GenerateAppxPackageOnBuild=true ^
  -p:WindowsPackageType=MSIX ^
  -p:WindowsAppSDKSelfContained=true ^
  -p:PublishReadyToRun=false
```

The output lands in `src\AppPackages\Vantage_<ver>_x64_Test\`. Promote it to the `releases/` folder when you're ready to ship.

## Sign the .msix

A signed package is required for `Add-AppxPackage` and for installation outside dev mode. **Signing inputs are intentionally not committed to this repo.** Provide your own dev certificate one of two ways:

### Option A — environment variables

```
set VANTAGE_CERT_PFX=Vantage_DevCert.pfx
set VANTAGE_CERT_THUMBPRINT=<your thumbprint>
set VANTAGE_CERT_PASSWORD=<your password>
dotnet publish ...
```

`VANTAGE_CERT_PFX` defaults to `Vantage_DevCert.pfx` next to the project.

### Option B — local props file

```
copy .vantage.signing.props.example  .vantage.signing.props
```

Edit the new file, fill in the three values, then publish. The file is in `.gitignore`.

### Generating a fresh dev cert (one-time setup)

```
$cert = New-SelfSignedCertificate -Type Custom -Subject "CN=Vantage" `
    -KeyUsage DigitalSignature -CertStoreLocation "Cert:\CurrentUser\My"
$pwd  = ConvertTo-SecureString -String "YourPasswordHere" -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath .\Vantage_DevCert.pfx -Password $pwd
```

The SHA-1 thumbprint appears in the export's output; paste it into the props file.

If neither option is provided the publish step will fail with a clear MSIX error.

## Run

After `dotnet publish`, install the side-loaded MSIX:

```
powershell> Add-AppxPackage .\src\AppPackages\Vantage_<ver>_x64_Test\Vantage_<ver>_x64.msix
```

Open *Vantage*, click *Settings → Add Provider*, enter your endpoint URL + API key, pick a model, save. From then on you can launch the app, type a task, and watch the agent drive your desktop.

## Notes

- Vantage ships with **no built-in providers** — every endpoint is yours to add. The provider list lives in `%LOCALAPPDATA%\Vantage\state.json` (LocalSettings).
- The first conversation is created when you send your first prompt; you can delete the last conversation and the app will sit at an EmptyState.
- A single task can run for hundreds of steps; the activity card streams the phase-by-phase progress and any verifier pre/post screenshots.
- All screenshot capture uses **logical pixels** (DPI-correct), so the agent's click coordinates land where it expects them on high-DPI displays.

## Repo layout

```
src/                    source — the project we work on
  Vantage.csproj          SDK-style project (x64 self-contained)
  Package.appxmanifest    single-package MSIX identity & visual assets
  App.xaml(.cs)           WinUI 3 entry point
  Assets/                 regenerated MSIX assets (PNG + .ico) built from ICON.png
  Common/                 cross-cutting helpers (Win32 P/Invokes, JSON, log writer)
  Models/                 ViewModels and DTOs
  Services/               Provider dispatch, history store, vision, theme manager
    Agent/                LMMAgent, LMMEngine, WorldSnapshot, VantageACI, prompts
  Views/                  MainWindow.xaml + 11 partials (Sidebar, Chat, ...)
  Regenerate-Icons.ps1    one-shot helper that rebuilds Assets/ from ICON.png
  ICON.png                master icon for ICON.pdf regeneration
  .vantage.signing.props  local-only signing inputs (gitignored)

releases/               built artifacts land here (gitignored except for .gitkeep)
  .gitkeep                folder marker — keeps releases/ in the repo

README.md               this file
.gitignore              per-folder ignores for bin/obj/AppPackages/releases
```
