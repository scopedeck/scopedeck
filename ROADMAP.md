# Roadmap

Scope features that the SDS1000X-E remote command set supports (programming guide PG01-E02C) and that
SDS Remote does not use yet. Options that a 2-channel SDS1202X-E does not have (digital/MSO channels) are left out.

## Next release (RC 2)

1. **Acquisition settings** — mode Normal / Peak detect / Average (with count) / High-res (`ACQW`, `AVGA`),
   memory depth 14k…14M (`MSIZ`), 20 MHz bandwidth limit per channel (`BWL`), interpolation sin(x)/x vs linear (`SXSA`).
2. **More trigger types** — pulse width, slope, interval, runt, dropout, window, video (`TRSE` with its
   pair parameters), holdoff, second level (`TRLV2`), and a "50 %" level button (`SET50`).
3. **Math channel** — read and draw `MATH:WF?` (A+B, A−B, A×B, A/B, d/dt, ∫dt, √) as a third trace with its own
   scale (`MTVD`, `MTVP`, `DEF`).
4. **History mode** — browse the stored acquisitions frame by frame with timestamps and export them all
   (`HSMD`, `FRAM`, `FTIM?`). This is the scope's own "long recording" feature.
5. **Setups on the PC** — save/restore the complete panel setup as a file (`PNSU`), plus the internal memories
   (`*SAV`, `*RCL`).

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
