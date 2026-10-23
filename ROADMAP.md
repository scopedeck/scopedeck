# Roadmap

Scope features that the SDS1000X-E remote command set supports (programming guide PG01-E02C) and that
ScopeDeck does not use yet. Options that a 2-channel SDS1202X-E does not have (digital/MSO channels) are left out.

## Done in RC 2

- **Acquisition settings** — mode Normal / Peak detect / Average ×4…×1024 / High-res, memory depth, 20 MHz bandwidth
  limit per channel, interpolation (`ACQW`, `MSIZ`, `BWL`, `SXSA`; `AVGA?` is avoided, see README).
- **More trigger types** — pulse width, slope, interval, runt, dropout with conditions, edge holdoff, low level
  (`TRLV2`), "50 %" button (`SET50`). The trigger source can be changed without resetting the type.
- **Math** — computed in the app (A+B, A−B, A×B, A÷B, d/dt, ∫dt, √) instead of the scope's `MATH:WF?`, because the
  scope's math display cannot be switched off remotely.
- **History mode** — browse, play back and export the stored acquisitions (`HSMD`, `FRAM`, `FTIM?`).
- **Setups** — save/load setup files (`PNSU`), scope memories 1–20 (`*SAV`, `*RCL`), default setup (`*RCL 0`).

## Next

- **Video trigger** (`TRSE TV` with standard, sync, line, field) — currently shown but has to be set up on the scope.
- **Window trigger** (`TRWI`) and pattern trigger.

## Later

- **Channel extras** — invert (`INVS`), unit V/A for current probes (`UNIT`), skew (`SKEW`), fine V/div steps.
- **Reference waveforms** — on the scope (`REFS`, `REFSA`, `REFDS`) and/or app-side overlays from saved CSVs.
- **Pass/Fail mask testing** — create a mask, count pass/fail, stop on fail (`PF*`).
- **Serial triggers** — I²C, SPI, UART, CAN, LIN with address/ID/data qualifiers (`TRIIC:*`, `TRSPI:*`,
  `TRUART:*`, `TRCAN:*`, `TRLIN:*`).
- **Measurement gates and inter-channel delay** (`MEGA`, `MEGB`, `MEGS`, `MEAD`) for the "Scope" measurement source.
- **Display** — persistence and XY mode drawn in the app (scope-side: `PESU`, `XYDS`).
- **Native desktop windows on macOS and Linux** — swap WinForms/WebView2 for Photino.NET (WebKit on macOS/Linux).

## Low priority / limited

- **Serial decode** — can be configured remotely (`DCST`, `B<n>:DC*`), but there is no command to read decoded
  results; they are only visible on the scope screen (screenshot). App-side decoding would be a separate project.
- **Built-in counter** (`CYMT?`, `COUN`) — measurements already cover frequency.
- **Scope-side FFT and cursors** (`FFT*`, `CRST`, `CRVA?`) — the app has its own, more flexible versions.
- **Zoom window** (`HMAG`, `HPOS`) — better done app-side on the transferred data.
- **Function generator** (`WGEN`, `ARWV`) — only with the SAG1021 USB module.
- **Housekeeping** — date/time, buzzer, network settings, self-calibration (`DATE`, `BUZZ`, `CONET`, `*CAL?`).
