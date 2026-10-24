# ScopeDeck

[![CI](https://github.com/scopedeck/scopedeck/actions/workflows/ci.yml/badge.svg)](https://github.com/scopedeck/scopedeck/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/scopedeck/scopedeck?include_prereleases)](https://github.com/scopedeck/scopedeck/releases)

A modern replacement for EasyScopeX for Siglent SDS1000X-E oscilloscopes (developed against an
SDS1202X-E, firmware 1.3.27). A .NET 10 backend talks to the scope; a TypeScript UI shows it — in its own
window on Windows, macOS and Linux, or in any browser.

No NI-VISA and no LabVIEW driver are needed: the scope accepts plain SCPI over a TCP socket on port 5025.

**[Download](https://github.com/scopedeck/scopedeck/releases)** · **[Website](https://scopedeck.github.io/scopedeck/)** · **[Roadmap](ROADMAP.md)**

![ScopeDeck with cursors and measurements](docs/images/live-cursors.png)

## Features

- Live waveform view (≈5–20 fps over LAN depending on memory depth) with ground, trigger-position and trigger-level markers
- Controls: Auto / Normal / Single / Stop, auto setup, acquisition mode (normal, peak detect, high-res, average ×4…×1024),
  memory depth, interpolation, time/div, delay, per-channel on/off, V/div, offset, coupling, probe factor, 20 MHz bandwidth limit
- **Triggers**: edge (with holdoff and a 50 % button), pulse width, slope, interval, runt and dropout, with
  shorter/longer/between/outside conditions and the low level for slope and runt triggers
- **Math**: A+B, A−B, A×B, A÷B, d/dt, ∫dt and √ as an extra trace, computed in the app (the scope's own math display
  cannot be switched off again remotely); works with cursors, measurements, FFT and CSV export
- **History**: browse the scope's stored acquisitions frame by frame with timestamps, play them back, export all as CSV
- **Setups**: save the scope setup to a file and load it back, store/recall the scope's 20 setup memories, default setup
- Channel extras: invert, unit V/A (current probes), skew ±100 ns, fine V/div steps (Shift+click ‹ ›). Clicking a
  channel's name presses the scope's channel button: an off channel is switched on and selected, another channel
  is selected (drawn behind the others, as on the scope), and the selected channel is switched off
- **Zoom**: drag across the plot or use the mouse wheel; the zoomed part is read from the scope at up to full
  resolution (`WFSU FP`). Double-click or *Reset zoom* shows the whole record again
- **References**: freeze up to four traces (R1–R4) or load a CSV export, shown as overlays with their own measurements
- **XY mode** (C1 against C2) and **persistence** (0.5 s, 1 s, 5 s or infinite afterglow)
- **Cursors**: draggable time (X1/X2) and voltage (Y1/Y2) cursors with ΔT, 1/ΔT, ΔV and each channel's value at X1/X2
- **FFT**: spectrum of any channel in dBV (RMS), Hann / Hamming / Blackman-Harris / flat-top / rectangular window,
  span zoom, peak marker, hover readout. Resolution follows the *Points* setting (20k points → finer RBW, higher Nyquist)
- **Envelope display**: up to 200k samples per channel are read and drawn as a min/max band, so slow timebases show
  the true signal band instead of aliased sample skipping; app measurements use the dense record
- **Measurements** by the app (every frame: frequency, period, Vpp, max, min, mean, RMS, duty) or by the scope
  itself (`PAVA? ALL`, full memory: adds amplitude, rise/fall time, pulse width, overshoot; refreshed every 2 s).
  *Between cursors X1–X2* limits both to a time window (the scope's measurement gate `MEGS`/`MEGA`/`MEGB`), and the
  delay and phase between channels are shown below the table (app: rising-edge timing; scope: `MEAD? PHA`/`FRR`)
- **Capture memory**: the complete acquisition (up to 14 Mpts per channel) as CSV. A running scope is paused
  for the transfer (~4 s for 7 Mpts) and then resumed
- **Log**: records measurements of every frame; click again to stop and download the CSV
- **Pass/Fail mask test** (the scope's own): create a mask with an X/Y tolerance from a good waveform, then count
  passing and failing waveforms, optionally stopping or beeping on a fail. ScopeDeck draws the mask band on the plot
- **Macros**: record what you change on the scope (settings, front-panel keys, SCPI commands), add pauses, save
  under a name and replay — with a short fixed gap or the recorded timing. Export/import as JSON
- **Front panel**: the scope's live screen with its soft keys, menu, channel, trigger and run buttons and all
  knobs (click to turn or push, or use the mouse wheel), for the scope's own menus that have no remote command
- Scope screenshot (PNG), CSV export of the displayed waveform, PNG export of the plot, SCPI console

## Running

**Prebuilt:** download from [Releases](https://github.com/scopedeck/scopedeck/releases) — the desktop app for Windows
(`…-windows-x64-standalone.zip` needs nothing else installed), macOS (`…-macos-arm64.zip` for Apple silicon,
`…-macos-x64.zip` for Intel) or Linux (`…-linux-x64.tar.gz`, `…-linux-arm64.tar.gz`), or the server + browser UI for
any of them.

**macOS:** unzip and move `ScopeDeck.app` to Applications. The app is not notarized (that needs a paid Apple
developer account), so the first start is refused: open *System Settings → Privacy & Security* and click
*Open Anyway* (or run `xattr -dr com.apple.quarantine /Applications/ScopeDeck.app`). Allow local network access when
asked.

**Linux:** needs WebKitGTK 4.1 (`sudo apt install libwebkit2gtk-4.1-0` on Debian 12 / Ubuntu 22.04 and later,
`webkit2gtk4.1` on Fedora). Unpack and run `ScopeDeck/ScopeDeck`.

On macOS and Linux, exported files (CSV, PNG, setups, macros) go to the Downloads folder; the app shows where.

**Windows desktop app from source:** run `.\publish.ps1`, then start `dist\ScopeDeck.exe`. It needs the .NET 10 runtime
(use `.\publish.ps1 -SelfContained` for a ~100 MB exe that does not).

The last scope address you connected to is remembered in `%LocalAppData%\ScopeDeck\settings.json`
(`~/.local/share/ScopeDeck` on Linux, `~/Library/Application Support/ScopeDeck` on macOS). A fixed default can be set
in `appsettings.json` next to the executable (`Scope:Host`).

**Browser (any OS):**

```powershell
dotnet run --project src/Siglent.Server                      # http://localhost:5180
dotnet run --project src/Siglent.Server -- --simulate true   # with the fake scope on 127.0.0.1
```

Building the server also builds the UI (`web/`, needs Node.js 20+); pass `-p:SkipWebBuild=true` to skip it.
UI development with hot reload: run the server, then `cd web; npm run dev` and open the Vite URL.

**macOS / Linux desktop app from source:** `dotnet run --project src/Siglent.Photino` (it also runs on Windows).
`src/Siglent.Photino/package.sh <rid> <version> <folder>` builds the release packages (the macOS app must be built on
a Mac, where it is signed).

**Server only:** publish it for any platform:

```bash
dotnet publish src/Siglent.Server -c Release -r linux-x64 -p:PublishSingleFile=true -o out   # or osx-arm64, osx-x64, linux-arm64
./out/scopedeck-server     # then open http://localhost:5180
```

**Update check:** on start, ScopeDeck asks GitHub once whether a newer release exists and shows an *Update available*
badge next to the version (pre-release builds are offered newer pre-releases too). Only the request itself is sent.
Switch it off with `"UpdateCheck": { "Enabled": false }` in `appsettings.json`; builds from source (`-dev`) never check.
In the Windows desktop app, clicking the badge offers **Update now**: the matching zip is downloaded, checked against
the SHA-256 checksum GitHub publishes, and installed over the running app (the running exe is renamed to `.old` and
removed on the next start; on any error it is put back). Your `appsettings.json` is kept. The folder must be writable
(not Program Files); otherwise the dialog links to the release page, as it always does on macOS and Linux.

The server listens on localhost only. To use the app from another device, start it with
`--urls http://0.0.0.0:5180` (anyone on your network can then control the scope).

Tests: `dotnet test` (unit tests plus end-to-end tests against the simulator).

## Layout

| Path | Purpose |
| --- | --- |
| `src/Siglent.Scope` | SCPI-over-TCP client (`ScpiConnection`), typed scope API (`SiglentScope`), value parsing, waveform decoding |
| `src/Siglent.Server` | ASP.NET Core minimal API (`/api/...`); the built UI is embedded in the assembly |
| `src/Siglent.Desktop` | Windows app: WinForms window with WebView2, hosting the server in-process on a free port |
| `src/Siglent.Photino` | macOS/Linux app: the same in a [Photino](https://www.tryphotino.io/) window (WebKit) |
| `src/Siglent.Simulator` | `FakeScopeServer`, an SCPI-speaking fake SDS1202X-E |
| `web` | Vite + TypeScript UI (no framework) |
| `tests/Siglent.Scope.Tests` | xUnit tests |

## Protocol notes (verified on the real SDS1202X-E, FW 1.3.27)

- **One client at a time.** The scope accepts a second TCP connection but never answers it. Close EasyScopeX
  (or another ScopeDeck) before connecting.
- `CHDR OFF` is sent on connect so responses are bare values (`2.00E+00` instead of `C1:VDIV 2.00E+00V`).
  This setting persists on the scope until changed or power-cycled.
- `Cn:WF? DAT2` returns `DAT2,#9<9-digit length><bytes>\n\n`. Each byte is a **signed** 8-bit code:
  `V = code × V/div / 25 − offset`. (The guide says "subtract 255"; two's complement is correct.)
- `WFSU SP,n,NP,count,FP,0` returns exactly every n-th sample. With `NP,0` the scope ignores `SP`, so `NP` is always set.
  Decimation is plain sample skipping, so the FFT of decimated data can alias; use more points for spectra.
- **Delay sign:** `TRDL 1MS` makes the scope display "Delay: −1.00ms". The screen center is at −TRDL, so sample i
  is at `−TRDL − 7 × T/div + i × SP / SARA`. The app's *Delay* field uses the value the scope displays.
- `Cn:PAVA? ALL` returns every measurement in one response (~0.5 s); `PAVA? ALL` without a channel never answers.
  Single-parameter queries take ~0.3 s each.
- Transfers are limited by the scope: ~50–150 ms per `WF?` with 7 Mpts memory, ~4 s for the full 7 Mpts.
- **`AVGA?` switches the scope into average mode** (so does `AVGA`). The app never sends it: the count comes from
  `ACQW?` ("AVERAGE,16") and is set with `ACQW AVERAGE,n`.
- History mode (`HSMD ON`) is refused in average mode. `FRAM?` reports the number of frames only right after it is
  switched on; the app otherwise finds it with `FRAM 1000000` (clamped to the last frame). `FTIM?` answers only in
  history mode. Frame count depends on memory depth (≈36 frames at 70K, 1–2 at 7M).
- Peak detect forces 7M memory; high-res allows at most 700K; 7M is refused in average mode.
- `TRSE <type>,SR,<src>` with only the source keeps the existing condition; changing the type needs a full
  `HT`/`HV`/`HV2` set valid for that type.
- **Window and pattern triggers are not in the guide:** `TRSE WIN,SR,C1` and `TRSE PA` work; `TRSE?` then answers only
  `WIN` / `PA` (no source). The window limits are `TRLV` (upper) and `TRLV2` (lower). Pattern states and logic use
  `TRPA C1,H,C2,L,STATE,NOR` (AND/OR/NAND/NOR). `TRWI` can only be set while the window trigger is active.
- **Unanswered queries by trigger type:** `TRSL?` gets no answer in window, pattern and video mode; `TRLV?`, `TRLV2?`
  and `TRCP?` none in pattern and video mode. ScopeDeck only asks what a type supports.
- After a trigger type change, the next `TRSE` within about a second can be ignored: coming back from window mode to
  edge kept the old source. ScopeDeck re-reads `TRSE?` after a type change and resends once if needed.
- `PNSU?` returns the setup as XML (~45 KB) on this 2-channel model (the guide says binary). Loading it back works, but
  two `unit_type` fields of display values came back different.
- `WFSU FP,n` selects the first transferred sample; together with `SP` and `NP` any slice of the record can be read.
- An inverted channel (`INVS ON`) is also transferred inverted. Switching a channel's unit (`UNIT A`) changed the
  trigger level (1.50 → 0.77) and switching back did not restore it. `VDIV` accepts fine values (0.37 → 0.368).
- The math display can be switched on (`MATH:TRA ON`) but not off again (`MATH:TRA OFF` is ignored, `MATH:TRA?` gets no
  answer), which is why ScopeDeck computes math itself.
- `SCDP` returns an 800×480 RGB565 BMP (top-down, BI_BITFIELDS) followed by one extra `\n` byte.
- **Measurement gate:** `MEGS ON`, `MEGA <t>`, `MEGB <t>` (times relative to the trigger) limit `PAVA?` to a window;
  `MEGS?` never answers and `MEGA`/`MEGB` are write-only, so ScopeDeck remembers the gate and switches it off on
  disconnect. `C1-C2:MEAD? PHA` / `FRR` take ~0.2 s each and answer `****` when a channel has no signal or (PHA)
  less than a full period is on screen; the app's own delay needs only one rising edge per channel. Both use each
  channel's mid level, so a clipped trace (partly off screen) shifts the result on slow edges.
- **Front-panel keys** (not in the guide): `$$SY_FP <key>,1` presses a key, `$$SY_FP <knob>,±1` turns a knob one
  detent (+1 = clockwise) and `$$SY_FP <knob>,0` pushes it. Key codes are from klumw/sdsremote. Pressing Math twice
  switches the scope's math display off again, which `MATH:TRA OFF` cannot. Default setup (13) and Print (25) are
  not offered.
- **Pass/Fail** (FW 1.3.27 differs from the guide): `PFDS TEST,ON,DISPLAY,ON` is ignored; the test is enabled with the
  undocumented `PFEN ON`/`OFF` (`PFEN?` answers), and `PFDS ON`/`OFF` / `PFDS?` are only the on-screen message.
  `PFCM` and `PFOP` work only while the test is enabled; `PFOP ON` resets the counters. The scope does not draw
  the mask, so ScopeDeck draws its own from the same waveform. Input that arrives while the scope is still taking a
  pass/fail command is lost (`*OPC?` right after `PFST` went unanswered); ScopeDeck waits 50 ms after each one.
  `PFST` is ignored while the test is off. Before a test has run, `PFDD?` can report leftovers like
  `FAIL,865,PASS,-865,TOTAL,0`.
- **Dropped input:** the scope loses or garbles input that arrives while it is still taking a setting. `C2:VDIV 10V`
  followed at once by `*OPC?` became 5 mV/div; after a V/div change the next command was lost with up to 50 ms in
  between (the input relays switch), never with 100 ms. ScopeDeck waits 20 ms after each setting and 150 ms after
  V/div, offset, coupling, probe, channel on/off and bandwidth limit, and sends a text query once more when it gets
  no answer within 1.5 s.
- After the scope has **stopped on a failed waveform**, the next `TRMD AUTO` switches Average to Normal (after a plain
  Stop it does not); ScopeDeck puts the acquisition mode back when you resume.
- **Serial triggers do not work remotely on FW 1.3.27.** `TRSE SERIAL` selects the serial trigger, but none of the
  guide's `TRIIC:*`, `TRSPI:*`, `TRUART:*`, `TRCAN:*` or `TRLIN:*` queries is answered and the commands are ignored
  (checked on the scope's menus). The only effect found: `TRIIC <anything>,<source>,<threshold>` sets the I²C SCL
  source and threshold. Serial triggers have to be set on the scope (or with the virtual front panel).

## Related projects

- [klumw/sdsremote](https://github.com/klumw/sdsremote) — a Flutter app for the same scopes (VXI-11/USB) with a
  virtual front panel, macro recorder, data logger with PDF reports and an AI chat assistant.

ScopeDeck was published for one day as "SDS Remote" and renamed to avoid confusion with the project above.

## License

MIT — see [LICENSE](LICENSE). Not affiliated with or endorsed by Siglent Technologies; Siglent and EasyScopeX are trademarks of their respective owners.
