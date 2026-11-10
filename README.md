# ContextFlow

**Contextual Environment Manager for Windows 11** — a local-first workspace HUD for dual-monitor workflows, with an offline AI policy engine for prioritizing the windows you are actually using.

ContextFlow lives on the desktop as a translucent floating bubble. Press **Ctrl + E + F** and it expands into a Fluent-inspired telemetry HUD with live CPU, RAM, and GPU load, managed window geometry, workflow capsules, and a draggable monitor router. When the HUD is collapsed, **Ctrl + E** and **Ctrl + F** scroll backward and forward through an infinite ring of managed windows, maximizing the selected window so it behaves like a Pop!_OS workspace. When idle, an embedded ONNX policy can **below-normal-throttle inactive processes**, **trim their working sets**, and **cloak inactive capsules from DWM composition** — without cloud services, accounts, or network access.

---

## Highlights

- **Ctrl+E+F global chord** that opens or collapses the HUD from any application.
- **Infinite window scroll**: **Ctrl+E** and **Ctrl+F** move through a circular, capsule-ordered ring of managed windows and maximize/focus the selected one. At the ends it wraps around instead of stopping. Both gestures are configurable in Settings.
- **Page map and split-screen tiling**: each workflow page owns a logical screen and can tile its windows in Auto, Columns, Rows, or Grid layouts. The page map in the HUD shows every page with a live layout preview; one click jumps to the page and arranges its windows.
- **Settings tab**: switch the complete UI between **English and Español**, choose the page tiling layout, rebind the scroll-up/scroll-down shortcuts, or reset them to defaults.
- **Multi-monitor floating UI**: the bubble and HUD can be dragged to any display and remember their position, size, and maximized state. The HUD can be resized normally, maximized, and restored with the header button or a double-click.
- **Floating semi-transparent bubble** with drag-to-reposition and edge snapping. The HUD and bubble use real Windows 11 Acrylic/Mica composition, DWM rounded corners, and immersive dark mode.
- **Spatial workflow capsules** across a logical infinite canvas, each assigned to a physical display. Capsule activation moves the whole window set to its target monitor.
- **Live process matrix** showing app icon, window title, process/PID, assigned capsule, monitor, physical coordinates, window size, and live state. Move a window to another capsule directly from its capsule pill.
- **Live telemetry**: asynchronous 500 ms CPU (`GetSystemTimes`), physical memory (`GlobalMemoryStatusEx`), and GPU Engine utilization through PDH's locale-independent `PdhAddEnglishCounter`.
- **Adaptive local AI optimizer (v2)**: an embedded 12-feature ONNX policy model executed by embedded ONNX Runtime, fed by real per-app CPU/memory and by signals it learns on the device. No network APIs, no cloud, no model download. A managed fallback makes startup resilient. See [AI v2](#ai-v2-adaptive-optimizer).
- **Performance tab**: a task-manager view that groups every process into its application (a browser and all its renderers count as one app) with live CPU and memory bars, AI state badges, and one-click **Optimize / Restore**, **Protect**, and **End app** actions. A side panel shows live system pressure, the AI profile selector, session savings, and AI recommendations.
- **Per-app CPU · RAM column** in the window matrix of the Workspace tab, with an arrow marker on apps the AI is currently optimizing.
- **Safe resource policies**: priority class + EcoQoS throttling, low memory priority, working set compaction only under memory pressure, inactive-capsule isolation (DWM cloak where Windows permits it, with a reversible minimize fallback), protected-process exclusions, audio protection, cooldowns, an adaptive maximum number of actions per pass, and automatic restoration.
- **Crash-consistency journal**: if ContextFlow is terminated unexpectedly, the next launch restores any priority or DWM state it left behind.
- **Light first** (`#F8FAFC`, `#FFFFFF`, `#6366F1`, `#0F172A`) and optional dark theme (`#0F172A`, `#1E293B`, `#F8FAFC`).
- **Local JSON persistence** under `%LOCALAPPDATA%\ContextFlow`.
- **Single-file, self-contained deployment** with an Inno Setup installer recipe, a packaged smoke test, optional Authenticode signing, and a commercial release checklist.

---

## AI v2: adaptive optimizer

The first optimizer looked at one process per window, used fixed thresholds, and only reacted every 10 seconds. Version 2 changes how it observes, decides and acts:

| Area | v1 | v2 |
|---|---|---|
| What it sees | The PID that owns a window | The whole **app family**: one `NtQuerySystemInformation` call per 2 s reads every process, and the parent chain groups helpers (browser renderers, WebView2, GPU/utility processes) under their app |
| CPU signal | System-wide only | Real **per-app CPU**, smoothed, plus a slow "sustained background CPU" average that catches hidden CPU hogs |
| Decision model | 9 features, fixed weights | 12 features: adds background CPU share and three **learned** signals |
| Learning | None | `AdaptiveUsageModel` learns per app: **affinity** (how much you use it, 3-day half-life), **daily rhythm** (24-hour histogram, so apps you usually open at this hour are spared) and **regret** (you came back within 5 minutes of an optimization, so that app is protected more). Stored in `ai-profile.json` |
| Cadence | Fixed 10 s, max 3 actions | Adapts to **pressure**: relaxed 15 s, normal 8 s, high 4 s, critical 3 s; the number of actions per pass and the thresholds scale with it, with the **profile** (Conservative / Balanced / Aggressive), and on battery |
| Memory | Always `EmptyWorkingSet` after 2 min | Lowers **memory priority** first (Windows trims those pages first only when it needs to). Hard trims only when RAM is actually filling up (or in Aggressive mode), with per-app cooldowns |
| Restore | On the next pass (up to 10 s) | **Instant**: as soon as a throttled app's window gets focus, the whole family gets its priority back |
| Safety | Protected names, active page | Also never touches apps **playing audio** (Core Audio session peak meter), visible windows on the active page, services (session 0), high/real-time priority processes, or ContextFlow itself. Apps you are using cannot be optimized manually either |
| Scope | Windowed apps only | Also windowless user apps (tray apps, updaters, dev servers) when they burn CPU in the background or memory is under high pressure |

All changes remain journaled in `session-guard.json`, so a crash or a forced kill is undone on the next launch, including the memory priority.

---

## Technology

| Layer | Choice |
|---|---|
| Runtime | .NET 8 (`net8.0-windows`), C# 12 |
| Desktop UI | WPF with custom Fluent-style controls and hardware-accelerated rendering |
| Native interop | P/Invoke: user32, dwmapi, pdh, psapi, kernel32, shell32, shcore |
| Offline AI | Embedded ONNX Runtime 1.22.1 + embedded logistic policy graph (`policy.onnx`) |
| Telemetry | Win32 `GetSystemTimes`, `GlobalMemoryStatusEx`, PDH GPU Engine counters |
| Tray | WinForms `NotifyIcon` hosted by the WPF dispatcher |
| Persistence | `System.Text.Json`, atomic write + backup |
| Packaging | Self-contained single-file publish, optional Inno Setup, optional signtool |

NuGet dependencies:

```text
Microsoft.ML.OnnxRuntime 1.22.1
```

Everything else is part of the Windows Desktop runtime or the base class library. The local policy model and icon are generated by the scripts in `tools/`; they are checked in and embedded in the executable, so neither Python nor an internet connection is needed at runtime.

---

## Repository Layout

```text
contextflow/  (repository root)
├─ ContextFlow.sln
├─ README.md
├─ .gitignore
├─ docs/
│  ├─ ARCHITECTURE.md
│  ├─ COMMERCIAL_RELEASE_CHECKLIST.md
│  ├─ PRIVACY.md
│  ├─ THIRD_PARTY_NOTICES.md
│  ├─ USER_GUIDE.md
│  └─ third-party/
│     ├─ ONNX-RUNTIME-LICENSE.txt
│     └─ ONNX-RUNTIME-THIRD-PARTY-NOTICES.txt
├─ installer/
│  └─ ContextFlow.iss
├─ scripts/
│  ├─ publish.ps1
│  └─ smoke-test.ps1
├─ tools/
│  ├─ generate_policy_model.py
│  └─ generate_icon.py
└─ src/ContextFlow/
   ├─ ContextFlow.csproj
   ├─ app.manifest
   ├─ App.xaml / App.xaml.cs
   ├─ Assets/
   │  ├─ AppIcon.ico
   │  └─ policy.onnx
   ├─ Controls/
   │  ├─ GaugeArc.cs
   │  ├─ MonitorRouteControl.cs
   │  ├─ PageLayoutPreview.cs
   │  ├─ Sparkline.cs
   │  └─ WorkspaceMap.cs
   ├─ Infrastructure/
   │  ├─ AppLog.cs
   │  ├─ AppPaths.cs
   │  ├─ AsyncRelayCommand.cs
   │  ├─ ColorUtil.cs
   │  ├─ Converters.cs
   │  ├─ ObservableObject.cs
   │  └─ RelayCommand.cs
   ├─ Interop/
   │  └─ NativeMethods.cs
   ├─ Models/
   │  ├─ AppSettings.cs
   │  ├─ CapsuleDefinition.cs
   │  ├─ DisplayInfo.cs
   │  ├─ HotkeyGesture.cs
   │  ├─ ManagedWindow.cs
   │  ├─ OptimizationEvent.cs
   │  ├─ PageLayout.cs
   │  ├─ PolicyModels.cs
   │  ├─ ProcessModels.cs
   │  ├─ ScreenRect.cs
   │  └─ TelemetrySnapshot.cs
   ├─ Services/
   │  ├─ AppController.cs
   │  ├─ DisplayService.cs
   │  ├─ GlobalHotkeyHook.cs
   │  ├─ AdaptiveUsageModel.cs
   │  ├─ AudioActivityMonitor.cs
   │  ├─ HardwareAIOptimizer.cs
   │  ├─ IconService.cs
   │  ├─ LocalAiPolicyEngine.cs
   │  ├─ LocalizationService.cs
   │  ├─ ProcessMonitor.cs
   │  ├─ SessionGuard.cs
   │  ├─ SettingsService.cs
   │  ├─ SingleInstanceService.cs
   │  ├─ TelemetryService.cs
   │  ├─ ThemeService.cs
   │  ├─ TrayIconService.cs
   │  ├─ WindowEffects.cs
   │  ├─ WindowManager.cs
   │  └─ WindowNavigator.cs
   ├─ Themes/
   │  ├─ Dark.xaml
   │  ├─ Light.xaml
   │  ├─ Styles.xaml
   │  ├─ Strings.en.xaml
   │  └─ Strings.es.xaml
   ├─ ViewModels/
   │  ├─ ActivityItemViewModel.cs
   │  ├─ AppResourceItemViewModel.cs
   │  ├─ CapsuleItemViewModel.cs
   │  ├─ DisplayItemViewModel.cs
   │  ├─ MainViewModel.cs
   │  └─ WindowItemViewModel.cs
   └─ Views/
      ├─ BubbleWindow.xaml / .xaml.cs
      └─ HudWindow.xaml / .xaml.cs
```

---

## Quick Start

### Prerequisites

- Windows 11 (Windows 10 22H2 also works for the Win32 paths, but Windows 11 is the design target).
- .NET 8 SDK or newer with the Windows Desktop targeting pack.
- Two monitors recommended; one monitor is supported (capsules still use virtual canvas positions).
- Optional: Inno Setup 6 for the installer.

```powershell
# From the repository root
dotnet restore ContextFlow.sln
dotnet build ContextFlow.sln -c Release
dotnet run --project src\ContextFlow\ContextFlow.csproj -c Debug
```

If your development machine only has a newer runtime (for example .NET 10) and you are running the framework-dependent Debug output, enable runtime roll-forward for local testing:

```powershell
$env:DOTNET_ROLL_FORWARD = "Major"
dotnet run --project src\ContextFlow\ContextFlow.csproj
```

The production package is self-contained and does not require this.

### First Use

1. ContextFlow starts as a translucent bubble near the right edge of the primary monitor.
2. Press **Ctrl + E + F**. Hold Ctrl and press E and F together; the chord is detected globally.
3. With the HUD collapsed, press **Ctrl + E** for the previous window or **Ctrl + F** for the next window. The selected window is restored, maximized, and focused; the ring wraps infinitely.
4. Open the **PAGE MAP** on the right and click a page to jump to it. ContextFlow tiles that page's windows on the target display using the layout selected in Settings.
5. Click a capsule/page in the left rail to activate it; the page's windows are tiled on its target display.
6. Drag the pill switch under **Route active page** to send the active page to another display.
7. Click the page pill on any process row to move that window to another page.
8. Use **Settings** to switch language, change the page layout, or rebind Ctrl+E/Ctrl+F.
9. Use the **Auto optimize** and **Page isolation** switches. Isolation is opt-in because it hides windows from DWM composition.
10. Drag the HUD header to another monitor, resize its borders, or use the **□ / ❐** header button to maximize/restore.
11. Press **Esc** or the **—** button to collapse back to the bubble.

The second launch of `ContextFlow.exe` signals the existing instance to open its HUD rather than creating a duplicate background process.

---

## Build a Single-File Commercial Package

```powershell
# From the repository root
.\scripts\publish.ps1 -Runtime win-x64
```

Output:

```text
artifacts\ContextFlow-1.0.0-win-x64\
├─ ContextFlow.exe
├─ SHA256SUMS.txt
└─ licenses\
   ├─ PRIVACY.md
   ├─ THIRD_PARTY_NOTICES.md
   ├─ USER_GUIDE.md
   ├─ ONNX-RUNTIME-LICENSE.txt
   └─ ONNX-RUNTIME-THIRD-PARTY-NOTICES.txt
artifacts\ContextFlow-1.0.0-win-x64.zip
```

The application is self-contained and single-file. Because ONNX Runtime includes native binaries, the published executable extracts those native assets into the per-user .NET single-file cache at first launch. The **application distribution** remains one executable (accompanied by the generated checksum and legal notices); no runtime install is required on the customer machine. The publish script removes ONNX import-library build leftovers and warns if any runtime DLLs remain outside the single file.

ARM64:

```powershell
.\scripts\publish.ps1 -Runtime win-arm64
```

Optional installer:

```powershell
iscc installer\ContextFlow.iss
```

Optional Authenticode signing (configure once):

```powershell
$env:CONTEXTFLOW_SIGNTOOL = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"
$env:CONTEXTFLOW_CERT_THUMBPRINT = "<certificate-sha1-thumbprint>"
.\scripts\publish.ps1 -Runtime win-x64
```

The script hashes and optionally signs the executable, then writes a SHA-256 file. For commercial distribution, sign the executable, timestamp it, review the third-party notices, and add your own EULA, privacy policy, support terms, and update channel. Use **[docs/COMMERCIAL_RELEASE_CHECKLIST.md](docs/COMMERCIAL_RELEASE_CHECKLIST.md)** as the sign-off checklist.

### Packaged smoke test

```powershell
.\scripts\smoke-test.ps1
```

This runs the published self-contained executable with the diagnostic `--exit-after 8` switch, verifies a clean exit code, confirms the embedded ONNX model loaded, and checks that the normal restoration/shutdown path ran. The diagnostic switch is also useful for CI desktop automation. Ensure no other ContextFlow instance is running before starting it.

---

## Architecture at a Glance

```text
WPF views
  ├─ BubbleWindow ── drag / click / show-hide animation
  └─ HudWindow ───── telemetry gauges, capsule rail, process matrix, canvas map
        │
AppController (composition root + lifecycle + hotkey + single-instance + tray)
        │
        ├─ GlobalHotkeyHook ── configurable WH_KEYBOARD_LL scroll gestures + Ctrl+E+F
        ├─ WindowManager ───── EnumWindows, DWM bounds, page tiling, DeferWindowPos routing, DWM cloak
        ├─ WindowNavigator ── circular capsule-ordered window scroll + maximize/focus
        ├─ LocalizationService ── English/Spanish runtime resource dictionaries
        ├─ DisplayService ──── EnumDisplayMonitors, per-monitor work areas & DPI
        ├─ TelemetryService ── 500 ms CPU/RAM/GPU sampler
        ├─ HardwareAIOptimizer ─ adaptive policy: app families, pressure tuning, throttle/trim/restore
        ├─ ProcessMonitor ──── 2 s process table, per-app CPU, app families, audio sessions
        ├─ AdaptiveUsageModel ─ on-device learning: affinity, daily rhythm, regret
        ├─ LocalAiPolicyEngine ─ embedded 12-feature ONNX model + managed fallback
        ├─ SessionGuard ────── crash-consistency journal for external state
        ├─ SettingsService ─── atomic JSON persistence
        └─ ThemeService ────── runtime light/dark resource swap
```

Read **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)** for the detailed data flow, threading model, policy feature contract, native APIs, and design trade-offs.

---

## Hotkey Implementation Note

`RegisterHotKey` supports modifiers plus **one** virtual key. It cannot represent “Ctrl + E + F pressed together.” ContextFlow therefore uses a global low-level keyboard hook (`WH_KEYBOARD_LL`) with a short (180 ms) disambiguation window:

- **Ctrl+E** or **Ctrl+F** starts a pending scroll action by default. Both gestures are rebindable in **Settings**.
- If the other key is pressed within the window, the pending scroll is cancelled and **Ctrl+E+F** toggles the HUD. The HUD chord is intentionally fixed.
- If the second key arrives later, the scroll action has already fired; the chord is suppressed until both keys are released so a slow second key cannot accidentally open the HUD.
- Key repeat is ignored; one physical press produces one navigation step.

The hook observes input and forwards events with `CallNextHookEx`. It is documented in `GlobalHotkeyHook.cs`. Running at normal integrity is intentional; ContextFlow is a user-session utility, not a system service or driver.

---

## Safety & Privilege Model

ContextFlow runs as `asInvoker`. It can only touch windows/processes the current user can already control. It will skip the following rather than force an action:

- Foreground windows and windows in the active capsule.
- Windows on another Windows virtual desktop (DWM-cloaked by the shell).
- Protected system, security, audio, shell, and DWM processes.
- Processes whose priority is High/Realtime.
- Processes the user explicitly excludes in `settings.json`.
- Processes that deny the requested handle access.

Additional safeguards:

- The number of app families changed per pass is bounded (1–10, depending on the profile and the live pressure level).
- Working-set trimming only runs when memory is under pressure (or in Aggressive mode), requires a profile-dependent idle time, and applies per-app cooldowns (15 minutes, 5 under critical pressure).
- Apps that played audio in the last 30 seconds are never throttled.
- Original process priorities and EcoQoS state are restored when a capsule/window becomes active, AI is paused, or ContextFlow exits.
- DWM isolation is opt-in and journaled; the next launch attempts recovery if the app process dies unexpectedly.

See **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)** and **[docs/USER_GUIDE.md](docs/USER_GUIDE.md)**.

---

## Local Data

```text
%LOCALAPPDATA%\ContextFlow\
├─ settings.json          capsule topology, theme, bubble position, toggles
├─ settings.backup.json   last known-good settings
├─ ai-profile.json        what the local AI learned about your app usage (resettable from the Performance tab)
├─ session-guard.json     transient crash-recovery journal (only while needed)
└─ logs\contextflow.log   rolling local diagnostic log (max ~2 MB)
```

There is no network code in the application. Telemetry is read locally via Win32/PDH; the ONNX model is embedded and executed locally. See **[docs/PRIVACY.md](docs/PRIVACY.md)**.

---

## Design Notes and Windows Constraints

ContextFlow is deliberately honest about the platform:

- **No public infinite-desktop compositor API exists.** Capsules are a logical infinite canvas in the HUD, and Ctrl+E/Ctrl+F provide a practical infinite *window* ring: the selected window is brought to the foreground and maximized while preserving its monitor. Activating a capsule maps its window set onto a physical monitor while preserving relative geometry. This is the stable, non-invasive way to emulate continuous workspace switching on Windows.
- **DWM does not expose per-app thumbnail refresh throttling.** The optimizer first attempts DWM composition cloaking (`DWMWA_CLOAK`) for inactive capsule windows. On current Windows 11 builds, `DwmSetWindowAttribute` can return `E_ACCESSDENIED` for windows owned by another process; ContextFlow detects this and falls back to a reversible minimize that removes the window from the interactive composition surface. Both paths are journaled and restored on capsule activation, normal exit, or crash recovery. It does not inject into DWM.
- **Cross-process window geometry is best-effort.** Elevated windows may reject `SetWindowPos`; ContextFlow reports the failure and leaves the window untouched.
- The DWM attribute used for cloaking is undocumented-but-stable. The isolation feature is opt-in, uses a safe fallback, and is restored on exit/crash recovery.
- The HUD is topmost while open. It does not install a display driver, service, or shell extension.

---

## Verification Performed

- Debug and Release builds with .NET 8 targeting and SDK 10.
- App starts as a bubble; global hotkey opens the HUD; pressing it again collapses to the bubble.
- Ctrl+E/Ctrl+F scroll through managed windows, maximize the selection, wrap at the ring ends, and do not trigger the HUD chord.
- HUD and bubble can be dragged between both monitors, resized, maximized, and restored across hide/show cycles.
- Page map jumps tile the selected page; Auto, Columns, Rows, and Grid layouts were exercised.
- English/Spanish switching updates the main HUD, page map, settings, generated status labels, and tray menu.
- Custom scroll gestures were captured, persisted, applied to the global hook, and reset to defaults.
- Capsule pill menus stay anchored at the click position while the process matrix refreshes.
- Single-instance activation opens the running instance's HUD.
- Light and dark resource themes; DWM composition and acrylic rendering on Windows 11.
- Embedded ONNX policy graph loads locally; telemetry shows CPU, RAM, and PDH GPU usage.
- Capsule display routing and per-window reassignment.
- Cross-process DWM cloaking denial observed and automatically handled by the minimize fallback.
- Crash recovery: a force-killed session with throttled/minimized windows was relaunched and the journal restored original priority/minimized state.
- Graceful restoration path available for packaged smoke tests via `ContextFlow.exe --exit-after 8`.
- Self-contained single-file publish for `win-x64` (72.4 MB), including the embedded ONNX runtime and packaged legal notices.

---

## Third-Party Notices

ONNX Runtime is © Microsoft Corporation and is distributed under the MIT License. See **[docs/THIRD_PARTY_NOTICES.md](docs/THIRD_PARTY_NOTICES.md)**. Review all notices and your own commercial terms before public distribution.
