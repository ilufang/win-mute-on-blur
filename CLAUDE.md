# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## MuteOnBlur

Windows tray tool (C# WinForms, .NET 10, no NuGet packages). It mutes "managed" apps in the volume
mixer while their window is not in the foreground, and unmutes them when they get focus back.
`IconGen/` is a separate helper project; ignore it.

## Commands

- Build: `dotnet build -c Debug`. If the tray app is running, the copy step fails because the exe is
  locked. Either exit it, or check that the code compiles with `dotnet build -c Debug -o <scratch dir>`.
- Run: `dotnet run` (or start `bin\Debug\net10.0-windows\MuteOnBlur.exe`). A second instance only shows
  a message box.
- Publish: `dotnet publish -c Release` produces a self-contained single-file exe. Add
  `-p:SelfContained=false` for a small exe that needs the .NET 10 Desktop Runtime.
- There are no tests or linters. Verify by reading `%APPDATA%\MuteOnBlur\log.txt`; only Debug builds
  write the focus and mute trace there. PowerShell 5.1 needs `Get-Content -Encoding UTF8` to read the
  log, otherwise non-ASCII exe names come out garbled.

## Control flow

1. `Program` takes the `Local\MuteOnBlur` mutex, which keeps it to one instance. It then starts `Log`
   and runs `TrayApp` on the STA UI thread.
2. `TrayApp` creates a `FocusWatcher`, then a `MuteController` seeded with the current foreground app,
   and wires `FocusWatcher.Changed` to `MuteController.SetForeground`.
3. `FocusWatcher`, on the UI thread, checks the foreground window on a 500 ms WinForms timer.
   - WinEvent callbacks (foreground change, minimize-start, minimize-end) arrive through the message loop.
     They are logged, then only pull the next check forward to 30 ms. A burst of events collapses into one
     check.
   - Events are only wake-ups because their timing cannot be trusted. Seen with Win-D: one arrived before
     `GetForegroundWindow` changed, a stale desktop event arrived after a real switch, and sometimes none
     came at all.
   - The check reads `GetForegroundWindow`.
     - A minimized foreground window means no app has focus, reported as a null app id.
     - Otherwise shell surfaces (taskbar, Alt-Tab), its own windows and ignored apps are dropped, and the
       previous app stays.
     - Win-D makes the desktop `WorkerW` the foreground window, which counts as leaving the app.
   - When the app id changes, it raises `Changed`. Quiet polls are not logged.
4. `MuteController` keeps every Core Audio COM object on one dedicated MTA thread, because session
   notifications only reach MTA clients. Public methods post actions to a `BlockingCollection` queue.
   - Reconcile rule: mute a session iff its app is managed and is not the foreground app.
   - It unmutes only sessions it muted itself (`_mutedByUs`, keyed by session identifier). That set is
     persisted, so a run after a crash can undo the mutes left behind.
   - New sessions are reconciled immediately via the `IAudioSessionNotification` callback.
   - A poll every 2 s refreshes the output devices and catches sessions whose notification was missed.
5. Tray menu: left- or right-click rebuilds the menu from `MuteController.GetSnapshot()`, a blocking
   round-trip to the audio thread.
   - The top level lists running managed apps (click to unmanage), then two cascades: "Managed" holds
     managed apps that are not running, "Playing" holds currently playing unmanaged apps (click to
     manage). Exit comes last.
   - The menu stays open after a toggle and rebuilds itself, reopening the cascade the click came from.
6. Exit, triggered from the menu or by Windows session end: stop the poll and unhook the WinEvents, then dispose the
   controller. Disposing unmutes the sessions it muted, saves state and stops the audio thread.

## Files

- `Program.cs`: entry point, single-instance mutex, global exception logging.
- `TrayApp.cs`: `ApplicationContext`. Owns the tray icon (embedded `app.ico`), builds the menu, caches
  each exe's description and icon, and handles exit. A menu item reads "Name (exe)": the name is the
  snapshot's mixer name, falling back to the exe's description.
- `FocusWatcher.cs`: tracks the foreground app. It resolves UWP apps hosted in ApplicationFrameHost to
  the child app's process, and holds the ignore lists (`IgnoredClasses`, `IgnoredApps`). The Start
  menu/search window (`searchapp.exe`) and the desktop (`WorkerW`/`Progman`) are deliberately *not*
  ignored, so opening either one mutes the app.
- `MuteController.cs`: audio thread, device and session management, mute reconciliation, the snapshot
  used by the menu (which also records the names of managed apps), and the shutdown unmute.
- `AudioSession.cs`: wrapper around one audio session. It filters out system, own-process and dead
  sessions, and exposes the ids, mute state (set with a fixed event-context GUID) peak level and
  display name.
- `AppNames.cs`: the name the volume mixer shows for an app, used in the snapshot. Tries the session
  display name, then the package-level DisplayName from the app's AppxManifest.xml (Apple Music plays
  from `amplibraryagent.exe`), then the title of the process's main window (Siglus games).
- `CoreAudio.cs`: hand-written COM interop for the MMDevice and audio session APIs, plus
  `SessionNotifier`.
- `Win32.cs`: P/Invoke declarations and helpers. An app id is the lower-cased exe file name.
- `Storage.cs`: JSON files under `%APPDATA%\MuteOnBlur\`. `config.json` holds `{"managed": [...], "names": {appId: name}}`, where `names` is each managed app's
  mixer name as first seen running (shown while it is not running). and
  `muted-by-us.json` holds session identifiers. Writes are atomic (temp file + move).
- `Log.cs`: `log.txt` in the same folder, reset at startup once it exceeds 1 MB. Severity levels are
  `Debug`/`Info`/`Warning`/`Error`; Debug lines reach the file only in Debug builds.
