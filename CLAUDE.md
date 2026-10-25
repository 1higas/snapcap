# Snapcap

A Windows tray app that sets the battery charge limit on Snapdragon X laptops through Qualcomm's battery-manager firmware. Public repo: https://github.com/1higas/snapcap (owner `1higas`, Thiago Fonseca; previously `snapdragon-charge-limit`, which redirects). The developer's machine is a Lenovo Yoga Slim 7x 14Q8X9 (83ED), Snapdragon X Elite, Windows 11 ARM64, English UI, dark mode.

## Goals and preferences (from the owner)

- **Minimal everything**: smallest possible exe, least code, lowest CPU and RAM. Use what Windows ships rather than adding dependencies. Measure the size impact of any change (release exe is ~69 KB; icon ~10 KB of that).
- **Windows 11 look**: Fluent colors, system accent color, light/dark mode, Segoe UI Variable, Segoe Fluent Icons, DWM rounded corners. Windows has no native range slider or flyout control, so the UI kit is hand-drawn (`src/Ui.cs`).
- **Everything is configured in the flyout.** The tray icon's right-click menu has only "Exit".
- **No pop-ups.** An available update shows as a quiet row at the top of the flyout (the flyout is bottom-anchored, so it grows upward and nothing else moves).
- **Layout shifts are forbidden.** Nothing the user is looking at or about to click may move because of a state change (applying, errors, hover, readings updating). Show transient status in space that already exists (for example "Applying…" is drawn in the title bar beside the gear), and add rows only at the top of the flyout, where growth goes upward. Opening a page or the update row appearing are the only height changes allowed.
- **Scale-agnostic.** The UI must look the same at any display scale, on any monitor, and after the scale changes while the app runs. Every size comes from one DPI, the effective DPI of the window's monitor (`Native.DpiAt`/`DpiOf`), through `Px()`; fonts are created in pixels from it (`GraphicsUnit.Pixel`), never in points. `WM_DPICHANGED` rebuilds fonts and calls `Rescale()`. The tray icon is sized from the taskbar's DPI (`Native.TrayIconSize`) and re-rendered on `DisplaySettingsChanged`. Don't use WinForms' `DeviceDpi`, `SystemInformation` sizes or point-sized fonts: they follow the system DPI, which keeps its sign-in value after a scale change (text drawn at 200% inside a layout sized for 100%).
- **0 W is always grey**, never green or orange: in the tray icon, the headline, the chart line and the hover dot. The chart splits the line at zero crossings and colors each piece by the sign of the readings; don't go back to clipping (see Gotchas).
- The portable copy opens its flyout when started; the installed copy starts in the tray only (launching it again while it runs opens the flyout, and the setup opens it once installing finishes by signalling the same "show" event). The portable copy shows a muted tip that the installed version needs no prompt per change.
- **README is for end users** (no command line, no internals). Technical detail goes to `docs/technical.md`.
- Two release assets, **`Snapcap-Setup.exe`** and **`Snapcap-Portable.exe`**, which are the same binary.
- The owner prefers to review before anything is committed or pushed; ask first.

## How it works

- Firmware request: open `\\.\QCOMBATTMGR` (Qualcomm PMIC GLink driver `qcpmicglink8380.sys`, admin-only ACL) and send IOCTL `0x80092044` with three uint32: `enable=1`, `target_soc` (stop %, 100 = no limit), `delta_soc` (resume at target - delta). The driver turns it into battmgr opcode `0x48` (`BATTMGR_CHG_CTRL_LIMIT_EN`), the same request Linux's `qcom_battmgr` sends for `charge_control_end_threshold`. Bounds: target 55-100, resume >= 50, delta 1-20. Found by reverse engineering; undocumented.
- The firmware keeps the limit across reboots and operating systems, and **cannot be queried**. Snapcap remembers what it last applied in `HKCU\Software\Snapcap`.
- Plugged in at the limit, the firmware runs the laptop from the charger and the battery idles; the resume threshold only matters after running on battery.
- Battery status comes from the standard battery-class IOCTLs (`IOCTL_BATTERY_QUERY_STATUS` etc., no admin needed). `Rate` is the battery's own power, not the charger's input: a USB meter on the charger reads more. The firmware smooths it; Snapcap does not average.

## Privilege model (security-critical, keep these invariants)

- The app runs **unelevated** (`asInvoker` manifest). That keeps Print Screen and other hotkeys working while the flyout is open, which they don't when an elevated window has focus.
- **Installed copy**: setup (self-elevates via `runas`) registers an on-demand scheduled task named `Snapcap` that runs `"C:\Program Files\Snapcap\Snapcap.exe" --set $(Arg0) --delta $(Arg1)` with `HighestAvailable`. The app runs it through the Task Scheduler COM API (`Schedule.Service`, late-bound), polls for completion and reads `LastTaskResult`. No prompt, about 1 s.
- **The task must only ever point at the exe in Program Files** (admin-only write). Before running it, `RunApplyTask` checks the task XML for that exact path and otherwise ignores the task. Never register a no-prompt elevated task for an exe in a user-writable folder (portable, Downloads, the build folder).
- **Portable copy**: also unelevated; each change runs itself elevated with `--set N --delta D` (UAC each time). If the installed task exists, the portable copy uses it. After a UAC prompt the flyout is reopened (the prompt steals focus, which closes it).
- The elevated entry point is the command line: `--set`/`--delta`/`--off` with validated integers; anything else exits 87. `--uninstall` and setup self-elevate. Keep the command line strict.
- Start with Windows is a per-user `HKCU\...\Run` value (`"exe" --tray`), not a task. The first run of a portable or installed copy turns it on once (`Initialized` flag).
- The installer launches the installed app through `explorer.exe` so it starts unelevated.
- Update checks run only when the flyout opens, at most once per 24 h (`LastUpdateCheck`), on a thread-pool thread with a 10 s timeout, inside try/catch; failures are ignored and retried on the next open. The latest tag seen is kept in `LatestVersion`.

## Code map (`src/`, C# 5, compiled by the in-box `csc.exe` of .NET Framework 4.8.1)

- `Program.cs`: entry point and modes (`--tray`, `--set/--delta/--off`, `--uninstall`, `--demo` in DEMO builds only, setup by file name), single instance (named mutex plus a "show" event), version and URL constants (`Program.Name/Id/Version/Url`).
- `TrayApp.cs`: NotifyIcon, refresh timer (reads the battery every N s for the icon and the chart history), watts icon rendering, power history (`Queue<Sample>`, max 60 min, readings under 0.5 W stored as 0), update check (GitHub releases API on flyout open, 24 h cooldown, TLS 1.2 forced, regex on `tag_name`), apply (background thread), demo trace (`#if DEMO`).
- `Flyout.cs`: the panel; a main page (headline, chart, limit toggle, slider, error/update notices) and a settings page behind the gear.
- `Ui.cs`: `Theme` (Fluent tokens from the registry, accent palette), `FluentForm` (owner-drawn item stack, hover tracking, DPI via `Px()`), items: `TextItem`, `Headline`, `Divider`, `Toggle`, `TitleBar`, `Slider`, `Chart`, `Notice`, `Segmented`, `Buttons`.
- `Settings.cs`: `HKCU\Software\Snapcap` values (Target, Delta, LastLimit, IconMode, Interval, History, CheckUpdates, LastUpdateCheck, LatestVersion, Initialized) and `Limits` (bounds and UI option lists).
- `Installer.cs`: elevation helpers, the apply task (register/run/verify), autostart, install/uninstall, and `SetupForm`.
- `Native.cs`: P/Invoke for the driver, battery IOCTLs, DWM attributes, dark menus (uxtheme ordinals 135/136), the app icon (`LoadImage` of resource 32512), working-set trim.
- `app.manifest`: asInvoker, PerMonitorV2. `app.ico`: generated by `tools/make-icon.ps1` (PNG frames 16/20/24/32/48/256).

## Build and test

- `build.cmd` builds `bin\Snapcap-Portable.exe` and copies it to `bin\Snapcap-Setup.exe`. `build.cmd demo` builds `bin\Snapcap-Demo.exe` with `--demo` (a hardcoded trace of a real session for screenshots). `bin/` is git-ignored.
- C# 5 only: no `$""`, `?.`, `nameof`, expression-bodied members or auto-property initializers. `dynamic` works but needs Microsoft.CSharp and costs size; use the `Com()` reflection helper instead.
- A running Snapcap locks the exe: stop it before building (`Stop-Process -Name Snapcap-Portable`; an elevated instance needs an elevated `taskkill /F`).
- UI testing: drive the mouse with `SetCursorPos`/`mouse_event` from a DPI-aware script and capture with `CopyFromScreen`. Input into an **elevated** window (setup) must come from an elevated process. A UAC prompt steals focus and closes the flyout. New exe paths show up among the hidden tray icons. The screen is 2944x1840 at 200%.
- The flyout is anchored to the bottom-right of the work area; positions shift when its height changes.
- **Claude Code's desktop app is an MSIX package, and everything launched from its shells runs inside the package context, elevated processes included: their `HKCU` writes go to a package-private copy of the registry.** Processes started outside the package (by Explorer, the Task Scheduler, the installer's `explorer.exe` launch, sign-in) use the real hive. So a registry value read from the shell can disagree with what the real app wrote. To check real behavior, launch through Explorer (for example `explorer.exe some.cmd`) and read the registry from there; don't trust shell reads of `HKCU\Software\Snapcap` or the `Run` key after the app or the task wrote them.
- The first limit change after installing can take several seconds while Windows Defender checks the new exe; `RunApplyTask` waits up to 60 s and the flyout shows "Applying…" meanwhile.
- Screenshots in `docs/` come from the demo build; crop with rounded transparent corners.

## Gotchas

- Windows PowerShell 5.1 `Set-Content`/`Get-Content` default to ANSI and BOMs: they mangled UTF-8 (`–` turned into `â€“`). Edit text with Python or the editor tools, or pass `-Encoding utf8` and strip the BOM.
- Paths in this environment can exceed 260 characters; PowerShell fails on them, bash `cp` doesn't.
- `TextRenderer.MeasureText("")` returns height 0: measure `" "` instead.
- GDI+ throws on zero-size arcs: `FillRound` skips empty rectangles.
- GDI+ `SetClip` with an inverted (negative-height) rectangle clips everything on a plain `Graphics` but gets flipped into a real band once the `Graphics` has a transform, as in a double-buffered partial repaint. The old chart clipped "below zero" that way and painted a flat 0 W line orange only sometimes (hovering), so tests that paint on a fresh `Graphics` missed it. Paint charts with a translated `Graphics` when testing.
- `Form` has a `Scale` method; the DPI factor field is `Zoom`.
- To test other scales without changing the display setting, create the form's handle first and then force the DPI (`SetDpi` by reflection); otherwise `OnHandleCreated` switches back to the monitor's real DPI.
- The Task Scheduler substitutes `$(Arg0)` into the command line as raw text; it is safe only because the first argument is always `--set` and the parser rejects anything unexpected.
- A Linux "Preserve Battery Health" limit persists into Windows; holding the power button for 15-30 s with the laptop off reportedly clears it.

## Releases

- v1.0.0 (published as "ChargeLimit", a single-window WinForms app) is on GitHub.
- v2.0.0 (Snapcap: tray, flyout, chart, installer) is on branch `v2-tray-ui`, **not committed yet** at the time of writing. Bump `Program.Version` for new releases; the update check compares it with the latest GitHub release tag (`vX.Y.Z`).
