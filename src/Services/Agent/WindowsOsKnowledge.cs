// SPDX-License-Identifier: MIT
// Vantage — Services/Agent/WindowsOsKnowledge.cs
//
// Compact atlas of Windows 11 navigation, settings URIs, PowerShell
// recipes, app-launch aliases, and safety rules. Trimmed hard from
// the original ~38 KB so smaller-context models still leave room for
// the conversation. Every section is opinionated — only the entries
// actually pulled into tasks were kept. Verify by running a real
// long-horizon task and adding back what's missing.

namespace Vantage.Services.Agent;

public static class WindowsOsKnowledge
{
    // ─────────────────────────────────────────────────────────────────
    // 1. Keyboard shortcut atlas — most-used only. Reach for these
    //    before clicking.
    // ─────────────────────────────────────────────────────────────────
    public const string KeyboardShortcuts = """
    ## Keyboard shortcuts (always prefer these over clicks)

    ### Win-key shortcuts
    - `Win`        → Start menu
    - `Win+E`      → File Explorer
    - `Win+I`      → Settings HOME
    - `Win+S`      → System search (apps / files / settings)
    - `Win+R`      → Run dialog (the fastest launch path)
    - `Win+D`      → Show desktop (toggle)
    - `Win+L`      → Lock PC
    - `Win+A`      → Quick Settings (Wi-Fi, Bluetooth, Night Light)
    - `Win+V`      → Clipboard history
    - `Win+X`      → Power-user menu (Terminal, Task Manager)
    - `Win+N`      → Notification center + calendar
    - `Win+Shift+S`→ Snipping tool (region to clipboard)
    - `Win+Left/Right` → Snap window
    - `Win+Up/Down`   → Maximize / minimize

    ### Explorer / dialogs / focused window
    - `Alt+F4`     → Close focused window
    - `Alt+Tab`    → Cycle windows
    - `Ctrl+Shift+Esc` → Task Manager (always works)
    - `Esc`        → Close current popup / dialog
    - `Alt+D` or `Ctrl+L` → Focus address bar (Explorer / browsers)

    ### Text (works in every text field)
    - `Ctrl+A` / `Ctrl+X` / `Ctrl+C` / `Ctrl+V` / `Ctrl+Z` / `Ctrl+Y`
    - `Ctrl+Home` / `Ctrl+End` → Document start / end
    - `Shift+Home` / `Shift+End` → Select to line start / end
    - `Ctrl+Left` / `Ctrl+Right` → Move by word
    - `Ctrl+Backspace` / `Ctrl+Delete` → Delete word left / right

    ### Browser / webview
    - `Ctrl+T`/`Ctrl+W`/`Ctrl+Shift+T` → New / close / reopen tab
    - `Ctrl+L` or `F6` → Focus URL bar
    - `Ctrl+R` / `F5`  → Reload
    - `Ctrl+F`         → Find in page
    - `Ctrl+Shift+Del` → Clear browsing data
    """;

    // ─────────────────────────────────────────────────────────────────
    // 2. Launch catalog — the most-reached destinations.
    // ─────────────────────────────────────────────────────────────────
    public const string LaunchCatalog = """
    ## Launch catalog (prefer `launch_app "<alias>"`)

    ### Built-in apps
    - `notepad` · `wordpad` · `mspaint` · `calc` · `charmap`
    - `SnippingTool` · `stikynot` · `taskmgr` · `resmon` · `eventvwr`
    - `services.msc` · `taskschd` · `taskschd.msc` · `devmgmt.msc`
    - `diskmgmt.msc` · `regedit` · `msinfo32` · `cleanmgr` · `msconfig`
    - `mstsc` (RDP) · `mstsc.exe`
    - `control` · `appwiz.cpl` · `mmsys.cpl` · `ncpa.cpl` · `desk.cpl`
    - `intl.cpl` · `timedate.cpl` · `powercfg.cpl` · `sysdm.cpl`
    - `main.cpl` · `inetcpl.cpl` · `odbcad32` · `dxdiag`
    - `powershell` · `pwsh` (PS 7) · `cmd` · `windows-terminal` / `wt`

    ### Common paths
    - `explorer`          → File Explorer at This PC
    - `explorer.exe`      → ditto
    - `shell:Downloads` `shell:Desktop` `shell:MyDocuments` `shell:MyPictures`
    - `shell:AppsFolder`  → All installed apps (UWP + desktop)
    - `shell:RecycleBinFolder` · `shell:ControlPanelFolder`

    ### Top ms-settings: URIs (Win+R → paste)
    - ms-settings:                       → Settings HOME
    - ms-settings:display                → Display
    - ms-settings:sound                  → Sound
    - ms-settings:notifications          → Notifications
    - ms-settings:quiethours             → Focus Assist
    - ms-settings:powersleep             → Power & sleep
    - ms-settings:storage               → Storage
    - ms-settings:network                → Network & internet
    - ms-settings:network-wifi           → Wi-Fi
    - ms-settings:network-vpn            → VPN
    - ms-settings:bluetooth              → Bluetooth & devices
    - ms-settings:printers               → Printers & scanners
    - ms-settings:appsfeatures           → Apps & Features (uninstall)
    - ms-settings:defaultapps            → Default apps
    - ms-settings:personalization        → Personalization root
    - ms-settings:personalization-background / colors / themes / start / taskbar
    - ms-settings:taskbar                → Taskbar layout
    - ms-settings:windowsupdate          → Windows Update
    - ms-settings:troubleshoot           → Troubleshoot
    - ms-settings:recovery               → Reset this PC
    - ms-settings:yourinfo               → Account info
    - ms-settings:signinoptions          → Sign-in options
    - ms-settings:privacy-microphone / privacy-webcam / privacy-location
    - ms-settings:easeofaccess / easeofaccess-magnifier / easeofaccess-narrator
    - ms-settings:dateandtime · regionlanguage · keyboard
    - ms-settings:gaming / gaming-gamebar / graphics
    - ms-settings:multitasking / multitasking-sgupdate
    - ms-settings:mouse · mousetouchpad · mousetouchpad-touch
    - ms-settings:phone                   → Phone Link
    - ms-settings:project                 → Project to this PC
    - ms-settings:autoplay · usb
    """;

    // ─────────────────────────────────────────────────────────────────
    // 3. PowerShell recipes — the everyday ones.
    // ─────────────────────────────────────────────────────────────────
    public const string PowerShellRecipes = """
    ## PowerShell recipes (use `run_powershell` action)

    ### File system
    - `Get-ChildItem "C:\\path" -File | Sort-Object Length -Descending | Select-Object -First 25 Name, Length, LastWriteTime`
    - `Get-ChildItem -Path "C:\\Users\\me" -Recurse -Filter "*partial*" -ErrorAction SilentlyContinue | Select FullName -First 25`
    - `(Get-ChildItem "C:\\path" -Recurse -File | Measure-Object Length -Sum).Sum / 1GB` — folder size in GB
    - `Remove-Item "C:\\path\\file" -Force` (irreversible)
    - `Copy-Item / Move-Item` with `-Recurse -Force`

    ### Apps & processes
    - `Get-Package | Select-Object Name,Version | Sort-Object Name` — installed programs
    - `Get-AppxPackage | Select Name, InstallLocation` — UWP apps
    - `Start-Process "https://example.com"` — open URL in default browser
    - `Start-Process "code" -ArgumentList "C:\\Users\\me\\project"`
    - `Get-Process | Where-Object {$_.MainWindowTitle -ne ""} | Select Id, ProcessName, MainWindowTitle`

    ### System / services / network
    - `Get-CimInstance Win32_OperatingSystem | Select Caption, Version`
    - `Get-CimInstance Win32_Processor | Select Name, NumberOfLogicalProcessors`
    - `Get-NetIPAddress -AddressFamily IPv4 | Select IPAddress, InterfaceAlias`
    - `Test-NetConnection google.com -Port 443`
    - `Get-Service | Where-Object {$_.Status -eq 'Running'} | Select Name, DisplayName`
    - `Start-Service / Stop-Service / Restart-Service`
    - `Clear-DnsClientCache`

    ### Registry (export first!)
    - `reg export "HKCU\\Software\\MyApp" "C:\\myapp.reg" /y`
    - `Get-ItemPropertyValue "HKCU:\\Software\\MyApp" "Key"` / `Set-ItemProperty -Path "HKCU:\\..." -Name "Key" -Value "new"`
    """;

    // ─────────────────────────────────────────────────────────────────
    // 4. App launch decision tree — modern Windows 11. Read this BEFORE
    //    you emit any click on a Start menu tile.
    // ─────────────────────────────────────────────────────────────────
    public const string AppCatalog = """
    ## App launch decision tree — modern Windows 11

    DO NOT default to `launch_app "<name>"` for everything. Windows 11
    has multiple launch paths and only ~30 well-known apps have a
    registered alias. For everything else, use a structured sequence.

    **STEP 1 — Is the app registered as an alias?**
    Use `launch_app "<alias>"` (no `.lnk`, no path). Windows resolves
    registered aliases:
      * `msedge` `chrome` `firefox` `mspaint` `notepad` `calc` `wordpad`
        `charmap` `taskmgr` `regedit` `msinfo32` `cleanmgr` `powershell`
        `pwsh` `cmd` `wt` `code` `devenv` `dotnet` `git-bash`
        `wsl` `ubuntu` `docker-desktop` `slack` `discord` `teams`
        `msteams` `zoom` `telegram` `whatsapp` `spotify` `vlc` `obs`
        `steam` `notion` `obsidian`
      * UWP Settings pages: `ms-settings:` / `ms-settings:display` /
        `ms-settings:sound` / etc. (Win+R-only).

    `launch_app` waits for and focuses the visible window before it
    returns. Once it succeeds, do not open Run, Search, or another launch
    route for that app. Continue with the requested work in the focused
    window and add `target_title` to typing/key actions when possible.

    **STEP 2 — If STEP 1 fails or the app has no alias, search:**
    This is the most reliable path on Windows 11. Emitting three
    actions is correct — don't try to "save actions" by guessing
    click coords on the Start menu:
    ```
      key win+s            ← opens System search; search box is focused
      type "copilot"       ← waits 100ms then keys via SendInput
      key return           ← launches the highlighted result
    ```
    The search filters as you type and Enter activates the top hit.
    Use 3-4 letter prefixes for partial matches — "edge" opens Edge,
    "cop" opens Copilot, "sett" finds Settings.

    **STEP 3 — If the app is pinned to the taskbar:**
    Look at the foreground hwnd + WindowsState in WORLD_STATE. The
    taskbar is the strip at the bottom; pinned app icons sit on it. Use
      `click description="the <AppName> icon pinned to the taskbar at
       the bottom of the screen"` and the grounding layer will hit it.
    Better: use `click_xy` with the actual screen position you read
    from the latest screenshot (look for the icon row).

    **STEP 4 — If the app is in the Start menu but not pinned:**
    Open the full Start menu, then use the menu's SEARCH box (the one
    at the bottom-left of the Start panel — same field as Win+S):
    ```
      key win              ← opens Start menu; type to filter
      type "copilot"
      key return
    ```
    This is identical mechanics to STEP 2; Start menu IS the search.

    **STEP 5 — UWP-only apps without an alias:**
    Use `run_powershell` with `Start-Process`:
      `Start-Process "ms-copilot:"` (or whatever the protocol is)
      `Start-Process shell:AppsFolder` → browse the folder
      `Get-AppxPackage | Where Name -like "*copilot*"` → find the
        PackageFamilyName, then `Start-Process "<PFN>!App"`

    **STEP 6 — last-resort PowerShell direct:**
    `Start-Process "<AppName>"` — PowerShell's Start-Process handles
    registered file associations, UWP protocol URIs, and installed
    shortcuts in one call. Pair with `Get-Command <hint>` if you're
    unsure of the alias.

    **DON'T:**
    - DO NOT click a Start menu tile directly. The tile grid is
      variable (refreshes when MS Store updates), sparse, and the
      grounding LLM hallucinates coords on it. Always use Search.
    - DO NOT emit `click_xy` with coordinates you didn't compute from
      the screenshot yourself. If you're going to compute coords,
      at least point to a visible feature (an icon, a button) and
      describe its location in `click`. Save `click_xy` for cases
      where you've measured a fixed UI element (an Open dialog's
      OK button at e.g. (1042, 736)).

    **Anti-cycles (READ THESE):**
    - Stick to ONE plan. If your chosen launch path is `win+s → type →
      return`, emit those three actions back-to-back. Don't switch to
      "click Start button" between them.
    - After a successful click (verifier Met=true), don't reground the
      same region. Move FORWARD in the plan.
    - Two consecutive failed groundings = bad description, NOT a bad
      strategy. Rephrase the description or change the mechanism (e.g.,
      abandon click for `launch_app` or `key win+s`).
    - Don't `screenshot` between every action. A screenshot is included
      with every response — only request an extra one after a multi-
      second `wait` or when the world is clearly in flux.
    - Don't emit `wait` for less than 2 seconds after a UI-relevant
      action. Animations settle in 1 s; Wait 3 s + screenshot mid-step
      burns a full model round-trip for nothing visible.
    """;

    // ─────────────────────────────────────────────────────────────────
    // 5. Safety rules — anti-hallucination, anti-destruction. These
    //    are non-negotiable and rarely cost much to keep in context.
    // ─────────────────────────────────────────────────────────────────
    public const string SafetyGuards = """
    ## Safety & anti-hallucination rules

    1. Never use `Shift+Delete` (irreversible). Delete → Recycle Bin.
    2. Don't run destructive cmdlets (`format`, `Remove-Item -Recurse`,
       `reg delete` for system keys, `bcdedit`) without explicit user go-ahead.
    3. Cancel is the safe button on dialogs. Reach for it first.
    4. Captchas / MFA / payment screens → STOP, tell the user.
    5. UAC / smart-screen consent prompts are unexpected — verify before clicking.
    6. If "no observable change" was reported by the verifier, DO NOT retry
       the same `description` or coordinates — change target, or take a
       fresh screenshot.
    7. Two consecutive identical FAILING descriptions → switch strategy.
    8. `confidence: high` should reflect real certainty. If you're guessing,
       say `low`. CALIBRATION matters — verifiers reward honest scores.
    """;

    // ─────────────────────────────────────────────────────────────────
    // Composition helper — emits the focused atlas.
    // ─────────────────────────────────────────────────────────────────
    public static string Compose() =>
        KeyboardShortcuts + "\n\n"
      + LaunchCatalog + "\n\n"
      + PowerShellRecipes + "\n\n"
      + AppCatalog + "\n\n"
      + SafetyGuards;
}
