# Technical notes

## How the limit is set

Qualcomm's `qcpmicglink` driver exposes `\\.\QCOMBATTMGR`. That device turns IOCTL `0x80092044` into the battery-manager firmware request `BATTMGR_CHG_CTRL_LIMIT_EN` (opcode `0x48`) and sends it to the ADSP over PMIC GLink. The input is three `uint32` values:

| Offset | Field        | Value                                       |
|--------|--------------|---------------------------------------------|
| 0      | `enable`     | always `1`                                  |
| 4      | `target_soc` | stop charging at this % (`100` = no limit)  |
| 8      | `delta_soc`  | resume charging at `target_soc - delta_soc` |

Linux's [`qcom_battmgr`](https://github.com/torvalds/linux/blob/master/drivers/power/supply/qcom_battmgr.c) driver sends the same request for `charge_control_end_threshold`, and Snapcap uses the same bounds: stop at 55–100%, resume no lower than 50%.

The IOCTL was found by reverse engineering the Windows driver. It isn't documented by Qualcomm and could change with a driver update; in that case the request would most likely just fail.

The firmware doesn't report the current limit back, so Snapcap remembers what it last applied in `HKCU\Software\Snapcap`. Battery status comes from the standard Windows battery IOCTLs; the watts are the battery's own charge or discharge rate.

## Privileges

The device only accepts administrators, so:

- **Installed:** Snapcap runs as a normal user, and the installer registers an on-demand scheduled task, "Snapcap", that runs `Program Files\Snapcap\Snapcap.exe --set <target> --delta <delta>` with the highest privileges. Snapcap starts it through the Task Scheduler API, so changing the limit needs no prompt. The task only points at the copy in Program Files, which only administrators can modify. Whatever starts the task can only pass two numbers, and they are validated before anything is sent.
- **Portable:** Snapcap also runs as a normal user, and each change starts the exe elevated, with a UAC prompt. A no-prompt task is never used for it, because its folder is writable by the user. If Snapcap is also installed, the portable copy uses the installed copy's task.

Start with Windows is a per-user `Run` entry.

## Command line

```bat
Snapcap.exe --set 80 [--delta 1]
Snapcap.exe --off
```

No window is shown; the exit code is `0` or a Win32 error code (`87` for invalid arguments). Values are limited to 55–100 for `--set` and 1–20 for `--delta`, with the resume point no lower than 50%.

## Design

- **Small, nothing to install.** C# 5 compiled by the `csc.exe` that ships with Windows' .NET Framework 4.8.1. WinForms supplies the message loop and tray icon; the rest is Win32.
- **Windows 11 look.** The panel is drawn by hand with Fluent colors, the system accent color, light/dark mode, Segoe UI Variable, Segoe Fluent Icons and DWM rounded corners.
- **Light in the background.** Between readings the process sleeps; a reading is one call to the battery driver. The chart keeps at most an hour of readings, the panel is created on demand and destroyed when it closes, and unused memory is returned to Windows (about 10 MB in the tray).
- **Network.** The only connection is the daily update check: one HTTPS request to `api.github.com` for this repository's latest release.
- **One binary.** `Snapcap-Setup.exe` and `Snapcap-Portable.exe` are the same file; "setup" in the name makes it install itself.

## Build

```bat
build.cmd
```

Builds `bin\Snapcap-Portable.exe` and `bin\Snapcap-Setup.exe` with no SDK or Visual Studio. `build.cmd demo` builds `bin\Snapcap-Demo.exe`, whose `--demo` switch fills the chart with sample history for screenshots. `tools\make-icon.ps1` redraws `src\app.ico`.
