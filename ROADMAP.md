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

## Done in RC 3

- **Channel extras** — invert (`INVS`), unit V/A (`UNIT`), skew (`SKEW`), fine V/div steps (Shift+click).
- **Zoom** — drag or mouse wheel on the plot; the visible part is read at up to full resolution (`WFSU FP`).
- **References** — R1–R4 frozen from a trace or loaded from a CSV export (app-side).
- **XY mode and persistence** — drawn in the app.

## Done in RC 5

- **Video trigger** — standard, any line or a chosen line and field (`TRSE TV`).
- **Window trigger** — upper/lower limits (`TRSE WIN`, `TRLV`, `TRLV2`; not in the guide).
- **Pattern trigger** — high/low/don't care per channel, AND/OR/NAND/NOR (`TRSE PA`, `TRPA`; thresholds stay on the scope).

## Done in RC 6

- **Envelope display** — up to 200k samples per channel are read and reduced to min/max per point, so slow timebases
  show the true signal band; app measurements are made on the dense record.
- **Measurement gate and inter-channel delay** — measurements between cursors X1–X2 (app-side, and the scope's
  `MEGS`/`MEGA`/`MEGB`), delay and phase between channels (rising edges; `MEAD? PHA`/`FRR` for the scope source).

## Done in RC 7

- **Active channel** — clicking a channel's name presses the scope's channel button (select, then off).
- **Virtual front panel** — the scope's screen with soft keys, buttons and knobs (`$$SY_FP`, not in the guide).

## Done in RC 8

- **Channel buttons** — the channel-name links press the scope's own CH1/CH2 key; the selected channel is drawn behind the others, as on the scope.

## Done in RC 9

- **Pass/Fail mask testing** — create a mask from a good waveform, count pass/fail, stop or beep on fail (`PF*`,
  `PFEN` not in the guide); the mask band is drawn on the plot.

## Done in RC 10

- **Macro recorder** — record settings, front-panel keys and SCPI commands with pauses; replay, save, export/import.
- **Reliable commands** — pauses after settings (longer after relay switching) and resending dropped queries.

## Done in RC 11

- **Desktop app for macOS and Linux** — the UI in a native window (Photino.NET with the system's WebKit), as an
  ad-hoc signed `.app` for macOS (Apple silicon and Intel) and a folder for Linux (x64, arm64); exports go to Downloads.

## Low priority / limited

- **Serial triggers** (`TRSE SERIAL`, `TRIIC:*`, `TRSPI:*`, `TRUART:*`, `TRCAN:*`, `TRLIN:*`) — FW 1.3.27 ignores the
  documented commands and answers none of the queries (see README); they can only be set with the front panel.

- **Serial decode** — can be configured remotely (`DCST`, `B<n>:DC*`), but there is no command to read decoded
  results; they are only visible on the scope screen (screenshot). App-side decoding would be a separate project.
- **Built-in counter** (`CYMT?`, `COUN`) — measurements already cover frequency.
- **Scope-side FFT and cursors** (`FFT*`, `CRST`, `CRVA?`) — the app has its own, more flexible versions.
- **Scope-side zoom window** (`HMAG`, `HPOS`) — ScopeDeck zooms itself, at full resolution.
- **Function generator** (`WGEN`, `ARWV`) — only with the SAG1021 USB module.
- **Housekeeping** — date/time, buzzer, network settings, self-calibration (`DATE`, `BUZZ`, `CONET`, `*CAL?`).
