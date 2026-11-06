# ContextFlow Privacy

**ContextFlow is designed to be local-first and offline-only.** There is no network client, telemetry uploader, analytics SDK, advertising SDK, account system, or cloud inference path in this codebase.

## What ContextFlow accesses

| Data | Purpose | Where it stays |
|---|---|---|
| CPU, RAM, GPU utilization | Live HUD gauges | In process memory only |
| Window titles, process names, icons | Process matrix | In process memory only |
| Window geometry | Capsule routing and matrix | In process memory only |
| Capsule topology, theme, toggles | Workspace persistence | `%LOCALAPPDATA%\ContextFlow\settings.json` |
| AI decisions / optimizer activity | Local activity journal | In process memory; rolling log may contain process names |
| Diagnostic log | Support/troubleshooting | `%LOCALAPPDATA%\ContextFlow\logs\contextflow.log` |

## What ContextFlow does **not** do

- No HTTP, WebSocket, DNS, telemetry, or update requests at runtime.
- No cloud API calls.
- No OCR, screenshot capture, clipboard capture, keystroke logging, or content recording.
- No remote AI/model download. The ONNX policy graph is embedded in the executable.
- No window contents are retained. Icons and titles are read only to display the process matrix.

## Logging

The diagnostic log contains timestamps, lifecycle messages, settings paths, policy engine status, and optimization actions. Process names may appear in optimization entries at `INFO` level. Window titles are not written to the log. The log rotates at approximately 2 MB and stays local.

## Files and removal

```text
%LOCALAPPDATA%\ContextFlow\settings.json
%LOCALAPPDATA%\ContextFlow\settings.backup.json
%LOCALAPPDATA%\ContextFlow\session-guard.json
%LOCALAPPDATA%\ContextFlow\logs\contextflow.log
```

Uninstalling the application may leave these per-user files behind. Delete the `%LOCALAPPDATA%\ContextFlow` directory to remove all settings, logs, and recovery data.

## Network disclosure

The development tooling (`dotnet restore`, optional Python scripts) may use the internet to obtain build dependencies or generate the checked-in model/icon. The **compiled application** does not make network requests. If you customize the source to add update checks, crash reporting, or license activation, you must update this document and your customer-facing privacy policy.

## Security / permission level

ContextFlow runs as the current user (`asInvoker`). It does not request administrator rights, install a service, or run a driver. It can only affect processes the current user can already access. Windows User Account Control and process ACLs remain authoritative.
