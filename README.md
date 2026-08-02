# ProcessRecorderApp

English | [日本語](README.ja.md)

**A tray-resident event recorder that keeps the footage from *before* you pressed record.**
It continuously buffers video from a screen, a camera, or another source in the background, so
when you start a recording the resulting MP4 also contains the seconds — or tens of seconds —
that led up to it.

The app lives in the notification area and can drive several recordings (recorders) at once.
You can operate it from the GUI, launch the same executable with arguments to tell the
already-running instance to start and stop recordings, or **have it watch another application's
UI and start and stop recording on its own** when what you are waiting for appears, changes, or
goes away.

## Features

- **Event recording from a look-back buffer** — every recorder continuously keeps the most
  recent few seconds to tens of seconds of video in memory (10 seconds by default). Starting a
  recording writes that buffer out to the MP4 first, so whatever happened *before* you pressed
  record is part of the file.
- **Several recorders at once** — configure and record from multiple sources (screen, camera,
  and so on) simultaneously.
- **Video sources** — screen capture, webcams (through Media Foundation), and test patterns.
- **Live preview** — watch the selected recorder's video in the app window in real time.
- **Burnt-in timestamp** — the date and time are rendered into the recorded video.
- **Filename templates** — the output name is given as a template such as
  `{Now:yyyyMMdd_HHmmss}_{Name}.mp4`, and can embed the date and time, the recorder name,
  environment variables, and user-defined variables.
- **Triggers from another application's UI (UIA triggers)** — watch another application's UI
  elements through UI Automation and start or stop recording automatically when a condition is
  met. A trigger can also **record only while its condition holds** (start when it is met, stop
  when it no longer is). The fired value is always written to a variable (`{trigger id}`), so it
  can be embedded in the filename as well. Triggers are created on the Settings screen with a
  picker that captures elements on screen (powered by the
  [UiaTrigger](https://github.com/masa-iwm/UiaTrigger) library).
- **Automatic recovery from errors** — if the video source fails, recording is restarted
  automatically after a delay.

## Usage

### Launching and the notification area

Running the executable puts the app in the notification area; no window is shown.
To operate it, right-click the tray icon and choose **Show** (*表示* when the UI is in Japanese).

- **Minimizing** the window and the **close button (×)** both send the app back to the
  notification area instead of quitting it.
- To quit, right-click the tray icon and choose **Quit** (*終了* when the UI is in Japanese)
  — or hold **Ctrl** while clicking the close button.

### Screens

The menu along the top of the window switches between these screens.

| Screen | Contents |
|---|---|
| **Preview** | Live preview and settings per recorder (video source, buffer length, filename template, and so on). You edit the properties on the left and the video appears on the right. Recorders are added and removed here too. |
| **Log** | Shows the app's internal activity log in a terminal view, with ANSI colours and carriage-return line overwrites. The number of lines kept is capped, and any lines discarded before they could be shown are reported in the log itself. |
| **Variables** | Lists and edits the variables (key and value) used by filename templates. |
| **Settings** | Application-wide settings such as the window size. This is also where UIA triggers are created and edited (the "..." button opens the editor and the picker) and where each trigger's recording action (none / start / stop / record while matching) and target recorder are assigned. The **Reload** button at the top right re-reads `settings.json` from disk, for when you have edited the file by hand. It is disabled while any recorder is recording or still writing out a file. |

At the bottom of the window, below the menu, there are also **buttons that start and stop every
recorder at once**. Individual recorders are started and stopped from their own controls on the
Preview screen.

Detailed video-source settings — which monitor to capture, the camera resolution, and so on —
are configured from the property pane on the Preview screen by opening the pipeline editor
dialog.

### Recording driven by another application's UI

Recording can start and stop by itself when the screen you are waiting for appears, changes, or
goes away.

1. Open the **Settings** screen and press the **"..." button** on the *trigger list* row. The
   trigger editor opens.
2. Press **"Record new…"** to open the element picker. Point the mouse at the element you want to
   watch (with **"Follow the mouse"** on, the selection tracks the cursor), then press
   **"Confirm this element"**.
3. In **"Trigger condition"** below, decide when it fires.
   - **Fires on**: `ElementAppeared` / `ElementRemoved` / `PropertyChanged` /
     `WhileMatching` (when the condition starts holding)
   - To **record only while the condition holds**, choose `WhileMatching` *and* tick
     **"Also notify when it stops matching"** — without it the **recording never stops**.
   - If the watched application does not announce its changes, put `1` (or so) in
     **"Poll interval (s)"** and the element is re-read on that cadence.
4. Press **"Add trigger"**, then **OK** in the editor to save.
5. Back on the Settings screen, a row for the new trigger has appeared under
   **trigger assignments**. Open it and choose:
   - **Action**: *(none)* / *Start recording* / *Stop recording* / *Record while matching*
   - **Target recorder**: leave empty for every recorder

   Even with *(none)*, the fired value is still written to a variable.
   *Record while matching* **will not stop** unless step 3's "Also notify when it stops matching"
   is ticked — for a trigger where it is not, the choice itself says so
   ("Record while matching (will not stop: …)").
6. That value is available as the variable `{trigger id}`, so you can embed it in a filename
   template (for example `{Now:yyyyMMdd_HHmmss}_{MyTrigger}.mp4`).

> **Try it against the application you actually want to watch.** This relies on that application
> telling UI Automation that something changed, and **a value visibly changing on screen is not
> the same as a change being reported**. For applications that stay silent, use the poll interval
> above. When it does not behave as expected, read the lines starting with `trigger.` in
> `activity.log` — one line each for what fired, what was started or stopped, and what was
> skipped and why.

### Output files

- Recordings are saved as **MP4 files**. The destination comes from each recorder's filename
  template. A relative template is resolved against the **output folder** — the
  `OutputDirectory` setting, which defaults to the folder the executable is in. A relative
  `OutputDirectory` is resolved against that same folder, and a template that is already an
  absolute path ignores the setting entirely.
- Old recordings can be **deleted automatically**. Set `RecordingRetentionDays` to the number of
  days to keep (`0`, the default, deletes nothing) and `RecordingCleanupIntervalHours` to how
  often the sweep runs. It also runs once right after startup. Sub-folders of the output folder
  are searched too, only `.mp4` files are deleted, and a sub-folder is removed when deleting left
  it empty — the output folder itself is never removed.
- Application settings are stored in `%LOCALAPPDATA%\ProcessRecorderApp\settings.json`.
- The activity log is written to `%LOCALAPPDATA%\ProcessRecorderApp\activity.log`. It records
  application start and exit, which H.264 encoder was selected, whether each recorder
  initialized, **the start and end of every recording (the file actually written and its
  duration)**, recording errors and automatic-recovery attempts, and the command lines that were
  run together with their exit codes. Once it exceeds 1 MB it is rotated to `activity.log.1`
  (one generation is kept).

  ```
  2026-07-26 12:00:51.233 INFO recording.start recorder='R1' file='C:\rec\R1_120050511.mp4'
  2026-07-26 12:00:56.417 INFO recording.stop  recorder='R1' file='C:\rec\R1_120050511.mp4' elapsedMs=5172 result=ok
  ```

## Automating from the command line

Launching the same executable with arguments forwards the request to the instance that is
already resident (no new process stays around). The launcher **waits for the result and exits
with that command's exit code** — normally instantaneous, but it can wait up to **60 seconds**,
for example right after the resident worker has started. This is meant for driving recordings
from batch files and external scripts that **check the exit code**.

| Command | Description |
|---|---|
| `ProcessRecorderApp.exe activate` | Shows the window. |
| `ProcessRecorderApp.exe start-recording-all` | Starts recording on every recorder. |
| `ProcessRecorderApp.exe stop-recording-all` | Stops recording on every recorder. |
| `ProcessRecorderApp.exe start-recording <target>` | Starts recording on the given recorder. A numeric `<target>` is a zero-based index; anything else is a recorder name. |
| `ProcessRecorderApp.exe stop-recording <target>` | Stops recording on the given recorder. |
| `ProcessRecorderApp.exe status` | Reports the state of every recorder, one per line, as TAB-separated `name`, `initialized`, `recording`, `last file`, `last failure`. If any recorder is not initialized or its last failure is still showing, the reason is written to standard error and exit code `15` is returned. |
| `ProcessRecorderApp.exe --set KEY=VALUE` | Sets a variable used by filename templates (may be repeated). The variable lasts for as long as the app keeps running; it is **not** written to `settings.json` unless you ask for that with `--persist`. |
| `ProcessRecorderApp.exe --get [KEY]` | Reads a filename-template variable (may be repeated). With no key, every variable is listed. For a key that is not defined, the key name is written to standard error and exit code `11` is returned. |
| `ProcessRecorderApp.exe --persist KEY` | Keeps the named variable in `settings.json` so it survives a restart (may be repeated). Same treatment of an undefined key as `--get`. |
| `ProcessRecorderApp.exe --no-persist KEY` | Stops keeping the named variable in `settings.json`. The variable itself stays for the rest of the session. |
| `ProcessRecorderApp.exe ping` | Liveness check. Writes a log entry only — no window and no notification. |
| `ProcessRecorderApp.exe --help` | Lists the commands. |

The outcome of a command is reported through the **exit code** (`%ERRORLEVEL%` /
`$LASTEXITCODE`) of the process you invoked. `0` means success and anything else means a
failure of some kind (see [src/README.md](src/README.md) for the full list).

Example — starting a specific recorder from a batch file:

```bat
ProcessRecorderApp.exe start-recording "Recorder #1"
if errorlevel 1 echo Failed to start recording
```

## Requirements

- Windows 11 (x64)
- Unpackaged, no installation required (Native AOT, self-contained build)

## Download

Pre-built packages are on the
[Releases page](https://github.com/masa-iwm/ProcessRecorderApp/releases). Each release
carries two zip files; unpack one and run `ProcessRecorderApp.exe` — nothing is installed.

- `ProcessRecorderApp-<version>-win-x64-gstreamer.zip` — **bundled**: ships the GStreamer
  runtime the app needs (LGPL components only; the inventory is in
  [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)). Works on its own.
- `ProcessRecorderApp-<version>-win-x64.zip` — **non-bundled**: contains no third-party
  runtime. At start-up the app resolves a GStreamer (MinGW 64-bit) installation provided by
  you. Pick this one to use your own GStreamer build — including GPL-licensed encoders such
  as `x264enc`, which the bundled runtime deliberately leaves out.

## Repository layout

- `src/ProcessRecorderApp/` — the main application (UI and startup)
- `src/GStreamer.GirCore/` — the recording and preview engine
- `src/SingleInstance/` — tray residency and single-instance control
- `src/Components/` — shared components
- `src/Controls/` — GUI parts
- `docs/` — development notes (test harness, CI, GPU verification, runtime updates)

For implementation details (architecture and technology stack) see
[src/README.md](src/README.md).

## License

The application itself is MIT licensed ([license.txt](license.txt)).

The **bundled** distribution ships GStreamer and its dependencies; see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the inventory and their licenses
(the **non-bundled** distribution contains none of them — it resolves a GStreamer
installation provided by the user at run time).

The full licence texts of the bundled components are in
[licenses/third-party/](licenses/third-party/), taken verbatim from upstream; the
bundled package ships them alongside the binaries.
