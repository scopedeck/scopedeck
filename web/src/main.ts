import "./style.css";
import { api, ApiError, type ChannelSettings, type ScopeSettings, type WaveformFrame } from "./api";
import { series125, si } from "./format";
import { spectrum, WINDOWS, type Spectrum, type WindowName } from "./fft";
import { measure } from "./measure";
import { CHANNEL_COLORS, ScopePlot, type CursorHit, type CursorMode } from "./plot";
import { SpectrumPlot } from "./spectrum";

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;

const TIME_DIVS = series125(1e-9, 100);
const VOLT_DIVS = series125(500e-6, 10);
const PROBES = [0.1, 0.2, 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000];
const SETTINGS_REFRESH_MS = 1500;
const SCOPE_MEASURE_REFRESH_MS = 2000;
const HOST_KEY = "sds-remote.host";

type MeasureSource = "app" | "scope";

const state = {
  connected: false,
  live: true,
  channelCount: 2,
  settings: null as ScopeSettings | null,
  frame: null as WaveformFrame | null,
  settingsAt: 0,
  loopRunning: false,
  measureSource: "app" as MeasureSource,
  scopeMeasurements: new Map<number, Record<string, number | null>>(),
  scopeMeasurementsAt: 0,
  fftOn: false,
  spectrum: null as Spectrum | null,
  /** Measurement log rows while logging, otherwise null. */
  log: null as string[] | null,
};

const plot = new ScopePlot($<HTMLCanvasElement>("plot"));
const spectrumPlot = new SpectrumPlot($<HTMLCanvasElement>("spectrum"));

// ---------- helpers ----------

let toastTimer = 0;
function toast(message: string, kind: "error" | "info" = "error"): void {
  const el = $("toast");
  el.textContent = message;
  el.className = `toast ${kind}`;
  el.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = window.setTimeout(() => (el.hidden = true), 4000);
}

/** Runs a scope command, then refreshes settings; reports failures. */
async function act(fn: () => Promise<unknown>): Promise<void> {
  try {
    await fn();
    await refreshSettings();
  } catch (e) {
    handleError(e);
  }
}

function handleError(e: unknown): void {
  const message = e instanceof Error ? e.message : String(e);
  toast(message);
  if (e instanceof ApiError && (e.status === 409 || e.status === 503)) setConnected(false);
}

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

function fillSelect(select: HTMLSelectElement, values: number[], format: (v: number) => string, current?: number): void {
  const list = current !== undefined && !values.some((v) => near(v, current)) ? [...values, current].sort((a, b) => a - b) : values;
  const key = list.join();
  if (select.dataset.key !== key) {
    select.replaceChildren(...list.map((v) => new Option(format(v), String(v))));
    select.dataset.key = key;
  }
  if (current !== undefined && document.activeElement !== select) {
    const match = list.find((v) => near(v, current));
    if (match !== undefined) select.value = String(match);
  }
}

const near = (a: number, b: number) => Math.abs(a - b) <= Math.abs(b) * 1e-3;

function setInput(input: HTMLInputElement, value: number | null | undefined): void {
  if (document.activeElement === input) return;
  input.value = value == null ? "" : String(Number(value.toPrecision(4)));
}

// ---------- connection ----------

function setConnected(connected: boolean, label?: string): void {
  state.connected = connected;
  document.body.classList.toggle("connected", connected);
  $("status").classList.toggle("ok", connected);
  $("status-text").textContent = connected ? label ?? "Connected" : "Disconnected";
  $("connect-btn").textContent = connected ? "Disconnect" : "Connect";
  $("placeholder").hidden = connected;
  if (!connected) {
    state.frame = null;
    state.settings = null;
    render();
  } else {
    startLoop();
  }
}

$("connect-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  if (state.connected) {
    await api.disconnect().catch(handleError);
    setConnected(false);
    return;
  }
  const host = $<HTMLInputElement>("host").value.trim();
  if (!host) return toast("Enter the oscilloscope's IP address.");
  const btn = $<HTMLButtonElement>("connect-btn");
  btn.disabled = true;
  btn.textContent = "Connecting…";
  try {
    const id = await api.connect(host);
    try {
      localStorage.setItem(HOST_KEY, host);
    } catch {
      /* storage unavailable */
    }
    await onConnected(id.model, id.firmware);
  } catch (err) {
    setConnected(false);
    handleError(err);
  } finally {
    btn.disabled = false;
  }
});

async function onConnected(model: string, firmware: string): Promise<void> {
  const status = await api.status();
  state.channelCount = status.channelCount ?? 2;
  buildChannelPanels();
  setConnected(true, `${model} · FW ${firmware}`);
  await refreshSettings();
}

// ---------- live loop ----------

async function startLoop(): Promise<void> {
  if (state.loopRunning) return;
  state.loopRunning = true;
  let frames = 0;
  let fpsAt = performance.now();
  try {
    while (state.connected) {
      if (!state.live) {
        await sleep(200);
        continue;
      }
      try {
        if (performance.now() - state.settingsAt > SETTINGS_REFRESH_MS) await refreshSettings();
        state.frame = await api.frame(Number($<HTMLSelectElement>("points").value));
        if (state.log) appendLogRow(state.frame);
        if (state.measureSource === "scope" && performance.now() - state.scopeMeasurementsAt > SCOPE_MEASURE_REFRESH_MS) {
          await refreshScopeMeasurements();
        }
        render();
        frames++;
        const now = performance.now();
        if (now - fpsAt > 1000) {
          $("fps").textContent = `${((frames * 1000) / (now - fpsAt)).toFixed(1)} fps`;
          frames = 0;
          fpsAt = now;
        }
      } catch (e) {
        handleError(e);
        await sleep(1000);
      }
      // A timer instead of requestAnimationFrame: rAF is throttled to ~1 Hz in background windows.
      await sleep(15);
    }
  } finally {
    state.loopRunning = false;
    $("fps").textContent = "";
  }
}

async function refreshSettings(): Promise<void> {
  if (!state.connected) return;
  state.settings = await api.settings();
  state.settingsAt = performance.now();
  renderSettings();
}

// ---------- rendering ----------

function render(): void {
  plot.draw(state.frame, state.settings);
  renderReadout();
  renderCursorInfo();
  renderMeasurements();
  renderSpectrum();
  renderInfo();
}

function renderInfo(): void {
  const f = state.frame;
  const s = state.settings;
  $("acq-status").textContent = s ? s.status : "—";
  $("acq-status").dataset.status = s?.status.toLowerCase() ?? "";
  if (!f) {
    $("info-line").textContent = "";
    return;
  }
  const parts = [`${si(f.timePerDiv, "s")}/div`, `Delay ${si(f.triggerDelay, "s")}`, `${si(f.sampleRate, "Sa/s")}`];
  const mem = f.channels[0]?.memoryPoints;
  if (mem) parts.push(`${si(mem, "pts")}`);
  $("info-line").textContent = parts.join("  ·  ");
}

function renderReadout(): void {
  const el = $("readout");
  const r = state.frame && plot.readout();
  if (!r || dragging) {
    el.hidden = true;
    return;
  }
  el.hidden = false;
  el.replaceChildren(
    line("t", r.time, "#c9d1d9"),
    ...r.values.map((v) => line(`C${v.channel}`, v.text, CHANNEL_COLORS[v.channel - 1])),
  );
  function line(label: string, value: string, color: string) {
    const div = document.createElement("div");
    div.innerHTML = `<b style="color:${color}">${label}</b> <span>${value}</span>`;
    return div;
  }
}

const APP_COLUMNS: [string, (m: ReturnType<typeof measure>) => string][] = [
  ["Freq", (m) => si(m.frequency, "Hz", 5)],
  ["Period", (m) => si(1 / m.frequency, "s", 4)],
  ["Vpp", (m) => si(m.pkpk, "V")],
  ["Max", (m) => si(m.max, "V")],
  ["Min", (m) => si(m.min, "V")],
  ["Mean", (m) => si(m.mean, "V")],
  ["RMS", (m) => si(m.rms, "V")],
  ["Duty", (m) => (Number.isFinite(m.duty) ? `${(m.duty * 100).toFixed(1)} %` : "—")],
];

/** PAVA parameter, column title, formatter. */
const SCOPE_COLUMNS: [string, string, (v: number) => string][] = [
  ["FREQ", "Freq", (v) => si(v, "Hz", 5)],
  ["PER", "Period", (v) => si(v, "s", 4)],
  ["PKPK", "Vpp", (v) => si(v, "V")],
  ["AMPL", "Ampl", (v) => si(v, "V")],
  ["MAX", "Max", (v) => si(v, "V")],
  ["MIN", "Min", (v) => si(v, "V")],
  ["MEAN", "Mean", (v) => si(v, "V")],
  ["RMS", "RMS", (v) => si(v, "V")],
  ["RISE", "Rise", (v) => si(v, "s")],
  ["FALL", "Fall", (v) => si(v, "s")],
  ["PWID", "+Width", (v) => si(v, "s")],
  ["DUTY", "Duty", (v) => `${v.toFixed(1)} %`],
  ["OVSP", "Overshoot", (v) => `${v.toFixed(2)} %`],
];

function renderMeasurements(): void {
  const el = $("measurements");
  const f = state.frame;
  if (!f || f.channels.length === 0) {
    el.replaceChildren();
    return;
  }
  const scope = state.measureSource === "scope";
  const titles = scope ? SCOPE_COLUMNS.map(([, t]) => t) : APP_COLUMNS.map(([t]) => t);
  const table = document.createElement("table");
  table.innerHTML = `<thead><tr><th></th>${titles.map((h) => `<th>${h}</th>`).join("")}</tr></thead>`;
  const body = table.createTBody();
  for (const wf of f.channels) {
    let cells: string[];
    if (scope) {
      const m = state.scopeMeasurements.get(wf.channel);
      cells = SCOPE_COLUMNS.map(([key, , fmt]) => (m?.[key] == null ? "—" : fmt(m[key]!)));
    } else {
      const m = measure(wf);
      cells = APP_COLUMNS.map(([, fn]) => fn(m));
    }
    const row = body.insertRow();
    row.innerHTML = `<th style="color:${CHANNEL_COLORS[wf.channel - 1]}">C${wf.channel}</th>${cells.map((c) => `<td>${c}</td>`).join("")}`;
  }
  el.replaceChildren(table);
}

async function refreshScopeMeasurements(): Promise<void> {
  state.scopeMeasurementsAt = performance.now();
  for (const wf of state.frame?.channels ?? []) {
    state.scopeMeasurements.set(wf.channel, await api.measurements(wf.channel));
  }
}

document.querySelectorAll<HTMLButtonElement>("#measure-source button").forEach((b) =>
  b.addEventListener("click", async () => {
    state.measureSource = b.dataset.source as MeasureSource;
    document.querySelectorAll("#measure-source button").forEach((x) => x.classList.toggle("active", x === b));
    if (state.measureSource === "scope") {
      state.scopeMeasurements.clear();
      state.scopeMeasurementsAt = 0;
      if (!state.live && state.connected) await refreshScopeMeasurements().catch(handleError);
    }
    render();
  }),
);

// ---------- cursors ----------

function renderCursorInfo(): void {
  const el = $("cursor-info");
  const { mode, t, v, ref } = plot.cursors;
  const f = state.frame;
  if (mode === "off" || !f) {
    el.hidden = true;
    return;
  }
  const lines: string[] = [];
  const row = (label: string, value: string, color = "#c9d1d9") =>
    `<div><b style="color:${color}">${label}</b> <span>${value}</span></div>`;
  if (mode === "time" || mode === "both") {
    const dt = t[1] - t[0];
    lines.push(row("X1", si(t[0], "s", 4), "#7fdcff"), row("X2", si(t[1], "s", 4), "#7fdcff"));
    lines.push(row("ΔT", si(dt, "s", 4)), row("1/ΔT", si(1 / Math.abs(dt), "Hz", 4)));
    for (const wf of f.channels) {
      const a = plot.valueAt(wf.channel, t[0]);
      const b = plot.valueAt(wf.channel, t[1]);
      const fmt = (x: number | undefined) => (x === undefined ? "—" : si(x, "V", 4));
      const d = a === undefined || b === undefined ? "—" : si(b - a, "V", 4);
      lines.push(row(`C${wf.channel}`, `${fmt(a)} → ${fmt(b)}  Δ ${d}`, CHANNEL_COLORS[wf.channel - 1]));
    }
  }
  if (mode === "volt" || mode === "both") {
    const color = CHANNEL_COLORS[ref - 1];
    lines.push(row("Y1", si(v[0], "V", 4), color), row("Y2", si(v[1], "V", 4), color), row("ΔV", si(v[1] - v[0], "V", 4), color));
  }
  el.innerHTML = lines.join("");
  el.hidden = false;
}

$<HTMLSelectElement>("cursor-mode").addEventListener("change", (e) => {
  const mode = (e.target as HTMLSelectElement).value as CursorMode;
  if (plot.cursors.mode === "off" && mode !== "off") plot.resetCursors();
  plot.cursors.mode = mode;
  $("cursor-ref").hidden = !(mode === "volt" || mode === "both");
  render();
});

$<HTMLSelectElement>("cursor-ref").addEventListener("change", (e) => {
  plot.cursors.ref = Number((e.target as HTMLSelectElement).value);
  const wf = state.frame?.channels.find((c) => c.channel === plot.cursors.ref);
  if (wf) plot.cursors.v = [2 * wf.voltsPerDiv - wf.offset, -2 * wf.voltsPerDiv - wf.offset];
  render();
});

// ---------- FFT ----------

function renderSpectrum(): void {
  if (!state.fftOn) return;
  const source = Number($<HTMLSelectElement>("fft-source").value);
  const wf = state.frame?.channels.find((c) => c.channel === source);
  state.spectrum = wf ? spectrum(wf, $<HTMLSelectElement>("fft-window").value as WindowName) : null;
  spectrumPlot.draw(state.spectrum, Number($<HTMLSelectElement>("fft-span").value));
  const s = state.spectrum;
  $("fft-info").textContent = s
    ? `RBW ${si(s.df, "Hz")} · Nyquist ${si(s.df * s.dbv.length, "Hz")}` +
      (s.peak ? ` · Peak ${si(s.peak.frequency, "Hz", 5)} ${s.peak.dbv.toFixed(1)} dBV` : "")
    : wf === undefined && state.frame ? `C${source} is off` : "";
  $("fft-readout").textContent = s ? spectrumPlot.readout(s) ?? "" : "";
}

{
  const win = $<HTMLSelectElement>("fft-window");
  win.replaceChildren(...Object.entries(WINDOWS).map(([k, label]) => new Option(label, k)));
  for (const id of ["fft-source", "fft-window", "fft-span"]) $(id).addEventListener("change", () => renderSpectrum());
  const spectrumCanvas = $<HTMLCanvasElement>("spectrum");
  spectrumCanvas.addEventListener("mousemove", (e) => {
    spectrumPlot.hoverX = e.clientX - spectrumCanvas.getBoundingClientRect().left;
    renderSpectrum();
  });
  spectrumCanvas.addEventListener("mouseleave", () => {
    spectrumPlot.hoverX = null;
    renderSpectrum();
  });
  new ResizeObserver(() => {
    spectrumPlot.resize();
    renderSpectrum();
  }).observe(spectrumCanvas.parentElement!);
}

$("fft-btn").addEventListener("click", () => {
  state.fftOn = !state.fftOn;
  $("fft-btn").classList.toggle("on", state.fftOn);
  $("spectrum-panel").hidden = !state.fftOn;
  document.body.classList.toggle("fft-on", state.fftOn);
  if (state.fftOn) {
    spectrumPlot.resize();
    renderSpectrum();
  }
});

// ---------- recording ----------

const LOG_FIELDS: [string, (m: ReturnType<typeof measure>) => number][] = [
  ["freq_Hz", (m) => m.frequency],
  ["vpp_V", (m) => m.pkpk],
  ["max_V", (m) => m.max],
  ["min_V", (m) => m.min],
  ["mean_V", (m) => m.mean],
  ["rms_V", (m) => m.rms],
  ["duty", (m) => m.duty],
];
let logChannels: number[] = [];

function appendLogRow(frame: WaveformFrame): void {
  const cells = [new Date().toISOString()];
  for (const ch of logChannels) {
    const wf = frame.channels.find((c) => c.channel === ch);
    const m = wf && measure(wf);
    for (const [, fn] of LOG_FIELDS) {
      const v = m ? fn(m) : NaN;
      cells.push(Number.isFinite(v) ? v.toPrecision(6) : "");
    }
  }
  state.log!.push(cells.join(","));
  $("log-btn").textContent = `● Log ${state.log!.length - 1}`;
}

$("log-btn").addEventListener("click", () => {
  const btn = $("log-btn");
  if (state.log) {
    const rows = state.log;
    state.log = null;
    btn.classList.remove("recording");
    btn.textContent = "Log";
    if (rows.length > 1) download(new Blob([rows.join("\n")], { type: "text/csv" }), `measurements-${stamp()}.csv`);
    return;
  }
  if (!state.connected || !state.frame?.channels.length) return toast("Connect and enable a channel to log measurements.", "info");
  logChannels = state.frame.channels.map((c) => c.channel);
  state.log = [["time", ...logChannels.flatMap((ch) => LOG_FIELDS.map(([name]) => `C${ch}_${name}`))].join(",")];
  btn.classList.add("recording");
  btn.textContent = "● Log 0";
  if (!state.live) setLive(true);
});

$("capture-btn").addEventListener("click", () => {
  if (!state.connected) return toast("Not connected.", "info");
  toast("Reading the full acquisition memory… the CSV download starts when the transfer is done.", "info");
  const a = document.createElement("a");
  a.href = api.captureUrl;
  a.download = "";
  a.click();
});

function renderSettings(): void {
  const s = state.settings;
  if (!s) return;
  fillSelect($("tdiv"), TIME_DIVS, (v) => `${si(v, "s")}`, s.timePerDiv);
  setInput($("trdl"), s.triggerDelay);
  $("sara").textContent = si(s.sampleRate, "Sa/s");

  document.querySelectorAll<HTMLButtonElement>("#trig-mode button").forEach((b) => {
    b.classList.toggle("active", b.dataset.mode === s.trigger.mode);
  });
  const src = $<HTMLSelectElement>("trig-source");
  if (document.activeElement !== src) src.value = s.trigger.source;
  if (s.trigger.slope) $<HTMLSelectElement>("trig-slope").value = s.trigger.slope;
  if (s.trigger.coupling) $<HTMLSelectElement>("trig-coupling").value = s.trigger.coupling;
  setInput($("trig-level"), s.trigger.level);

  for (const ch of s.channels) renderChannel(ch);
}

// ---------- channel panels ----------

function buildChannelPanels(): void {
  const container = $("channels");
  container.replaceChildren();
  const src = $<HTMLSelectElement>("trig-source");
  const fftSrc = $<HTMLSelectElement>("fft-source");
  const cursorRef = $<HTMLSelectElement>("cursor-ref");
  for (const s of [src, fftSrc, cursorRef]) s.replaceChildren();
  for (let n = 1; n <= state.channelCount; n++) {
    src.add(new Option(`C${n}`, `C${n}`));
    fftSrc.add(new Option(`C${n}`, String(n)));
    cursorRef.add(new Option(`Y on C${n}`, String(n)));
    const panel = document.createElement("section");
    panel.className = "panel channel";
    panel.id = `ch${n}`;
    panel.style.setProperty("--ch", CHANNEL_COLORS[n - 1]);
    panel.innerHTML = `
      <h2><label class="switch"><input type="checkbox" data-f="enabled" /><span></span></label> Channel ${n}</h2>
      <div class="row"><label>Volts/div</label>
        <div class="stepper">
          <button data-dir="-1" aria-label="More sensitive">‹</button>
          <select data-f="voltsPerDiv"></select>
          <button data-dir="1" aria-label="Less sensitive">›</button>
        </div></div>
      <div class="row"><label>Offset</label><div class="unit-input"><input type="number" step="any" data-f="offset" /><span>V</span></div></div>
      <div class="row"><label>Coupling</label>
        <select data-f="coupling"><option value="D1M">DC</option><option value="A1M">AC</option><option value="GND">GND</option></select></div>
      <div class="row"><label>Probe</label><select data-f="probe"></select></div>`;
    container.append(panel);

    const field = <T extends HTMLElement>(f: string) => panel.querySelector<T>(`[data-f="${f}"]`)!;
    fillSelect(field("probe"), PROBES, (v) => `${v}×`);
    field<HTMLInputElement>("enabled").addEventListener("change", (e) =>
      act(() => api.channel(n, { enabled: (e.target as HTMLInputElement).checked })),
    );
    field<HTMLSelectElement>("voltsPerDiv").addEventListener("change", (e) =>
      act(() => api.channel(n, { voltsPerDiv: Number((e.target as HTMLSelectElement).value) })),
    );
    field<HTMLInputElement>("offset").addEventListener("change", (e) =>
      act(() => api.channel(n, { offset: Number((e.target as HTMLInputElement).value) })),
    );
    field<HTMLSelectElement>("coupling").addEventListener("change", (e) =>
      act(() => api.channel(n, { coupling: (e.target as HTMLSelectElement).value })),
    );
    field<HTMLSelectElement>("probe").addEventListener("change", (e) =>
      act(() => api.channel(n, { probe: Number((e.target as HTMLSelectElement).value) })),
    );
    panel.querySelectorAll<HTMLButtonElement>(".stepper button").forEach((b) =>
      b.addEventListener("click", () => {
        const ch = state.settings?.channels.find((c) => c.channel === n);
        if (!ch) return;
        const next = stepValue(voltDivs(ch.probe), ch.voltsPerDiv, Number(b.dataset.dir));
        if (next !== undefined) act(() => api.channel(n, { voltsPerDiv: next }));
      }),
    );
  }
}

function renderChannel(ch: ChannelSettings): void {
  const panel = document.getElementById(`ch${ch.channel}`);
  if (!panel) return;
  const field = <T extends HTMLElement>(f: string) => panel.querySelector<T>(`[data-f="${f}"]`)!;
  panel.classList.toggle("off", !ch.enabled);
  field<HTMLInputElement>("enabled").checked = ch.enabled;
  fillSelect(field("voltsPerDiv"), voltDivs(ch.probe), (v) => si(v, "V"), ch.voltsPerDiv);
  setInput(field("offset"), ch.offset);
  const cpl = field<HTMLSelectElement>("coupling");
  if (document.activeElement !== cpl) cpl.value = ch.coupling;
  fillSelect(field("probe"), PROBES, (v) => `${v}×`, ch.probe);
}

/** V/div follows the probe factor: a 10× probe turns 500 µV…10 V into 5 mV…100 V. */
function voltDivs(probe: number): number[] {
  return VOLT_DIVS.map((v) => Number((v * probe).toPrecision(3)));
}

function stepValue(list: number[], current: number, dir: number): number | undefined {
  const idx = list.findIndex((v) => near(v, current));
  if (idx >= 0) return list[idx + dir];
  return dir > 0 ? list.find((v) => v > current) : [...list].reverse().find((v) => v < current);
}

// ---------- controls ----------

document.querySelectorAll<HTMLButtonElement>("#trig-mode button").forEach((b) =>
  b.addEventListener("click", () => act(() => api.trigger({ mode: b.dataset.mode }))),
);
$("autosetup-btn").addEventListener("click", () => act(() => api.autoSetup()));
$<HTMLSelectElement>("tdiv").addEventListener("change", (e) =>
  act(() => api.timebase({ timePerDiv: Number((e.target as HTMLSelectElement).value) })),
);
document.querySelectorAll<HTMLButtonElement>('[data-step="tdiv"]').forEach((b) =>
  b.addEventListener("click", () => {
    if (!state.settings) return;
    const next = stepValue(TIME_DIVS, state.settings.timePerDiv, Number(b.dataset.dir));
    if (next !== undefined) act(() => api.timebase({ timePerDiv: next }));
  }),
);
$<HTMLInputElement>("trdl").addEventListener("change", (e) =>
  act(() => api.timebase({ triggerDelay: Number((e.target as HTMLInputElement).value) })),
);
$<HTMLSelectElement>("trig-source").addEventListener("change", (e) =>
  act(() => api.trigger({ source: (e.target as HTMLSelectElement).value })),
);
$<HTMLSelectElement>("trig-slope").addEventListener("change", (e) =>
  act(() => api.trigger({ slope: (e.target as HTMLSelectElement).value })),
);
$<HTMLSelectElement>("trig-coupling").addEventListener("change", (e) =>
  act(() => api.trigger({ coupling: (e.target as HTMLSelectElement).value })),
);
$<HTMLInputElement>("trig-level").addEventListener("change", (e) =>
  act(() => api.trigger({ level: Number((e.target as HTMLInputElement).value) })),
);

function setLive(live: boolean): void {
  state.live = live;
  const btn = $("live-btn");
  btn.classList.toggle("on", live);
  btn.textContent = live ? "Live" : "Paused";
}
$("live-btn").addEventListener("click", () => setLive(!state.live));
document.addEventListener("keydown", (e) => {
  if (e.code === "Space" && !(e.target instanceof HTMLInputElement || e.target instanceof HTMLSelectElement)) {
    e.preventDefault();
    setLive(!state.live);
  }
});

// ---------- plot interaction ----------

const canvas = $<HTMLCanvasElement>("plot");
let dragging: CursorHit | null = null;

function canvasPoint(e: PointerEvent): [number, number] {
  const r = canvas.getBoundingClientRect();
  return [e.clientX - r.left, e.clientY - r.top];
}

canvas.addEventListener("pointerdown", (e) => {
  const [x, y] = canvasPoint(e);
  dragging = plot.hitCursor(x, y);
  if (dragging) canvas.setPointerCapture(e.pointerId);
});
canvas.addEventListener("pointermove", (e) => {
  const [x, y] = canvasPoint(e);
  if (dragging) {
    plot.moveCursor(dragging, x, y);
  } else {
    const hit = plot.hitCursor(x, y);
    canvas.style.cursor = hit ? (hit.kind === "t" ? "ew-resize" : "ns-resize") : "crosshair";
  }
  plot.hoverX = x;
  render();
});
canvas.addEventListener("pointerup", () => (dragging = null));
canvas.addEventListener("pointerleave", () => {
  if (dragging) return;
  plot.hoverX = null;
  render();
});
new ResizeObserver(() => {
  plot.resize();
  render();
}).observe($("plot-wrap"));

// ---------- export ----------

const stamp = () => new Date().toISOString().replace(/[:T]/g, "-").slice(0, 19);

function download(blob: Blob, name: string): void {
  const a = document.createElement("a");
  a.href = URL.createObjectURL(blob);
  a.download = name;
  a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 1000);
}

$("csv-btn").addEventListener("click", () => {
  const f = state.frame;
  if (!f || f.channels.length === 0) return toast("No waveform to export.", "info");
  const len = Math.max(...f.channels.map((c) => c.volts.length));
  const base = f.channels[0];
  const rows = [["time_s", ...f.channels.map((c) => `C${c.channel}_V`)].join(",")];
  for (let i = 0; i < len; i++) {
    const t = base.firstPointTime + i * base.sampleInterval;
    rows.push([t.toExponential(6), ...f.channels.map((c) => c.volts[i]?.toPrecision(6) ?? "")].join(","));
  }
  download(new Blob([rows.join("\n")], { type: "text/csv" }), `waveform-${stamp()}.csv`);
});

$("png-btn").addEventListener("click", () => {
  canvas.toBlob((b) => b && download(b, `plot-${stamp()}.png`), "image/png");
});

let shotUrl = "";
$("screenshot-btn").addEventListener("click", async () => {
  const btn = $<HTMLButtonElement>("screenshot-btn");
  btn.disabled = true;
  try {
    const bmp = await api.screenshot();
    // Re-encode the scope's BMP as PNG so it is small and opens everywhere.
    const bitmap = await createImageBitmap(bmp);
    const c = document.createElement("canvas");
    c.width = bitmap.width;
    c.height = bitmap.height;
    c.getContext("2d")!.drawImage(bitmap, 0, 0);
    const png = await new Promise<Blob>((r) => c.toBlob((b) => r(b!), "image/png"));
    URL.revokeObjectURL(shotUrl);
    shotUrl = URL.createObjectURL(png);
    $<HTMLImageElement>("shot-img").src = shotUrl;
    $<HTMLDialogElement>("shot-dialog").showModal();
  } catch (e) {
    handleError(e);
  } finally {
    btn.disabled = false;
  }
});
$("shot-save").addEventListener("click", () => {
  const a = document.createElement("a");
  a.href = shotUrl;
  a.download = `screen-${stamp()}.png`;
  a.click();
});
$("shot-close").addEventListener("click", () => $<HTMLDialogElement>("shot-dialog").close());

// ---------- SCPI console ----------

$("console-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  const input = $<HTMLInputElement>("console-input");
  const command = input.value.trim();
  if (!command) return;
  const log = $("console-log");
  const add = (text: string, cls: string) => {
    const div = document.createElement("div");
    div.className = cls;
    div.textContent = text;
    log.append(div);
    log.scrollTop = log.scrollHeight;
  };
  add(`> ${command}`, "cmd");
  input.value = "";
  try {
    const { response } = await api.scpi(command);
    add(response ?? "(sent)", response === null ? "muted" : "resp");
    if (response === null) await refreshSettings();
  } catch (err) {
    add(err instanceof Error ? err.message : String(err), "err");
  }
});

// ---------- startup ----------

(async () => {
  plot.resize();
  render();
  try {
    const status = await api.status();
    $("version").textContent = status.version ? `v${status.version}` : "";
    let saved: string | null = null;
    try {
      saved = localStorage.getItem(HOST_KEY);
    } catch {
      /* storage unavailable */
    }
    $<HTMLInputElement>("host").value = status.connected ? status.host : saved ?? status.host;
    if (status.connected && status.identity) await onConnected(status.identity.model, status.identity.firmware);
  } catch (e) {
    handleError(e);
  }
})();
