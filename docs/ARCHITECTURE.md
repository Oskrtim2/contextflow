# ContextFlow Architecture

## 1. Purpose

ContextFlow is a Windows 11 desktop utility that combines three concerns that are usually separate:

1. **Spatial workspace orchestration** — group application windows into workflow capsules and route a capsule's entire window set to a physical display.
2. **Local observability** — expose live CPU, RAM, GPU, window geometry, and process state.
3. **Offline hardware optimization** — use a small embedded machine-learning policy to throttle inactive processes, compact working sets, and remove inactive capsule windows from DWM composition.

It is a per-user, `asInvoker` application. It is not a service, driver, shell extension, or kernel component.

---

## 2. Runtime topology

```text
┌──────────────────────────────────────────────────────────────────────┐
│ WPF presentation                                                     │
│  BubbleWindow (layered, topmost, no taskbar)                         │
│  HudWindow (Acrylic/Mica, custom chrome, topmost while open)         │
│  Custom controls: GaugeArc, Sparkline, WorkspaceMap, MonitorRoute    │
└───────────────────────────────┬──────────────────────────────────────┘
                                │ bindings / commands
┌───────────────────────────────▼──────────────────────────────────────┐
│ MainViewModel                                                        │
│  capsules, windows, telemetry history, activity journal, filters     │
│  commands, HUD transition requests, theme requests                   │
└───────────────────────────────┬──────────────────────────────────────┘
                                │ events + commands
┌───────────────────────────────▼──────────────────────────────────────┐
│ AppController — composition root and lifecycle                       │
│  owns services, tray, hotkey, WindowNavigator, single instance, UI   │
└───────┬───────────┬────────────┬──────────────┬──────────────┬───────┘
        │           │            │              │              │
┌───────▼──────┐ ┌──▼─────────┐ ┌▼───────────┐ ┌▼───────────┐ ┌▼──────┐
│WindowManager │ │Telemetry   │ │Display     │ │HardwareAI  │ │Session│
│              │ │Service     │ │Service     │ │Optimizer   │ │Guard  │
└───────┬──────┘ └──┬─────────┘ └──────┬─────┘ └──────┬─────┘ └──┬────┘
        │           │                  │              │          │
        └───────────┴──────────────────┴──────────────┴──────────┘
                  Win32 / DWM / PDH / PSAPI / kernel32
```

### Why WPF?

- Stable, mature Windows 11 support with per-monitor DPI awareness.
- Direct P/Invoke integration with DWM system backdrops and Win32 window management.
- Rich vector controls for the gauges, canvas, and transition surfaces without a web runtime.
- Single-file, self-contained publishing is straightforward.
- No additional UI framework runtime or webview to deploy.

WinUI 3 was considered, but its deployment model adds Windows App SDK bootstrap complexity for a utility that is otherwise fully self-contained. WPF plus explicit DWM composition produces the required result with fewer moving parts.

---

## 3. Startup and lifecycle

`App.OnStartup`:

1. Creates `%LOCALAPPDATA%\ContextFlow` and the rolling logger.
2. Acquires a per-user named mutex. A second instance locates the primary bubble HWND and posts a private registered window message, then exits.
3. Constructs `AppController`.
4. `AppController.StartAsync`:
   - Loads and sanitizes `settings.json`.
   - Runs `SessionGuard.RecoverOrphanedState()` before tracking starts.
   - Refreshes monitor topology.
   - Creates services and the view-model.
   - Applies the saved theme.
   - Applies capsule topology to `WindowManager`, starts the 1-second discovery loop, the 500 ms telemetry loop, and the 10-second policy loop.
   - Creates the HUD and bubble windows, the tray icon, and the hotkey hook.
   - Shows the bubble, not the HUD. The app always starts in its low-profile state.

`AppController` keeps the lifecycle state (`_hudVisible`, shutdown flags) out of the view-model. The view-model expresses intent (`CollapseRequested`, `ThemeChangeRequested`); the controller owns window animation and native state.

### Shutdown order

1. Release the global hotkey hook.
2. Dispose the tray icon.
3. Save settings.
4. Dispose the optimizer (restore priority classes / EcoQoS).
5. Dispose the window manager (uncloak all windows, stop tracking).
6. Dispose telemetry.
7. Clear the session journal only after restoration attempts complete.
8. Close windows and call `Application.Shutdown`.

If the process is killed, the session journal restores external state on the next launch.

---

## 4. Global shortcuts

Requirements:

- **Ctrl + E** — previous managed window (scroll up).
- **Ctrl + F** — next managed window (scroll down).
- **Ctrl + E + F** — open or collapse the HUD.

`RegisterHotKey` can only register modifiers plus a single virtual key. It cannot register two non-modifier keys pressed together, so the app uses `SetWindowsHookEx(WH_KEYBOARD_LL)`.

`GlobalHotkeyHook`:

- Tracks physical E and F key-down state and ignores key repeat, so one physical press produces one action.
- Checks `GetAsyncKeyState(VK_CONTROL)` on each relevant event.
- Starts a 180 ms disambiguation timer for a lone Ctrl+E or Ctrl+F.
- If the other key is pressed within the window, cancels the pending scroll and raises the Ctrl+E+F `Pressed` event.
- If the timer expires, raises `PreviousRequested` or `NextRequested` on the UI thread. If the key is still held, the chord is suppressed until both keys are released, so a slow second key cannot open the HUD accidentally.
- Resets state on key-up. A timer that fires after both keys were released does not leave the next shortcut blocked.
- Always calls `CallNextHookEx`; the hook is observational, never swallowing input.
- Fails closed: if hook installation fails, the HUD still opens from the tray and bubble; the status footer reports `HOTKEYS UNAVAILABLE`.

`AppController` handles navigation by collapsing the topmost HUD first, then asking `WindowNavigator` to focus and maximize the target window.

### Window scroll model

`WindowNavigator` builds a deterministic circular order from the 1-second `WindowManager` snapshot:

1. Capsules in `AppSettings.Capsules` order (the logical vertical desktop).
2. Windows within a capsule by `FirstSeenUtc`, then handle.

`Ctrl+E` decrements and `Ctrl+F` increments the index; the arithmetic wraps in both directions. `WindowManager.FocusAndMaximizeWindow` restores minimized windows, maximizes windows that expose `WS_MAXIMIZEBOX`, and calls the existing `FocusWindow` thread-input path.

Because the snapshot can lag up to one second, a navigation performed in the last 1.5 seconds is treated as authoritative; `WindowsChanged` snapshots captured during that interval do not overwrite the selected handle. Outside that interval the real foreground window is authoritative. This prevents rapid Ctrl+F presses from selecting the same window twice.

### HUD and bubble placement

Both windows use the virtual desktop rectangle (`SystemParameters.VirtualScreen*`), not the primary-monitor `WorkArea`, so they can be moved to either display. `AppController` stores the HUD's normal rectangle and maximized flag in `AppSettings`; `LocationChanged`, `SizeChanged`, and `StateChanged` are debounced before persistence. On the next open, saved bounds are clamped to the current virtual desktop, which keeps the HUD on its monitor even after a hide/show cycle or a monitor topology change.

---

## 5. Window discovery and management

`WindowManager` operates on an immutable `ManagedWindow` snapshot.

### Discovery

- `EnumWindows` enumerates top-level windows in Z-order.
- Filters:
  - `IsWindowVisible`.
  - Non-empty title.
  - Not `WS_CHILD`.
  - Not a tool window unless it has `WS_EX_APPWINDOW`.
  - Not an owned dialog/popup.
  - Not a shell/desktop/IME class.
  - Not this process.
  - Not DWM-cloaked by another virtual desktop/shell.
- Geometry comes from `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` with `GetWindowRect` fallback.
- Process names and executable paths come from `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` + `QueryFullProcessImageName`.
- Icons use `WM_GETICON`, class icons, and `SHGetFileInfo` fallback; all `ImageSource`s are frozen and cached.

### Capsule assignment

- New windows inherit the capsule of an existing window from the same executable, else the active capsule.
- Reassigning a window through the HUD updates runtime assignment and process affinity.
- Assignments are runtime state. Durable capsule topology is stored, not HWNDs.

### Routing window sets

`MoveCapsuleWindowsToDisplay`:

- Calculates each window's normalized position relative to its source monitor work area.
- Maps that position and size into the target work area.
- Uses `BeginDeferWindowPos` / `DeferWindowPos` / `EndDeferWindowPos` for a batch.
- Falls back to individual `SetWindowPos` calls if the batch fails.
- Restores maximized windows around the move to ensure they maximize on the destination monitor.
- Excludes windows already on the destination display.

The tracker polls every second. Native WinEvent hooks are intentionally not used for v1.0: polling avoids re-entrancy and hook-pump complexity while keeping the matrix current. The service boundary allows a future `SetWinEventHook` implementation without touching the UI.

### Inactive capsule isolation

When **Capsule isolation** is enabled:

- Windows in the active capsule and the foreground window are left visible.
- ContextFlow first attempts DWM composition cloaking via `DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, 1)`.
- On current Windows 11 builds, that call returns `E_ACCESSDENIED` for windows owned by a different process. When this is detected, ContextFlow logs the denial once and uses a reversible `ShowWindow(SW_MINIMIZE)` fallback. Minimized windows are removed from the interactive composition surface, use no GPU composition budget for their content, and remain recoverable through the taskbar.
- Windows the user already minimized are not claimed by ContextFlow.
- Every isolated window is recorded in `SessionGuard` with its mode (`cloak` or `minimize`).
- On exit, on recovery, or when the feature is disabled, all affected windows are uncloaked or restored.
- If the user manually restores a window minimized by the fallback, ContextFlow honors that choice and does not immediately minimize it again; the tracking entry is removed on the next capsule transition.

Why not control DWM thumbnail refresh? Windows exposes no public API for throttling arbitrary apps' DWM thumbnail/animation refresh. DWM thumbnails belong to the app registering them. DWM cloaking removes inactive windows from composition when permitted; the minimize fallback achieves the same user-facing workspace isolation in a documented, reversible way. ContextFlow does not inject into DWM or use kernel hooks.

---

## 6. Telemetry

`TelemetryService` runs one async loop with `PeriodicTimer(500 ms)`.

| Metric | Source | Notes |
|---|---|---|
| CPU % | `GetSystemTimes` idle/kernel/user deltas | Kernel includes idle; computes busy time between samples |
| RAM % + used/total GB | `GlobalMemoryStatusEx` | Physical memory |
| GPU % | PDH `\GPU Engine(*)\Utilization Percentage` | Uses `PdhAddEnglishCounter` so it is locale-independent; groups per-process/per-engine instances, sums each engine, displays the busiest engine |

PDH is optional: systems without GPU Engine counters show `GPU —` and continue with CPU/RAM. Counter setup/read failures disable GPU sampling instead of spinning.

The telemetry thread never touches WPF. Events are marshalled through the view-model dispatcher and the UI keeps a 60-sample history for sparklines.

---

## 7. Local AI policy

The optimizer is not a cloud wrapper. It is:

```text
telemetry + window/capsule state
        │
        ▼
PolicyFeatures (9 normalized floats)
        │
        ▼
embedded ONNX graph  (or managed fallback)
        │
        ▼
score 0..1  + deterministic guardrails
        │
        ▼
priority throttle / working-set trim
```

### Features

| # | Feature | Normalization |
|---|---|---|
| 0 | Idle pressure | inactive seconds / 300, clamped 0..1 |
| 1 | Inactive capsule | 1 when not active |
| 2 | System memory pressure | RAM % / 100 |
| 3 | Working-set pressure | process working set MB / 4096 |
| 4 | CPU pressure | current system CPU % / 100 |
| 5 | Logical viewport distance | capsule center distance from active capsule center / 6000 |
| 6 | Window count pressure | process window count / 10 |
| 7 | Minimized | all group windows minimized |
| 8 | Foreground | 0 for candidates; included in contract |

The graph is a logistic model (`MatMul → Add → Sigmoid`) in `src/ContextFlow/Assets/policy.onnx`. It is loaded once from an embedded resource through ONNX Runtime. A managed fallback uses identical coefficients if ONNX initialization fails. The model is generated reproducibly by `tools/generate_policy_model.py`; the checked-in model means no Python or network in the product.

### Decision guardrails

For each process group that is *not* foreground and *not* in the active capsule:

1. Skip protected processes and user exclusions.
2. Require at least 45 seconds of inactivity.
3. Require a working set of at least 48 MB.
4. Score the group with the policy.
5. Throttle when score ≥ 0.52 and idle ≥ 60 seconds.
6. Trim when score ≥ 0.70, idle ≥ 120 seconds, working set ≥ 180 MB, and the 15-minute trim cooldown has elapsed.
7. Apply at most three process groups per pass, highest score first.

Priority target is `BELOW_NORMAL_PRIORITY_CLASS`, or `IDLE_PRIORITY_CLASS` when every tracked window in the group is minimized. High/Realtime processes are never downgraded.

### Restoration

- Original priority class is stored per PID with process start time to protect against PID reuse.
- Switching to a capsule immediately restores that capsule's process groups.
- Foreground detection restores its group on the next pass.
- Disabling AI restores all groups.
- Exit/dispose restores all groups.
- Crash journal (`SessionGuard`) restores records on the next launch.

### Why parts of the system are intentionally conservative

Working-set trimming causes page faults when the app is paged back in. Priority throttling can visibly slow an app that the user is about to use. The optimizer therefore waits for clear inactivity, limits per-pass changes, cooldowns trims, and restores aggressively. **Capsule isolation is opt-in** because hiding windows changes user-visible desktop state.

---

## 8. Infinite logical canvas

Windows gives each physical monitor a coordinate rectangle in the virtual desktop. It does not expose a compositor API for an unrendered infinite desktop. ContextFlow models one:

- Each capsule has a rectangular logical area in an unbounded 2D canvas (`CanvasX/Y/Width/Height`).
- Capsule centers determine viewport distance for the AI policy.
- The HUD renders the topology with a pannable/zoomable custom control (`WorkspaceMap`), including active capsule highlighting and window counts.
- A capsule targets a physical monitor. Activating it transfers its window set to that monitor and restores its relative layout.

The UI map is therefore a topological workspace navigator, not a mirror of an impossible off-screen Windows surface. This avoids undocumented shell/DWM hacks while giving the workflow feel of Pop!_OS/COSMIC workspaces.

---

## 9. Presentation layer

### HUD

- `HudWindow.xaml`: single custom-chrome WPF window with Acrylic system backdrop.
- Layout: header, capsule rail, metric cards, process matrix, canvas map, AI journal, footer.
- No system caption; header drag is wired to `DragMove`. Min/max is not offered to keep the HUD a HUD.
- Rounded corners via DWM + root border; Acrylic via `DWMWA_SYSTEMBACKDROP_TYPE` with a Windows 10 `SetWindowCompositionAttribute` fallback.
- Dark title bar composition via `DWMWA_USE_IMMERSIVE_DARK_MODE`.
- Heavily styled widgets use templates and theme brushes; all colors are `DynamicResource`.
- `GaugeArc` animates a dependency property with a 270° arc; `Sparkline` draws the last 60 samples.
- `WorkspaceMap` and `MonitorRouteControl` are `FrameworkElement`s with custom render/interaction code to keep the visual language consistent.
- The route control previews drag positions and commits the display switch once on release, avoiding repeated window moves across intermediate monitors.

### Bubble

- Layered transparent WPF window (`AllowsTransparency=True`).
- Topmost, tool window, no taskbar/Alt+Tab presence.
- Dragging uses `GetCursorPos` physical coordinates and per-monitor DPI-aware conversion.
- On release, snaps to the nearest screen edge within 24 px and writes the new position.
- Root opacity base remains 1; entrance animations never risk leaving a native layered surface fully transparent if a timeline is throttled.

### Animation and DWM throttling

Window opacity animation on layered windows can fail to composite consistently under some DWM/remote-desktop conditions. Bubble animations therefore operate on the visual tree and always keep native `Window.Opacity = 1`. HUD hide waits for the visual animation with a timeout so a stalled animation cannot trap the user in an invisible HUD.

---

## 10. Persistence and recovery

### Settings

`AppSettings` includes theme, optimizer toggles, active capsule, bubble position, process exclusions, and capsule topology.

- JSON is written to a temp file, then atomically moved.
- The previous file is copied to `settings.backup.json`.
- Corrupt JSON falls back to backup/defaults.
- Nullable bubble positions prevent non-finite JSON values.

### Session journal

`session-guard.json` contains:

- `Priorities`: PID, name, process start time, original priority, throttled priority.
- `CloakedWindows`: HWND, owning PID, and mode (`cloak` or `minimize`). The name is retained for schema compatibility, but it covers both isolation modes.

Entries are added when ContextFlow changes external state and removed on restoration. On launch, entries are validated against current process identity/window ownership before being applied. Cloaked windows are uncloaked; minimized fallback windows are restored. The journal is cleared after recovery. This prevents persistent hidden windows or priorities if the app crashes.

A packaged smoke test can request the normal shutdown path:

```powershell
ContextFlow.exe --exit-after 8
```

This performs the same restore/journal-clear sequence as tray Exit and is useful in CI/desktop-automation validation.

---

## 11. Threading model

| Thread | Work |
|---|---|
| WPF dispatcher | All UI, view-model mutations, tray, HUD transitions, hook event delivery |
| Window tracking task | Enumeration, DWM queries, icon retrieval, session journal writes; snapshot event back to dispatcher |
| Telemetry task | CPU/RAM/GPU polling; event callbacks marshalled by view-model |
| Optimizer task | Policy evaluation, process handles, PSAPI/DWM-adjacent decisions; activity events marshalled by view-model |
| Tray server | WinForms hidden window message pump integrated with WPF dispatcher |

Shared mutable state in services is guarded with locks; retained WPF objects (icons, brushes) are frozen. All UI-bound updates funnel through `MainViewModel.Dispatch`.

---

## 12. Error handling

- File/JSON errors fall back to defaults rather than preventing startup (settings) or disable only the affected subsystem (journal).
- GPU PDH failures degrade GPU to `—`.
- Window access failures are reported as failed optimization events or skipped.
- A global dispatcher exception handler logs, attempts priority/cloak restoration, shows a diagnostic, and shuts down.
- Shutdown restoration is best-effort; crash recovery is the second line of defense.

---

## 13. Extensibility points

- Add a new policy: extend `PolicyFeatures`, retrain/export `policy.onnx`, keep the fallback weights aligned, update `tools/generate_policy_model.py`, and re-run verification.
- Add a capsule management dialog: persist into `AppSettings.Capsules`; `MainViewModel.AddCapsule` demonstrates the flow.
- Replace polling with `SetWinEventHook`: `WindowManager` already abstracts the snapshot event.
- Add per-process CPU attribution: use `GetProcessTimes` deltas per tracked PID and add it to the policy feature vector.
- Add display hot-plug support: handle `WM_DISPLAYCHANGE`/`SystemEvents.DisplaySettingsChanged`, refresh `DisplayService`, and remap stale device names.
- Add installer update mechanism: signed package + version endpoint would violate the current zero-network runtime guarantee; keep it explicit if added.

---

## 14. Key native APIs used

`EnumWindows`, `GetWindowRect`, `DwmGetWindowAttribute`, `DwmSetWindowAttribute`, `SetWindowPos`, `BeginDeferWindowPos`/`DeferWindowPos`/`EndDeferWindowPos`, `EnumDisplayMonitors`, `GetMonitorInfo`, `MonitorFromWindow`, `SetForegroundWindow`, `AttachThreadInput`, `SetWindowsHookEx(WH_KEYBOARD_LL)`, `GetAsyncKeyState`, `OpenProcess`, `QueryFullProcessImageName`, `GetPriorityClass`, `SetPriorityClass`, `SetProcessInformation`, `EmptyWorkingSet`, `GetSystemTimes`, `GlobalMemoryStatusEx`, `PdhOpenQuery`, `PdhAddEnglishCounter`, `PdhCollectQueryData`, `PdhGetFormattedCounterArray`, `SHGetFileInfo`.

All are declared in `Interop/NativeMethods.cs` with explicit structures and constants.

---

## 15. Packaged smoke tests

`scripts/smoke-test.ps1` launches the self-contained artifact with `--exit-after N`. The app uses the exact tray-exit shutdown path, verifies ONNX loaded, and validates that no secondary runtime files were required. This gives CI a way to exercise the production binary without UI automation.

For the live UI:
- Trigger the single-instance activation channel by launching a second copy.
- Use UI Automation on the `ContextFlow` window to invoke theme/capsule controls.
- Validate DWM fallback by toggling capsule isolation while windows are assigned to an inactive capsule; check `IsIconic` and `session-guard.json`, then switch back and verify restoration.
