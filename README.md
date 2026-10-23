# RDP Monitor Switcher

Remote Desktop with TeamViewer-style monitor switching: **one** RDP connection to a remote PC with several (virtual) monitors. You either switch between monitors in one window, or open each remote monitor in its own freely movable window. The session stays connected the whole time.

```
[ ● Verbunden ] [ Trennen ] ◀ [ Monitor 1 ] [ Monitor 2 ] [ Monitor 3 ] ▶ ＋ －  ⧉ Fenster je Monitor   …  Anpassen  ⛶ Vollbild
```

> **Built with AI:** this project was developed entirely by an AI ([Claude Code](https://claude.com/claude-code) by Anthropic): architecture, native FreeRDP integration, app and tests. The repository owner provided the idea and requirements and tested the app on real hardware. The code was not hand-written by a person.

---

## 1. Technical decision (summary)

**Key fact:** RDP does not stream the remote PC's *physical* monitors (unlike TeamViewer). In an RDP session, the **client** decides how many monitors the session has and what size they are. Windows creates those virtual monitors and moves the physical screens to the lock screen. `selectedmonitors` in `.rdp` files picks **local** monitors, not remote ones.

| Variant | Technically possible? | Used here |
|---|---|---|
| 1. Switching inside the same session | **Yes**, if the client declares several virtual monitors and shows only one of them | **Yes: the core feature** |
| 2. Changing the monitor layout inside a running connection | **Yes**, via the Display Control channel (MS-RDPEDISP, Windows 8.1+ on the remote PC) | **Yes:** window size, ＋/－ monitors |
| 3. Reconnecting with a different monitor setup | Yes, takes 2–5 s | Fallback only, and **announced in the UI** |
| 4. Separate connections to different PCs | Possible, but not needed | No |
| `mstsc.exe` / `.rdp` files only | Not enough control (image can't be cropped, can't be embedded) | No |
| Microsoft RDP ActiveX (MSTSCAX) | Only **one** large monitor can be declared, image can't be cropped | No |

**Implementation:** The app uses **FreeRDP 3.32.2** (Apache 2.0) through a small, self-written C bridge (`RdpBridge.dll`). On connect it declares *N* virtual monitors to the remote PC; Windows treats them as real separate monitors (taskbar, maximizing per monitor, Win+Shift+Arrow). FreeRDP decodes the whole remote desktop into **one** image. Each window shows only the rectangle of its selected monitor, scaled to fit, and mouse coordinates are shifted by that monitor's offset.

➡ **Switching monitors is purely local:** instant, no new connection, no renegotiation. That is variant 1, Priority A.

## 2. Answers to the questions from the requirements

1. **Which RDP client is actually used?** FreeRDP 3.32.2 (libfreerdp + libfreerdp-client, built statically via vcpkg) behind a separate C API (`native/RdpBridge`). No mstsc, no ActiveX.
2. **Does the existing RDP session stay up when switching monitors?** **Yes.** Switching (button, Ctrl+Alt+←/→/1…9) and opening separate monitor windows do not touch the connection.
   Resizing a window or changing the monitor count with ＋/－ is also applied **inside the running connection** via Display Control.
3. **Is a new connection ever made?** Only in two cases, and the app always says so:
   - The remote PC does not support Display Control (older than Windows 8.1, or it hasn't confirmed the change within 6 s) **and** the *number* of monitors changed. A notice appears: "… die Verbindung wird mit N Monitor(en) neu aufgebaut".
   - The network connection dropped (automatic reconnect with 1/2/5/10 s back-off, up to 10 attempts). Windows reconnects you to **the same** user session; open programs stay open.
4. **Requirements on the remote PC:** see section 3.
5. **How do I test it myself?** See section 7.

## 3. Requirements

### Local PC (where you run the app)
- Windows 10 or 11, x64.
- **Nothing to install**: no .NET, no Visual C++ runtime, no FreeRDP, no admin rights. Copy the folder `dist\RdpMonitorSwitcher` and start `RdpMonitorSwitcher.exe`.
- Only on Windows "N" editions: the *Media Feature Pack* for H.264. Without it, turn off "H.264" in the connection dialog. Everything else still works.

### Remote PC (the one with the monitors)
- Windows 10/11 **Pro, Enterprise or Education** (Home cannot accept RDP connections), or Windows Server.
- Remote Desktop enabled: *Settings → System → Remote Desktop → On*.
- Your user is allowed to sign in via RDP (administrators are by default; others via "Remote Desktop users").
- Recommended: Network Level Authentication (NLA) on (the default).
- For live monitor changes: Windows 8.1 / Server 2012 R2 or newer (Display Control).
- Group policy "Limit maximum number of monitors" (default 16) must allow at least as many monitors as you want.
- **No extra software** on the remote PC. Only the optional "follow the active window" feature needs the helper (see 4.).

### Reaching the remote PC
The app speaks plain RDP, so it works on any route RDP gets through. While disconnected, the app shows the same guide under **"Einrichtung: Wie erreiche ich meinen PC?"**.
- **Same home network:** nothing extra. Enter the PC name or its local IP.
- **From outside, A – Tailscale (recommended):** see below. Works even without a public IPv4 (DS-Lite).
- **From outside, B – the router's VPN** (e.g. FRITZ!Box WireGuard, FRITZ!OS 7.50+): connect the VPN, then use the PC's local IP. Wake-on-LAN through the FRITZ!Box then works from outside too. Needs a public IPv4 or IPv6.
- **From outside, C – port forwarding: not recommended.** RDP exposed directly to the internet gets found and attacked within hours. If you do it anyway: use an external port other than 3389, a long unique password and account lockout, keep Windows updated, and remove the forwarding when you no longer need it.

### Tailscale
Enable **Run unattended** (tray icon → Preferences) on the remote PC, so it's reachable after a reboot before anyone signs in, and **Disable key expiry** for it in the Tailscale admin console.
Otherwise works without any special setup. As the host, enter the Tailscale IP (`100.x.y.z`) or the MagicDNS name (`office-pc` or `office-pc.<tailnet>.ts.net`).
- The first time, a certificate warning appears because the RDP certificate is issued to the computer name. Check it, then click **"Immer vertrauen"** (always trust); it's saved under `%APPDATA%\RdpMonitorSwitcher\…\server`.
- Login uses NTLM (Kerberos is not available over Tailscale). That's the same as mstsc over Tailscale.

## 4. Usage

1. **Verbinden** (Connect) → enter host, user (`user`, `DOMAIN\user` or `user@domain`) and password.
2. Choose **Monitore** (monitors):
   - *An Fenster anpassen* (fit to window, recommended): enter a count; every remote monitor gets exactly the size of the window showing it, so there are no black bars.
   - *Feste Auflösungen* (fixed resolutions): e.g. `2560x1440, 1920x1080`, matching the remote PC's physical monitors.
3. After connecting, monitor 1 appears. Switch with the **Monitor buttons** in the toolbar (the active one is blue; resolution shown underneath) or with shortcuts.

### One window per monitor
**⧉ Fenster je Monitor** (or `Ctrl+Alt+M`) opens every other remote monitor in its own window. Each window can be dragged to any local monitor, resized, maximized or switched to fullscreen. All windows share **the same** RDP session; you can drag remote windows across. Closing a monitor window does not close the session. The app remembers each monitor window's position.

### Shortcuts (while the remote screen has focus)
| Keys | Action |
|---|---|
| `Ctrl+Alt+←` / `→` | previous / next monitor |
| `Ctrl+Alt+1` … `9` | select a monitor directly |
| `Ctrl+Alt+Enter` (or `Ctrl+Alt+Break`) | fullscreen on/off |
| `Ctrl+Alt+T` | show/hide toolbar (when hidden or in fullscreen: move the mouse to the top edge) |
| `Ctrl+Alt+M` | open every monitor in its own window |
| `Ctrl+Alt+End` | send Ctrl+Alt+Del to the remote PC |
| `Ctrl+Alt+D` | fetch the selected remote files (while dragging: continue the drag locally; otherwise: local clipboard) |

All other keys, **including the Windows key and Alt+Tab**, go to the remote PC while its screen has focus. To get out locally, click outside the window or leave fullscreen with `Ctrl+Alt+Enter`. When switching monitors or losing focus, all held keys and mouse buttons are released, so nothing gets "stuck" remotely.

### Getting files out of the remote PC (drag out)
RDP has no drag & drop channel; while you drag in the remote Explorer, the drag happens entirely on the remote PC. The app therefore takes over the drag:

1. In the **remote** Explorer, drag one or more files/folders as usual.
2. Drag to the **zone at the window edge** (default: right, highlighted in blue while you drag), drag past the window edge, **or** press `Ctrl+Alt+D`.
3. The app cancels the drag on the remote side, copies the selection there (Ctrl+C) and fetches the files over the RDP clipboard channel. A small progress window appears next to the mouse (`Esc` cancels).
4. **Keep holding the mouse button.** The drag continues on your local PC; drop the files onto the desktop or into an Explorer folder.

Without a pressed mouse button, `Ctrl+Alt+D` or the toolbar button **⇩ Dateien holen** puts the selected remote files on the **local clipboard**, ready to paste with `Ctrl+V`. If you let go of the button during a drag before the transfer finishes, the files also land on the clipboard.

Settings (**⚙** in the toolbar): turn the zone on/off, edge (right/left/top/bottom), width, and delay (how long the button must be held before the zone fires, so clicks near the edge don't trigger it). Requirement: "Zwischenablage teilen" (shared clipboard) is enabled in the connection. Files are downloaded first to `%TEMP%\RdpMonitorSwitcher\transfer\` (cleaned up automatically after 1 day). Remote file names are checked so nothing can be written outside that folder.

### Getting files onto the remote PC
- **Copy & paste:** copy files or folders in your **local** Explorer (`Ctrl+C`), then press `Ctrl+V` in the **remote** Explorer, the same as with mstsc. The app offers the files over the RDP clipboard automatically. The remote Explorer fetches the contents while it pastes and shows its usual copy progress (no waiting beforehand, large files stream directly).
- **Drag & drop:** drag local files onto a remote window. While you hover, a blue frame shows "Loslassen = an dieser Stelle auf dem Remote-PC einfügen" (release = paste here on the remote PC). RDP has no drop channel either, so the app emulates the drop: it offers the files, clicks at the drop point on the remote side and presses `Ctrl+V` there.
  ✔ Works on the remote desktop and in the empty area of an Explorer window.
  ⚠ If you drop onto a folder **icon**, the files land in the currently open folder, not in the folder you dropped on (like Ctrl+V after clicking the folder).

Only the files you copied or dropped can be read by the remote PC, nothing else. They stay available until you copy something else locally or disconnect.

### Turning the remote PC on (Wake-on-LAN)
In the connection dialog, under **"PC aus der Ferne einschalten"** (turn on the PC remotely):
- **Über die FRITZ!Box** (via the FRITZ!Box, works from anywhere): enter the FRITZ!Box address (at home `fritz.box`; away from home your MyFRITZ! address with port, e.g. `abc123.myfritz.net:44312`), a FRITZ!Box user and password, click **"Geräte laden"** (load devices) and pick the remote PC from the list. The MAC address is filled in automatically. The FRITZ!Box password is stored in the Windows Credential Manager.
- **Magic Packet:** for other routers; only works when your PC is in the same network as the remote PC.

When disconnected, **"⏻ PC aufwecken & verbinden"** (wake PC & connect) sends the wake signal, waits until the PC accepts connections (up to 4 minutes), then connects automatically.

Requirements on the remote PC (one-time setup):
1. BIOS/UEFI: enable Wake-on-LAN (often "Wake on LAN", "Power On By PCI-E" or "ErP" off).
2. Device Manager → network adapter → *Power Management*: "Allow this device to wake the computer"; *Advanced*: "Wake on Magic Packet" = Enabled.
3. Turn off Windows **Fast Startup** (Control Panel → Power Options → "Choose what the power buttons do"), or the PC usually can't be woken from "Shut down".
4. Connect the PC **by cable**; Wake-on-LAN over WiFi almost never works.

FRITZ!Box: *Home Network → Network → Network Settings → "Allow access for applications"* (TR-064) must be on (the default). The FRITZ!Box user needs the right "FRITZ!Box Einstellungen" (FRITZ!Box settings), and for waking from outside also "Zugang auch aus dem Internet erlaubt" (access from the internet allowed) plus enabled MyFRITZ!/internet access to the FRITZ!Box.

### Updates
**⟳** in the toolbar checks GitHub for a new version. If there is one, the app downloads it, checks the SHA-256 checksum, replaces its files and restarts. Settings and saved passwords stay. At startup the app checks silently and only shows a notice. The app folder must be writable (e.g. not under `C:\Program Files`).

### Following the active window (optional helper on the remote PC)
When you click a program in the remote taskbar (or Alt+Tab to it), the main window switches to the monitor that program is on. If a separate monitor window already shows that monitor, nothing changes. Switch it off in **⚙ → Monitor automatisch wechseln**.

RDP doesn't tell the client where windows are, so this needs a small helper on the remote PC:
1. Copy the app to the remote PC, start it there once, open **⚙** and click **"Auf diesem PC einrichten"**.
2. That's it. The helper is the same exe started with `--agent`. It has no window, needs no admin rights, and starts with Windows (autostart entry for the current user). It listens for "window came to the front" events, so it uses no CPU while idle (about 50 MB RAM). It sends only the centre of the active window's monitor, over a dynamic virtual channel inside the existing RDP connection (`RdpMonitorSwitcher.Focus`), so there is no extra port and no firewall rule.
3. With mstsc or other RDP clients the helper just stays idle. Remove it with the same button. Updates via ⟳ on the remote PC restart it automatically.

### Other features
- **Anpassen / 1:1** (Fit / 1:1): scaled to the window, or original pixels with scrollbars.
- **＋ / －**: add/remove a remote monitor while the session runs.
- Clipboard (text) in both directions, remote sound, remote mouse pointer shape, per-monitor DPI scaling (`Remote-Skalierung`).

## 5. Building

Build machine requirements: **.NET 10 SDK**, **Visual Studio 2022/2026 with "Desktop development with C++"** (incl. CMake), **vcpkg** (`git clone https://github.com/microsoft/vcpkg %USERPROFILE%\vcpkg` + `bootstrap-vcpkg.bat`, or set `VCPKG_ROOT`).

```bat
build.cmd
```
does everything: builds FreeRDP + `RdpBridge.dll` via vcpkg (~10 min the first time), runs the tests, and publishes a self-contained build to `dist\RdpMonitorSwitcher\`.

Publishing a release (after `build.cmd`): `release.cmd "release notes"` creates the GitHub release with the ZIP and checksum (needs the GitHub CLI, logged in). Bump `<Version>` in `src\RdpMonitorSwitcher\RdpMonitorSwitcher.csproj` first; `<UpdateRepository>` there holds `owner/name` of the GitHub repository.

Individual steps:
```bat
native\RdpBridge\build.cmd                          :: native bridge only
dotnet build RdpMonitorSwitcher.sln                 :: app (Visual Studio: open RdpMonitorSwitcher.sln)
dotnet test tests\RdpMonitorSwitcher.Tests          :: unit tests
dotnet run --project tests\RdpBridge.SmokeTest -- <host>   :: native smoke test (see 7.2)
```
`RdpBridge.dll` is linked statically (FreeRDP, OpenSSL, zlib, cJSON, static CRT) and depends only on Windows system DLLs.

## 6. Architecture

```
native/RdpBridge/            C bridge over FreeRDP 3 (CMake + vcpkg manifest, custom triplet)
  include/rdpbridge.h        stable C API (~15 functions), documented threading model
  src/rdpbridge.c            session, FreeRDP event loop, frame copy, input queue, Display Control
  src/rb_pointer.c           remote pointer → HCURSOR
  src/rb_clipboard.c         text clipboard (cliprdr)
src/RdpMonitorSwitcher.Core/ no WPF, fully testable
  Models/                    MonitorLayout, ConnectionProfile, geometry, states
  Rdp/                       IRdpSession (abstraction of the connection)
  Services/                  RemoteDesktopController (connection, reconnect, live layout),
                             MonitorNavigator, ViewportMapper, KeyTranslator, ShortcutMap, ReconnectPolicy
  Configuration/             SettingsStore (JSON), ICredentialStore
  ViewModels/                MainViewModel, MonitorViewModel (MVVM, CommunityToolkit.Mvvm)
src/RdpMonitorSwitcher/      WPF app (.NET 10)
  Rdp/                       FreeRdpSession: P/Invoke to RdpBridge.dll
  Views/                     MainWindow, MonitorWindow, RemoteViewport, dialogs
  Services/                  FrameRenderer, InputRouter (keyboard hook), ClipboardSync, cursor, dialogs, windows
  Infrastructure/            Win32: Credential Manager, low-level keyboard hook
tests/RdpMonitorSwitcher.Tests/   xUnit, 213 tests (no RDP connection needed)
tests/RdpBridge.SmokeTest/        tests the native C API against a real RDP server
```

**How a frame reaches the screen:** FreeRDP decodes (GFX: RemoteFX/Progressive/H.264) → `EndPaint` copies the changed rectangles into a separate frame buffer (under a lock) → one coalesced notification to the UI thread → `FrameRenderer` locks once and every `RemoteViewport` copies **only the changed part of its monitor** into its `WriteableBitmap`.

**Threading:** all calls into the bridge only queue a command; the FreeRDP thread executes it (input, layout, clipboard). The UI never waits on the network. Closing a session (thread join) runs in the background.

### Security
- Passwords go only into the **Windows Credential Manager** (`RdpMonitorSwitcher:<host>:<port>|<user>`, encrypted by Windows), never into `settings.json` or logs. Optional ("Passwort speichern").
- The password is passed to the bridge as a UTF-8 copy that is **zeroed** right after the call.
- Certificates: unknown or changed certificates are only accepted after you confirm in the dialog.
- Logs: `%LOCALAPPDATA%\RdpMonitorSwitcher\logs\` (app + FreeRDP), without credentials.
- Settings: `%APPDATA%\RdpMonitorSwitcher\settings.json` (host, user, monitors, window positions).

## 7. Tests

### 7.1 Automated (no RDP needed)
`dotnet test tests\RdpMonitorSwitcher.Tests`: **213 tests** for Wake-on-LAN (MAC parsing, magic packet, FRITZ!Box SOAP requests incl. remote address, error translation, device list, waiting for the PC, wake & connect), updates (release parsing, version comparison, **checksum check**),  file transfer local → remote (file lists incl. folders, reading ranges, only offered files readable, name/length limits, drop = offer → click → Ctrl+V, rejection/timeout) and remote → local (Ctrl+C on the remote side, chunked download, folders, empty files, timeouts, aborts, read errors, **path traversal protection**), the drag-out zone (delay, minimum distance, edges, only once per drag), monitor layout (gaps, primary at origin, protocol limits), monitor selection/switching incl. wrap-around and error messages, shortcuts, scancode translation (NumLock/Pause/Right Shift quirks), mouse/wheel coding, coordinate mapping, connection status, automatic reconnect (back-off, no retry after login errors/logoff/takeover, cancel), live layout changes (debounce, confirmation, fallback reconnect), multi-window mode (windows per monitor, size per window, closing), configuration (round trip, corrupt file, **no password in the file**), Credential Manager usage.

### 7.2 Native smoke test
`dotnet run --project tests\RdpBridge.SmokeTest -- <host>` connects **without credentials** (so no login attempt) and checks: DLL loads, FreeRDP thread, TLS, certificate callback, error classification. Result on the build PC: `SMOKE TEST PASSED`.

### 7.3 Manual test with a real remote PC (2–3 monitors)
Do this from your local PC, not from inside an RDP session on the target PC (Windows Pro allows only one session per user):

1. Prepare the remote PC (section 3), and note its IP / Tailscale name.
2. Start the app → **Verbinden** → host, user, password, *Fit to window*, count **3** → connect.
   ✔ Certificate dialog appears (first time) → "Immer vertrauen".
   ✔ Status turns green "Verbunden", toolbar shows Monitor 1/2/3, monitor 1 fills the window with no black bars.
3. On the remote PC: *Settings → System → Display* shows **3 monitors**. Drag a window to monitor 2.
4. Click **Monitor 2** → the window you just moved is visible immediately. Check: the log at `%LOCALAPPDATA%\RdpMonitorSwitcher\logs\freerdp*.log` has **no** new connection entry, and the remote session is unchanged.
5. `Ctrl+Alt+→`, `Ctrl+Alt+1`, `Ctrl+Alt+3`: switching works; `Ctrl+Alt+5` → error message "Monitor 5 ist nicht verfügbar…".
6. Resize the window → after ~0.5 s the remote monitors match the new size (log: "monitor layout … sent").
7. **＋** → a 4th monitor appears in the toolbar and in the remote display settings, with no reconnect. **－** removes it again.
8. **⧉ Fenster je Monitor** → monitors 2 and 3 open in their own windows. Move them to local monitors, maximize them, drag a remote window from window 1 to window 2.
9. Keyboard: Windows key opens the remote Start menu; Alt+Tab switches remotely; while holding Shift, switch monitors with `Ctrl+Alt+→` → no stuck key afterwards.
10. Copy text locally → paste remotely (and the other way round).
11. Pull the network cable or stop Tailscale briefly → "Verbindung unterbrochen – neuer Versuch in … s", then you're back in the same session automatically.
12. Drag out: drag a file in the remote Explorer to the right edge → the blue zone appears, a progress window, then drop the file onto the local desktop. Also: select a file, `Ctrl+Alt+D`, then `Ctrl+V` in the local Explorer.
13. Local → remote: copy a local folder with `Ctrl+C`, `Ctrl+V` in the remote Explorer → the remote copy dialog appears and the folder arrives completely. Then drag a local file onto the remote desktop → it appears where you dropped it.
14. **Trennen** (Disconnect) → no automatic reconnect.

## 8. Known limitations

- **Virtual monitors, not physical screens:** you see the RDP session with the monitors the app declares, not the remote PC's physical screens or what is open on them in the console session. (RDP's only way to do that, `mstsc /shadow`, needs group policies and can't be embedded.) In "fixed resolutions" mode you can match the layout to the real monitors.
- Monitors are arranged **side by side in a row** (monitor 1 = primary, on the left). Stacked or offset arrangements aren't configurable yet.
- All declared monitors are transferred, including those not currently shown. RDP only sends changed areas, so this is usually unproblematic, but it uses more bandwidth than a single monitor.
- Clipboard: text and files in **both directions** (remote → local via drag out / Ctrl+Alt+D, local → remote via Ctrl+C/Ctrl+V or drag & drop). No images, no drive, printer or USB redirection, no microphone.
- Dropping onto the remote PC is emulated with a click + Ctrl+V at the drop point. Dropping onto a folder icon pastes into the current folder; dropping onto an app that cannot paste files does nothing.
- File names (relative path incl. folders) may be at most 259 characters (RDP limit); files over 2 GB need a remote PC with "huge file support" (Windows 8 / Server 2012 or newer).
- Drag out: files are fetched completely **before** the local drag continues. For large files, keep the button held until the progress window disappears. Only data the remote Explorer puts on the clipboard can be fetched (files/folders, no virtual items such as phones).
- Remote pointer: shown in the remote pointer's size; when the view is scaled down, it can look larger than the image.
- Keyboard: the Windows key / Alt+Tab always go to the remote PC while the remote screen has focus (like mstsc in fullscreen); there's no setting for this yet. IME/Unicode input (e.g. on-screen keyboard) stays local.
- RD Gateway, smartcard and Azure AD login are not wired into the UI.
- Wake-on-LAN via the FRITZ!Box from outside was verified against the FRITZ!Box interface description, but not end to end (remote access path `/tr064` per community documentation). The FRITZ!Box only wakes devices it already knows.
- Updates are only as trustworthy as the GitHub repository: the checksum detects broken downloads, but there is no code signing.
- FreeRDP prints a warning in its log for the Media Foundation H.264 decoder ("experimental"). If you see image errors with H.264, turn off "H.264" in the connection dialog.
- The end-to-end run with a real login and several monitors (7.3) could **not** be done on the build PC (it is itself being used remotely); what has been verified: build, 123 unit tests, native smoke test against a real RDP server up to authentication, and app startup/UI. The file transfer (drag out) was also only tested with a simulated remote PC.

## 9. Licenses
- FreeRDP: Apache License 2.0
- OpenSSL 3: Apache License 2.0
- zlib: zlib license, cJSON: MIT
- CommunityToolkit.Mvvm: MIT, Serilog: Apache 2.0

When you pass the app on, include the license notices (`native\RdpBridge\vcpkg_installed\x64-windows-static-rdp\share\*\copyright`).
