# Vantage

Vantage is a native Windows 11 chat app for an AI assistant that can work with your desktop. It supports screenshots, mouse and keyboard input, Windows UI Automation, apps, windows, clipboard, and PowerShell.

History, settings, provider configuration, and task state are stored locally under `%LOCALAPPDATA%\Vantage`. API keys are protected with Windows DPAPI.

## Requirements

- Windows 11, or Windows 10 build 22000 or later
- .NET 9 SDK
- Windows SDK 10.0.26100

## Build

```powershell
dotnet restore
dotnet build .\src\Vantage.csproj --configuration Release --no-restore -p:Platform=x64 -p:RuntimeIdentifier=win-x64
```

## Package

Create a self-contained VeloPack installer:

```powershell
.\scripts\pack-velopack.ps1
```

The installer and update packages are written to `releases\velopack`.

## Run

```powershell
.\releases\velopack\HappyGamerGoose.Vantage-win-Setup.exe
```

Open Settings, add a provider, and enter a request in the chat box.

## Computer Use

Use `batch` for short sequences that do not need a new screenshot between steps. Use window-scoped actions when accessibility information is available. Refresh observations after an action changes focus or layout.

The app can send computer-use input and run requested PowerShell commands without per-action approval dialogs. Use only providers and instructions you trust. Stop or press Escape to cancel an active run.

## Tests

```powershell
$probe = ".\src\probe-worldstate\probe-worldstate.csproj"
dotnet run --project $probe -- --input-self-test
dotnet run --project $probe -- --context-self-test
dotnet run --project $probe -- --launch-match-self-test
dotnet run --project $probe -- --powershell-self-test
dotnet run --project $probe -- --computer-use-prompt-self-test
```

## License

[MIT](LICENSE)
