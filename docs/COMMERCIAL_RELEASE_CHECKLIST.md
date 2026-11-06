# Commercial Release Checklist

This is an engineering release checklist, not legal advice. Have counsel review distribution terms for your jurisdiction.

## Product build

- [ ] Build with the desired runtime: `.\scripts\publish.ps1 -Runtime win-x64`.
- [ ] Verify `artifacts\ContextFlow-<version>-win-x64\ContextFlow.exe` starts on a clean Windows 11 machine with **no .NET SDK/runtime installed**.
- [ ] Run `.\scripts\smoke-test.ps1`.
- [ ] Test light and dark themes, both monitors at different DPI scales, and a single-monitor fallback.
- [ ] Test the bubble ↔ HUD transition after the app has been idle, after a full-screen app, and after an RDP session reconnect.
- [ ] Test `Ctrl+E+F` from elevated apps, browsers, games, and apps with focus.
- [ ] Test capsule activation, display routing, and per-window capsule assignment.
- [ ] Enable capsule isolation and verify inactive windows minimize (or DWM-cloak where allowed), restore on switch-back, and restore on normal exit.
- [ ] Force-kill ContextFlow while priority/isolation changes are active, relaunch, and verify the session journal restores the original state.
- [ ] Verify the optimizer skips elevated/protected/foreground windows.
- [ ] Verify single-instance activation opens the existing instance.
- [ ] Verify tray Exit goes through the full restore path.

## Code signing and packaging

- [ ] Acquire a commercially trusted Authenticode certificate (EV preferred).
- [ ] Build with `CONTEXTFLOW_CERT_THUMBPRINT` and `CONTEXTFLOW_SIGNTOOL` set; verify the timestamp and signature.
- [ ] Install on a clean VM with Microsoft Defender SmartScreen enabled.
- [ ] Build the optional Inno Setup installer with `iscc installer\ContextFlow.iss`.
- [ ] Test install, launch, upgrade, repair, and uninstall.
- [ ] Decide whether user data under `%LOCALAPPDATA%\ContextFlow` should remain on uninstall.
- [ ] Publish the SHA-256 hash next to the signed binary.

## Legal and commercial

- [ ] Replace placeholder terms with your EULA and support policy.
- [ ] Publish a privacy policy consistent with `docs/PRIVACY.md` — especially the no-network claim.
- [ ] Include the upstream ONNX Runtime license and third-party notices in the installer/about box.
- [ ] Confirm all generated assets (icon, policy model) are owned or licensed for your distribution.
- [ ] If adding crash reporting, update checks, or license activation, revise the privacy policy and architecture; those features are not present in this source.
- [ ] Set your price, refund policy, support SLAs, and update mechanism.

## Store / distribution metadata

- [ ] Product name: ContextFlow
- [ ] Executable: ContextFlow.exe
- [ ] Version: 1.0.0 (update `ContextFlow.csproj`, installer define, and README together)
- [ ] Architecture: win-x64 (add win-arm64 if required)
- [ ] Minimum OS: Windows 11 (Windows 10 22H2 may work but is not the design target)
- [ ] Permissions: standard user, no admin, no service, no driver
- [ ] Network access: none at runtime
- [ ] Add screenshots showing: floating bubble, light HUD, dark HUD, process matrix, route switch.

## Known intentional limitations

- A true infinite Windows compositor is not publicly available; capsules are a logical canvas mapped to physical displays.
- DWM cross-process cloaking may be denied; a reversible minimize fallback is used.
- Cross-process `SetWindowPos` cannot move windows that reject access (for example, elevated windows).
- Capsule assignments are runtime-only; topology persists across launches, not HWNDs.
- Existing user app licenses may restrict automation; test with representative applications.
