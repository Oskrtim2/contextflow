# ContextFlow User Guide

## Install / launch

For development: `dotnet run --project src\ContextFlow\ContextFlow.csproj -c Debug`.

For production, use the self-contained `ContextFlow.exe` from `artifacts\ContextFlow-1.0.0-win-x64`. No .NET install is required. Optionally run the Inno Setup installer.

The app starts as a translucent floating bubble near the screen edge. There is no main taskbar button; use the notification-area icon for lifecycle actions.

## Summon / dismiss

- **Ctrl + E + F** — open the HUD; press again to collapse to the bubble.
- **Click the bubble** — open the HUD.
- **Esc** or the **—** header button — collapse to the bubble.
- **Double-click the tray icon** — open the HUD.
- A second launch of `ContextFlow.exe` opens the HUD of the existing instance.

It is a chord: hold Ctrl and press **E**, then **F** while Ctrl is held. Both keys must be down at the same time. The app waits 180 ms to tell a lone Ctrl+E/Ctrl+F from the chord, so a quick Ctrl+E or Ctrl+F scrolls windows and the complete chord toggles the HUD.

## Infinite window scroll

When the HUD is collapsed, the managed windows form a circular vertical desktop ordered by capsule position and window creation time:

- **Ctrl + E** — previous window (scroll up). At the first window it wraps to the last.
- **Ctrl + F** — next window (scroll down). At the last window it wraps to the first.
- The selected window is restored if minimized, maximized if it supports it, and brought to the foreground.
- The HUD process matrix highlights the current scroll window and shows its position (`3 / 12`) next to **Application windows**.
- The **▲ / ▼** buttons in the process matrix header trigger the same actions. They collapse the HUD first so the selected window is visible.
- The footer shows the global shortcuts; the current window title and position are always visible in the process matrix header.

This is a practical, non-invasive approximation of an infinite compositor: Windows does not expose a public API for a true endless desktop, so ContextFlow turns the window ring into a full-screen workspace sequence.

## Window controls

- Drag the HUD header to move it to any monitor. Its position and size are saved under `%LOCALAPPDATA%\ContextFlow\settings.json`.
- Drag any resize border to resize the HUD while it is restored.
- Double-click the header or click **□ / ❐** to maximize or restore. A maximized HUD restores to its previous normal rectangle.
- Drag the floating bubble to any monitor; it stays where you drop it instead of snapping back to the primary display.

## HUD regions

### Workflow Capsules (left rail)

Each capsule is a logical workspace bound to a physical display.

- **Click a capsule** to activate it. Its windows are routed to its target display. If capsule isolation is on, windows in other capsules are cloaked.
- **+** creates a new capsule at the next logical canvas position.
- The tinted bar and dot show the capsule's identity and active state.
- The **ACTIVE** badge follows the capsule currently in the HUD.

### Route active capsule

A segmented, draggable switch:

- Click a display segment, or drag the segmented thumb to the target display.
- The active capsule's window set is moved once when you release the toggle.
- The display caption below shows the active target and its resolution.
- Relative window positions are preserved and clamped into the destination work area.

### Local AI optimizer

- **Auto optimize** — master switch for local priority/working-set policies. On by default.
- **Capsule isolation** — opt-in hiding of inactive capsule window sets. ContextFlow first tries DWM cloaking; if Windows denies cross-process cloaking (common on current Windows 11), it uses a reversible minimize fallback. Off by default.
- **Run pass** — immediately evaluate eligible inactive windows.
- The **ONNX · local** tag indicates the embedded policy runtime loaded. If ONNX cannot initialize, the app falls back to an equivalent managed policy and shows `Heuristic · local`.

### Telemetry cards

- **CPU** — system utilization, logical processor count, 60-sample sparkline.
- **Memory** — used / total physical RAM and percentage.
- **GPU** — busiest GPU Engine utilization reported by PDH; progress ring and sparkline. If PDH GPU counters are not exposed on your system, the card shows `—`.
- **Managed windows** — count of all currently tracked application windows and capsules.

### Process matrix

Each row contains:

- app icon and fallback initial
- window title and process name + PID
- capsule assignment pill
- monitor
- physical X/Y position and window size
- current state: `FOCUSED`, `ON SCREEN`, `MINIMIZED`, or `CLOAKED`

Actions:

- **Click the capsule pill** → choose another capsule. The window is assigned and, if a target display exists, routed there. The menu stays anchored to the place where you clicked even while the matrix refreshes.
- **Double-click a row** or use **↗** → focus that window and collapse the HUD so you can work.
- **▲ / ▼** → previous or next window in the infinite scroll ring.
- **Filter windows…** → search by title, process, or capsule.
- **ALL / CAPSULE** → show every managed window or only the active capsule.
- The current scroll window has an accent-tinted row. The header shows its title and position (`current / total`).
- The matrix is a snapshot refreshed approximately once per second.

### Canvas overview

A pannable, zoomable topology of your capsules.

- **Drag** empty canvas to pan.
- **Ctrl + wheel** zooms around the cursor.
- **Wheel / Shift + wheel** scrolls vertically / horizontally.
- **Click a capsule** to activate it.
- **Double-click** the map to fit all capsules.

The canvas is a logical topology of capsules. The actual infinite desktop interaction is the Ctrl+E/Ctrl+F window ring described above: each selected window is maximized and focused, so you can scroll through full-screen work contexts. Activating a capsule maps its window set to its target monitor. This is explained in `ARCHITECTURE.md`.

### AI activity journal

Shows the most recent local policy actions, including working-set trims, priority changes, DWM composition changes, and routing assignments. Detail text includes the local policy score and inactivity time when applicable.

## Tray menu

- **Open ContextFlow**
- **AI optimization** toggle
- **DWM capsule isolation** toggle
- **Dark theme** toggle
- **Exit ContextFlow** — performs the full restoration path for priorities and DWM state.

## Data and troubleshooting

Settings, logs, and the transient crash journal live in `%LOCALAPPDATA%\ContextFlow`.

If the HUD cannot appear:

1. Check `%LOCALAPPDATA%\ContextFlow\logs\contextflow.log`.
2. Confirm another application is not blocking low-level keyboard hooks. The tray and bubble still work if the hook is unavailable.
3. Try launching the executable a second time. Single-instance activation will surface the HUD.

If a window is unexpectedly hidden after a crash, relaunch ContextFlow. The crash journal attempts to uncloak it during startup. You can also reset all state by exiting the app and deleting `%LOCALAPPDATA%\ContextFlow` after reviewing the log.

## Safety recommendations

- Keep **Capsule isolation** off unless you want ContextFlow to manage the visibility of application windows. On systems where DWM refuses cross-process cloaking, inactive windows are minimized instead.
- Do not exclude critical business apps from protection by adding untrusted names to configuration.
- The optimizer never targets the foreground app or the active capsule.
- Resource optimizations are reversible; closing ContextFlow restores priority classes and restores every window it isolated.
- If ContextFlow is force-killed, a minimal journal (`session-guard.json`) records priority and isolation state. The next launch attempts recovery before tracking resumes.
