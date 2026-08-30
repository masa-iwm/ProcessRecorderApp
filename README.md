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
- **Continuous recording alongside it** — each recorder can also keep a second, always-on
  recording at its own frame rate, resolution and encoder settings, cut into files on a fixed
  interval. The pre-buffer does not apply to it.
- **Video sources** — screen capture, webcams (through Media Foundation), and test patterns.
- **Live preview** — watch the selected recorder's video in the app window in real time.
- **Watch a recording while it is still being recorded** — every file the app writes — event
  recordings and continuous segments alike — stays readable from the first byte, both while
  recording and after a forced shutdown; the browser page can then play it and keep following
  it as it grows.
  Other players cannot seek in such a file until the recording stops.
  Turn `FragmentedOutput` off (it is on by default) to get plain MP4 files instead, which are
  seekable once finished but empty while they are being written.
- **Remote control from a browser** — optionally runs a small HTTP server so that a
  browser on another PC on the same LAN can watch the recorders, start and stop them, change
  settings, browse past recordings and see a live preview. Off by default, with **named users in
  three roles** (`Viewer` / `Operator` / `Admin`) and an optional guest read — see the section
  below before turning it on. An `Admin` can also switch a recorder's **video source** from the
  page, by picking one of the supported source elements and filling in its properties — the
  pipeline string itself is built on the app's side and never accepted from the browser.
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
  automatically after a delay. For a camera or a screen capture the app also watches for the
  device coming back, so unplugging and replugging it recovers within about a second instead of
  waiting out the delay — and a recorder that could not start at all because its device was
  missing starts as soon as the device appears.

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

The preview can be shown **full screen**: use the full-screen button in the property pane,
**F11**, or **double-click** the preview, and press **Esc** to leave. While full screen, the
**left and right arrow keys** switch recorders and the **up and down arrow keys** switch the
framing guide. Right-clicking offers all three: the recorder, the guide, and full screen.

**Framing guides** (rule of thirds, golden ratio, crosshair, square) can be drawn over the
preview. Pick one with `FramingGrid` on the Settings screen, from the preview's right-click menu,
or with the up and down arrow keys while full screen. The lines follow the area the video actually
occupies, not the whole panel.

When you **add** a recorder you can either start from the defaults or **copy the settings of an
existing recorder**. A copy keeps every setting and only its name is adjusted so that it stays
unique.

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
- A finished recording gets a small **sidecar file** beside it — `<name>.mp4.json`, holding the
  recorder name, the start and end time, the duration, the video size and the **trigger** that
  started it (`manual`, `uia:<id>`, `remote`, `cli` or `continuous`) — together with a
  `<name>.mp4.png` thumbnail taken near the trigger moment. Both are written on a best-effort
  basis: if they cannot be written the recording is unaffected, and the listing still shows the
  same recording without them.
- Old recordings can be **deleted automatically**. Set `RecordingRetentionDays` to the number of
  days to keep (`0`, the default, deletes nothing) and `RecordingCleanupIntervalHours` to how
  often the sweep runs. It also runs once right after startup. Sub-folders of the output folder
  are searched too, only `.mp4` files and their sidecars are deleted, and a sub-folder is removed
  when deleting left it empty — the output folder itself is never removed.
- Application settings are stored in `%LOCALAPPDATA%\ProcessRecorderApp\settings.json`.
  A matching **JSON Schema** is written next to it as `settings.schema.json`, and the settings file
  points at it with `$schema` — an editor that understands JSON Schema (VS Code and others) then
  gives you completion and validation while you edit the file by hand. The same schema is kept in
  the repository as [docs/settings.schema.json](docs/settings.schema.json).
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
| `ProcessRecorderApp.exe status` | Reports the state of every recorder, one per line, as TAB-separated `name`, `initialized`, `recording`, `awaiting resume`, `last file`, `continuous`, `continuous file`, `last failure` — the free-text failure is always the last column. `awaiting resume` means the recording was finalised by an automatic recovery and will be picked up again in a new file once the device is back; while the device is away you see `initialized=False`, `recording=False`, `awaiting resume=True`. If any recorder is not initialized or its last failure is still showing, the reason is written to standard error and exit code `15` is returned. |
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

## Remote control from a browser

The app can run a small HTTP server of its own so that a browser on **another PC on the same
LAN** can watch what it is doing and drive it. It is **off by default**.

**Turning it on.** Switch on `RemoteControlEnabled` on the Settings screen. An access token is
generated the first time you do (32 random bytes, Base64Url — 43 characters) and is shown in the
`RemoteControlAccessToken` row. That row is read-only — you can select and copy the token, but the
only way to change it is the "…" button next to it, which mints a new one and invalidates the old
token together with every browser session opened with it. The server listens on
`RemoteControlBindAddress` (`0.0.0.0` by default, meaning every network interface) and
`RemoteControlPort` (`8752` by default; `0` lets the operating system pick a free port, and the
port actually taken is recorded in `activity.log` as `remote.start`). From the other PC, open

```
http://<the IP address of the PC running the app>:8752/?token=<the access token>
```

once. The server answers with a session cookie (`HttpOnly`, `SameSite=Strict`, gone when the
browser closes) and redirects to `/`, so the token never has to appear in the address bar again.
Scripts can send `Authorization: Bearer <the access token>` instead of holding a cookie. The token
is **the administrator's key**: whoever holds it may do everything.

**Users and roles.** Instead of handing the token around, add named users on the Settings screen —
the `RemoteUserList` row shows how many there are and its "…" button opens the editor. Each user
has a password (stored only as a PBKDF2-SHA256 hash; the plain text is never written anywhere) and
one of three roles:

| Role | May do |
|---|---|
| `Viewer` | Read everything the page shows, including the live preview. |
| `Operator` | Everything a `Viewer` may, plus start/stop recordings and set filename-template variables. |
| `Admin` | Everything, including changing the application and recorder settings. |

Users sign in on the page itself (the form appears whenever the server answers `401`), and the
session lasts **at most 24 hours** — it is not extended by use. Turning `RemoteControlAllowGuestRead`
on lets anyone who can reach the port read without signing in, exactly as in previous versions;
leaving it off (the default) means **even reading needs a sign-in**.

**Changing users or the guest setting restarts the server**, which signs everybody out. Changing
the token, the bind address or the port does the same.

**What you get.** The page lists every recorder with the same state the `status` command reports
and lets you start and stop them one at a time or all at once; edit each recorder's settings and
the application settings; read and write filename-template variables; browse the recordings under
the output folder and play or download them; and watch a **live preview** of a recorder. An
`Admin` can also swap out a recorder's `SrcPipeline` by picking one of the sources the app
enumerates — a screen, a window, a camera and so on — together with its parameters; a
hand-written pipeline is not accepted (`PUT /api/recorders/{id}/source`). The page
is pushed the new state whenever it changes, so it follows what you do in the GUI as well. The
preview comes in **two modes**: **recording quality (low latency)**, the default, reuses the H.264
stream that is already being encoded for recording, so it costs no extra encoding; **preview
settings (DASH)** re-encodes at the resolution, frame rate and bitrate you set per recorder.
The re-encoded mode also offers the **presets 1080p, 720p, 480p and 360p**, which shrink to the
source rather than blowing it up: a preset taller than what the recorder captures is not offered,
and the width follows the source's own aspect ratio. Picking one takes an `Operator`; the choice
belongs to the recorder and is shared by everyone watching it, is not written to the settings file
and is forgotten when the application exits. Either way the price is latency:
**roughly 2 to 3 seconds**.

**Recording transcode.** A finished recording can also be played back re-encoded, at the same
1080p / 720p / 480p / 360p presets: the quality menu of the recording player offers those that are
not larger than the file, the conversion starts from wherever the playback is, and it needs a
**hardware H.264 decoder** — on a PC without one the menu is not drawn at all (the bundled runtime
carries no software H.264 decoder). `RemoteAuxiliaryEncoderLimit` (`2` by default, `1` to `8`) is
how many of these conversions and re-encoded live previews may run **at the same time**, counted
across the whole application; beyond it the page says `auxiliary encoder busy` until one of them
ends.

**Pages and appearance.** The page is split into three: **Live** (`#/live`), **Recordings**
(`#/recordings`) and **Settings** (`#/settings`), reached from the links in the top bar; switching
between them does not stop a preview or a playback that is running. The button next to them
switches the colour scheme between light and dark and remembers the choice in that browser;
without a choice it follows the operating system.

**The Recordings page.** A calendar on the left picks the day and a drop-down picks the recorder;
the table on the right holds that day's recordings and nothing else. Days that have recordings
carry the count as a badge, and the arrows move a month at a time without losing the day you
picked. Each row shows a thumbnail taken at the start of the recording, the filename and the
recorder, the size, the start time, the state, the length and **what started it** — by hand, by a
UI trigger (with the trigger's id), from the browser, from the command line, or as a segment of a
continuous recording. The start reason is written beside the recording when it finishes, so a
recording that is still running does not show one yet. The list refreshes itself as recordings
appear and finish; the Refresh button is there for when you would rather ask.

**Player controls.** The preview and the recording playback share one control bar: skip by 10 or
30 seconds, volume and full screen. Playback speed (0.5× to 2×) is offered for a recording only: a
live picture is held at the live edge, so a raised rate would be given back at once. On a live
picture the quality — recording quality, one of the presets, or the recorder's own preview
settings — is switched from there too; entries your role may not write are shown greyed out.

**Security — read this before turning it on.**

- **Reading is not free unless you say so.** With `RemoteControlAllowGuestRead` on, the recorder
  list and state, the application settings, the recorder settings (including `SrcPipeline`, the raw
  GStreamer pipeline, and `FilenameTemplate`, which may be an absolute path), the list of
  recordings, the recording files themselves and the live preview become readable by **anyone who
  can reach the port** — whatever is on the captured screen is on the LAN. The web page itself
  (`/` and the scripts and style sheet it names) is always served without a sign-in, because that is where the sign-in
  form comes from. The access token and the stored password hashes are **never** part of any
  response.
- **Writing needs a session, and an `Admin` session is as good as sitting at the machine.**
  Starting and stopping recordings and setting variables need `Operator`; changing settings needs
  `Admin`; the access token counts as `Admin`.
  Recorder settings are filtered too: `SrcPipeline`, `EncodingProperties`,
  `ContinuousEncodingProperties`, `FilenameTemplate` and `ContinuousFilenameTemplate` are refused
  (400), because the first three *are* what the app runs — the encoder strings are interpolated
  into the pipeline exactly as they stand — and the other two accept absolute paths, which would
  write outside `OutputDirectory`. Application
  settings are restricted to a fixed allow-list, so `OutputDirectory`, the debug paths and the
  `RemoteControl*` settings themselves cannot be changed remotely either.
- **Plain HTTP, no TLS.** There is no mDNS either — you address the PC by its IP. Use this only
  on a network you trust, and leave it off otherwise.
- **Windows Firewall is not opened for you.** Add the rule yourself, for example:

  ```powershell
  New-NetFirewallRule -DisplayName "ProcessRecorderApp remote" -Direction Inbound -Protocol TCP -LocalPort 8752 -Program "<full path to the executable>" -Profile Private
  ```

**Limits.**

- **Keep it to one tab.** HTTP/1.1 allows six connections per origin and the page holds two of
  them open permanently — one for the state stream, one for the preview.
- The preview allows **4 viewers per recorder and 8 in total**; past that the request is refused
  with `503`.
- A recording that is still being written can be **followed while it is written** as long as it is
  **fragmented** (the default): the page plays it through Media Source Extensions and keeps up with
  the tail. With `FragmentedOutput` turned off the file can only be **downloaded, not played** —
  a non-fragmented MP4 becomes playable only once its index has been written at the end.
- Deleting recordings, `HEAD` requests, HTTPS and mDNS are not supported. The browsers this is
  built for are **Chrome and Edge on a PC**.

## Requirements

- Windows 11 (x64)
- Unpackaged, no installation required (Native AOT, self-contained build)

## Download

Pre-built packages are on the
[Releases page](https://github.com/masa-iwm/ProcessRecorderApp/releases). Each release
carries three zip files; unpack one and run `ProcessRecorderApp.exe` — nothing is installed.

- `ProcessRecorderApp-<version>-win-x64-gstreamer-mingw.zip` — **bundled (MinGW)**: ships the
  GStreamer runtime the app needs (LGPL components only; the inventory is in
  [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)). Works on its own, with no
  prerequisites. **Start here if you are unsure.**
- `ProcessRecorderApp-<version>-win-x64-gstreamer-msvc.zip` — **bundled (MSVC)**: the same
  selection built with MSVC. Half the size, and screen capture additionally offers
  `capture-api=wgc` (Windows Graphics Capture). **Requires the Microsoft Visual C++
  redistributable (x64)** on your machine — the C/C++ runtime is not bundled.
- `ProcessRecorderApp-<version>-win-x64.zip` — **non-bundled**: contains no third-party
  runtime. At start-up the app resolves a GStreamer (MinGW or MSVC 64-bit) installation
  provided by you. Pick this one to use your own GStreamer build — including GPL-licensed
  encoders such as `x264enc`, which the bundled runtimes deliberately leave out.

## Repository layout

- `src/ProcessRecorderApp/` — the main application (UI and startup)
- `src/GStreamer.GstSharpNet/` — the recording and preview engine
- `src/SingleInstance/` — tray residency and single-instance control
- `src/Components/` — shared components
- `src/RemoteControl/` — the built-in HTTP server for remote control
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
