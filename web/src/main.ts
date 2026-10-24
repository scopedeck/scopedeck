import "./style.css";
import { api, ApiError, type ChannelSettings, type HistoryState, type ScopeSettings, type TriggerSettings, type WaveformFrame } from "./api";
import { series125, si } from "./format";
import { spectrum, WINDOWS, type Spectrum, type WindowName } from "./fft";
import { computeMath, MATH_CHANNEL, MATH_OPS, type MathOp, type MathSettings } from "./math";
import { delayBetween, measure } from "./measure";
import { channelColor, channelName, isRef, ScopePlot, type CursorHit, type CursorMode } from "./plot";
import { References } from "./refs";
import { SpectrumPlot } from "./spectrum";

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;

const TIME_DIVS = series125(1e-9, 100);
const VOLT_DIVS = series125(500e-6, 10);
const PROBES = [0.1, 0.2, 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000];
const SETTINGS_REFRESH_MS = 1500;
const SCOPE_MEASURE_REFRESH_MS = 2000;

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
  scopeDelays: new Map<number, Record<string, number | null>>(),
  /** Measurements limited to the span between the time cursors. */
  gate: false,
  /** What was last sent to the scope's gate: "off", "t1,t2", or null (never sent). */
  scopeGate: null as string | null,
  fftOn: false,
  spectrum: null as Spectrum | null,
  /** Measurement log rows while logging, otherwise null. */
  log: null as string[] | null,
  math: null as MathSettings | null,
  historyPlaying: false,
  /** The selected channel: drawn on top and highlighted, like the active channel on the scope. */
  activeChannel: 1,
};

const refs = new References();

/**
 * What is shown: the scope's channels (with their unit), the app-side math trace (channel 0) when math is on,
 * and the visible reference traces (101–104).
 */
function displayFrame(): WaveformFrame | null {
  const f = state.frame;
  if (!f) return null;
  const units = new Map(state.settings?.channels.map((c) => [c.channel, c.unit]) ?? []);
  const channels = f.channels.map((c) => (units.get(c.channel) === "A" ? { ...c, unit: "A" } : c));
  const m = state.math ? computeMath({ ...f, channels }, state.math) : null;
  return { ...f, channels: [...channels, ...(m ? [m] : []), ...refs.visible()] };
}

/** Unit of a trace: V for channels, the operation's unit for math. */
const unitOf = (wf: { unit?: string }) => wf.unit ?? "V";

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
    setPanelOpen(false);
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
    // The server remembers the address for next time.
    const id = await api.connect(host);
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
  updateTraceOptions();
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
        await fetchFrame();
        if (state.log && state.frame) appendLogRow(state.frame);
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

/**
 * Reads a frame; while zoomed, only the visible part of the record (at up to full resolution).
 * A new timebase or delay invalidates the zoom window and the afterglow.
 */
async function fetchFrame(): Promise<void> {
  const points = Number($<HTMLSelectElement>("points").value);
  const record = plot.recordWindow(state.frame);
  let start = 0;
  let end = 1;
  if (plot.view && record) {
    start = Math.max(0, (plot.view.start - record.start) / record.span);
    end = Math.min(1, (plot.view.start + plot.view.span - record.start) / record.span);
    if (!(end > start)) [start, end] = [0, 1];
  }
  const frame = await api.frame(points, start, end, gateWindow());
  const previous = state.frame;
  if (previous && (previous.timePerDiv !== frame.timePerDiv || previous.triggerDelay !== frame.triggerDelay)) {
    setZoom(null);
    plot.clearPersistence();
  }
  state.frame = frame;
  const shown = displayFrame();
  if (shown) plot.accumulate(shown);
}

async function refreshSettings(): Promise<void> {
  if (!state.connected) return;
  state.settings = await api.settings();
  state.settingsAt = performance.now();
  renderSettings();
}

// ---------- rendering ----------

function render(): void {
  plot.draw(displayFrame(), state.settings);
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
    ...r.values.map((v) => line(channelName(v.channel), v.text, channelColor(v.channel))),
  );
  function line(label: string, value: string, color: string) {
    const div = document.createElement("div");
    div.innerHTML = `<b style="color:${color}">${label}</b> <span>${value}</span>`;
    return div;
  }
}

const APP_COLUMNS: [string, (m: ReturnType<typeof measure>, unit: string) => string][] = [
  ["Freq", (m) => si(m.frequency, "Hz", 5)],
  ["Period", (m) => si(1 / m.frequency, "s", 4)],
  ["Pk-pk", (m, u) => si(m.pkpk, u)],
  ["Max", (m, u) => si(m.max, u)],
  ["Min", (m, u) => si(m.min, u)],
  ["Mean", (m, u) => si(m.mean, u)],
  ["RMS", (m, u) => si(m.rms, u)],
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
  const f = displayFrame();
  if (!f || f.channels.length === 0) {
    el.replaceChildren();
    return;
  }
  const scope = state.measureSource === "scope";
  const titles = scope ? SCOPE_COLUMNS.map(([, t]) => t) : APP_COLUMNS.map(([t]) => t);
  const table = document.createElement("table");
  table.innerHTML = `<thead><tr><th></th>${titles.map((h) => `<th>${h}</th>`).join("")}</tr></thead>`;
  const body = table.createTBody();
  let aliased = false;
  const window = gateWindow();
  for (const wf of f.channels) {
    let cells: string[];
    if (scope && wf.channel !== MATH_CHANNEL && !isRef(wf.channel)) {
      const m = state.scopeMeasurements.get(wf.channel);
      cells = SCOPE_COLUMNS.map(([key, , fmt]) => (m?.[key] == null ? "—" : fmt(m[key]!)));
    } else if (scope) {
      cells = SCOPE_COLUMNS.map(() => "—");
    } else {
      const m = measure(wf, window);
      aliased ||= m.aliased;
      cells = APP_COLUMNS.map(([, fn]) => fn(m, unitOf(wf)));
    }
    const row = body.insertRow();
    row.innerHTML = `<th style="color:${channelColor(wf.channel)}">${channelName(wf.channel)}</th>${cells.map((c) => `<td>${c}</td>`).join("")}`;
  }
  el.replaceChildren(table);

  // Delay and phase of every other channel relative to C1.
  const c1 = f.channels.find((c) => c.channel === 1);
  const others = f.channels.filter((c) => c.channel > 1 && !isRef(c.channel));
  if (c1 && others.length) {
    const delays = document.createElement("div");
    delays.className = "delays";
    for (const wf of others) {
      let text: string;
      if (scope) {
        const d = state.scopeDelays.get(wf.channel);
        text = d?.FRR == null && d?.PHA == null ? "—" : `delay ${d?.FRR == null ? "—" : si(d.FRR, "s", 4)}  ·  phase ${d?.PHA == null ? "—" : `${d.PHA.toFixed(1)}°`}`;
      } else {
        const d = delayBetween(c1, wf);
        text = d ? `delay ${si(d.delay, "s", 4)}  ·  phase ${d.phase === null ? "—" : `${d.phase.toFixed(1)}°`}` : "—";
      }
      const item = document.createElement("span");
      item.innerHTML = `<b style="color:${channelColor(1)}">C1</b> → <b style="color:${channelColor(wf.channel)}">${channelName(wf.channel)}</b> <span></span>`;
      item.querySelector("span")!.textContent = text;
      item.title = "Rising edge of C1 to the nearest rising edge of the other channel";
      delays.append(item);
    }
    el.append(delays);
  }

  if (aliased) {
    const note = document.createElement("p");
    note.className = "measure-note";
    note.textContent =
      "Freq, Period, Duty: too many signal cycles even for the dense read. Use “Scope” measurements, zoom in, or a faster time/div.";
    el.append(note);
  }
}

async function refreshScopeMeasurements(): Promise<void> {
  state.scopeMeasurementsAt = performance.now();
  await syncScopeGate();
  const channels = (state.frame?.channels ?? []).map((c) => c.channel);
  for (const ch of channels) state.scopeMeasurements.set(ch, await api.measurements(ch));
  if (channels.includes(1)) {
    for (const ch of channels.filter((c) => c > 1)) state.scopeDelays.set(ch, await api.delay(1, ch));
  }
}

// ---------- measurement gate ----------

/** The time window measurements are limited to (between the X1 and X2 cursors), or null. */
function gateWindow(): [number, number] | null {
  if (!state.gate) return null;
  const [a, b] = plot.cursors.t;
  return a === b ? null : [Math.min(a, b), Math.max(a, b)];
}

/** Sends the gate to the scope when its own measurements are shown; switches the scope's gate off otherwise. */
async function syncScopeGate(): Promise<void> {
  const window = state.measureSource === "scope" ? gateWindow() : null;
  const key = window ? window.join() : "off";
  if (key === state.scopeGate || (key === "off" && state.scopeGate === null)) return;
  await api.gate(window);
  state.scopeGate = key;
  state.scopeMeasurementsAt = 0;
}

$<HTMLInputElement>("gate").addEventListener("change", async (e) => {
  state.gate = (e.target as HTMLInputElement).checked;
  if (state.gate && !(plot.cursors.mode === "time" || plot.cursors.mode === "both")) {
    // The gate is the span between the time cursors, so they must be visible.
    if (plot.cursors.mode === "off") plot.resetCursors();
    plot.cursors.mode = plot.cursors.mode === "volt" ? "both" : "time";
    $<HTMLSelectElement>("cursor-mode").value = plot.cursors.mode;
  }
  await syncScopeGate().catch(handleError);
  render();
});

document.querySelectorAll<HTMLButtonElement>("#measure-source button").forEach((b) =>
  b.addEventListener("click", async () => {
    state.measureSource = b.dataset.source as MeasureSource;
    document.querySelectorAll("#measure-source button").forEach((x) => x.classList.toggle("active", x === b));
    if (state.measureSource === "scope") {
      state.scopeMeasurements.clear();
      state.scopeDelays.clear();
      state.scopeMeasurementsAt = 0;
      if (!state.live && state.connected) await refreshScopeMeasurements().catch(handleError);
    } else {
      await syncScopeGate().catch(handleError);
    }
    render();
  }),
);

// ---------- cursors ----------

function renderCursorInfo(): void {
  const el = $("cursor-info");
  const { mode, t, v, ref } = plot.cursors;
  const f = displayFrame();
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
      const u = unitOf(wf);
      const fmt = (x: number | undefined) => (x === undefined ? "—" : si(x, u, 4));
      const d = a === undefined || b === undefined ? "—" : si(b - a, u, 4);
      lines.push(row(channelName(wf.channel), `${fmt(a)} → ${fmt(b)}  Δ ${d}`, channelColor(wf.channel)));
    }
  }
  if (mode === "volt" || mode === "both") {
    const color = channelColor(ref);
    const u = unitOf(f.channels.find((c) => c.channel === ref) ?? {});
    lines.push(row("Y1", si(v[0], u, 4), color), row("Y2", si(v[1], u, 4), color), row("ΔY", si(v[1] - v[0], u, 4), color));
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
  const wf = displayFrame()?.channels.find((c) => c.channel === plot.cursors.ref);
  if (wf) plot.cursors.v = [2 * wf.voltsPerDiv - wf.offset, -2 * wf.voltsPerDiv - wf.offset];
  render();
});

// ---------- FFT ----------

function renderSpectrum(): void {
  if (!state.fftOn) return;
  const source = Number($<HTMLSelectElement>("fft-source").value);
  const wf = displayFrame()?.channels.find((c) => c.channel === source);
  state.spectrum = wf ? spectrum(wf, $<HTMLSelectElement>("fft-window").value as WindowName) : null;
  spectrumPlot.draw(state.spectrum, Number($<HTMLSelectElement>("fft-span").value));
  const s = state.spectrum;
  $("fft-info").textContent = s
    ? `RBW ${si(s.df, "Hz")} · Nyquist ${si(s.df * s.dbv.length, "Hz")}` +
      (s.peak ? ` · Peak ${si(s.peak.frequency, "Hz", 5)} ${s.peak.dbv.toFixed(1)} dBV` : "")
    : wf === undefined && state.frame ? `${channelName(source)} is off` : "";
  const readout = s ? spectrumPlot.readout(s) : null;
  $("fft-readout").textContent = readout ?? "";
  $("fft-readout").hidden = !readout;
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

// ---------- trace lists (channels + math) ----------

/** Fills the FFT source and voltage-cursor lists with the channels, plus M while math is on. */
function updateTraceOptions(): void {
  const traces = [
    ...Array.from({ length: state.channelCount }, (_, i) => i + 1),
    ...(state.math ? [MATH_CHANNEL] : []),
    ...refs.items.map((r) => r.id),
  ];
  for (const [id, prefix] of [["fft-source", ""], ["cursor-ref", "Y on "]] as const) {
    const sel = $<HTMLSelectElement>(id);
    const keep = sel.value;
    sel.replaceChildren(...traces.map((ch) => new Option(prefix + channelName(ch), String(ch))));
    if (traces.map(String).includes(keep)) sel.value = keep;
  }
  if (!traces.includes(plot.cursors.ref)) plot.cursors.ref = 1;
}

// ---------- math ----------

{
  $<HTMLSelectElement>("math-op").replaceChildren(
    ...Object.entries(MATH_OPS).map(([k, d]) => new Option(d.label, k)),
  );
  const scale = $<HTMLSelectElement>("math-scale");
  scale.replaceChildren(new Option("Auto", ""), ...series125(1e-6, 1e9).map((v) => new Option(`${si(v, "")}/div`, String(v))));
}

function readMathSettings(): MathSettings {
  const scale = $<HTMLSelectElement>("math-scale").value;
  return {
    op: $<HTMLSelectElement>("math-op").value as MathOp,
    a: Number($<HTMLSelectElement>("math-a").value),
    b: Number($<HTMLSelectElement>("math-b").value),
    scale: scale ? Number(scale) : null,
    offset: Number($<HTMLInputElement>("math-offset").value) || 0,
  };
}

function updateMath(): void {
  if (!state.math) return;
  state.math = readMathSettings();
  const def = MATH_OPS[state.math.op];
  $("math-b-wrap").hidden = !def.binary;
  $("math-unit").textContent = def.unit || "×";
  render();
}

$("math-btn").addEventListener("click", () => {
  const on = !state.math;
  for (const id of ["math-a", "math-b"]) {
    const sel = $<HTMLSelectElement>(id);
    if (sel.options.length !== state.channelCount) {
      sel.replaceChildren(...Array.from({ length: state.channelCount }, (_, i) => new Option(`C${i + 1}`, String(i + 1))));
      sel.value = id === "math-b" && state.channelCount > 1 ? "2" : "1";
    }
  }
  state.math = on ? readMathSettings() : null;
  $("math-btn").classList.toggle("on", on);
  $("math-panel").hidden = !on;
  updateTraceOptions();
  updateMath();
  if (!$("ref-panel").hidden) renderRefs();
  render();
});
for (const id of ["math-op", "math-a", "math-b", "math-scale", "math-offset"]) $(id).addEventListener("change", updateMath);

// ---------- references ----------

function renderRefs(): void {
  const list = $("ref-list");
  list.replaceChildren(
    ...refs.items.map((r) => {
      const chip = document.createElement("span");
      chip.className = `chip${r.visible ? "" : " hidden-ref"}`;
      chip.style.setProperty("--c", channelColor(r.id));
      chip.title = r.label;
      chip.innerHTML = `<b>${channelName(r.id)}</b><span class="muted"></span>`;
      chip.querySelector("span")!.textContent = r.label.length > 28 ? `${r.label.slice(0, 27)}…` : r.label;
      const eye = document.createElement("button");
      eye.textContent = r.visible ? "Hide" : "Show";
      eye.addEventListener("click", () => {
        r.visible = !r.visible;
        renderRefs();
        render();
      });
      const del = document.createElement("button");
      del.textContent = "✕";
      del.title = "Remove";
      del.addEventListener("click", () => {
        refs.remove(r.id);
        renderRefs();
        render();
      });
      chip.append(eye, del);
      return chip;
    }),
  );
  updateTraceOptions();
  // Traces that can be frozen: channels and math, not other references.
  const src = $<HTMLSelectElement>("ref-source");
  const keep = src.value;
  const traces = (displayFrame()?.channels ?? []).filter((c) => !isRef(c.channel)).map((c) => c.channel);
  src.replaceChildren(...traces.map((ch) => new Option(channelName(ch), String(ch))));
  if (traces.map(String).includes(keep)) src.value = keep;
}

$("ref-btn").addEventListener("click", () => {
  const on = $("ref-panel").hidden;
  $("ref-panel").hidden = !on;
  $("ref-btn").classList.toggle("on", on);
  if (on) renderRefs();
});
$("ref-save").addEventListener("click", () => {
  const ch = Number($<HTMLSelectElement>("ref-source").value);
  const wf = displayFrame()?.channels.find((c) => c.channel === ch);
  if (!wf) return toast("That trace has no data right now.", "info");
  const time = new Date().toLocaleTimeString();
  if (!refs.save(wf, `${channelName(ch)} at ${time}`)) return toast("All four reference slots are in use; remove one first.", "info");
  renderRefs();
  render();
});
$("ref-load").addEventListener("click", () => $<HTMLInputElement>("ref-file").click());
$<HTMLInputElement>("ref-file").addEventListener("change", async (e) => {
  const input = e.target as HTMLInputElement;
  const file = input.files?.[0];
  input.value = "";
  if (!file) return;
  try {
    const n = refs.loadCsv(await file.text(), file.name);
    toast(`Loaded ${n} reference trace${n === 1 ? "" : "s"} from ${file.name}.`, "info");
  } catch (err) {
    toast(err instanceof Error ? err.message : String(err));
  }
  renderRefs();
  render();
});

// ---------- history ----------

function renderHistory(h: HistoryState): void {
  $("history-btn").classList.toggle("on", h.enabled);
  $("history-panel").hidden = !h.enabled;
  if (!h.enabled) {
    state.historyPlaying = false;
    $("hist-play").classList.remove("on");
    return;
  }
  const slider = $<HTMLInputElement>("hist-slider");
  slider.max = String(Math.max(1, h.frameCount));
  if (document.activeElement !== slider) slider.value = String(h.frame);
  $("hist-info").textContent = `Frame ${h.frame} / ${h.frameCount}${h.frameTime ? `  ·  ${h.frameTime}` : ""}`;
}

/** Selects a history frame and shows it immediately. */
async function goToFrame(frame: number): Promise<void> {
  const h = state.settings?.history;
  if (!h?.enabled) return;
  const target = Math.min(Math.max(1, frame), h.frameCount);
  const next = await api.historyFrame(target);
  state.settings = { ...state.settings!, history: next };
  renderHistory(next);
  await fetchFrame();
  render();
}

$("history-btn").addEventListener("click", () => {
  const on = !state.settings?.history.enabled;
  if (on && state.settings?.acquisition.mode === "AVERAGE") {
    return toast("History isn't available in Average mode. Switch acquisition to Normal, Peak detect or High-res first.", "info");
  }
  act(async () => {
    const h = await api.history(on);
    if (on && h.enabled) toast("History mode stops acquisition; switch it off to resume.", "info");
    else if (on) toast("The scope did not switch history mode on.", "info");
  });
});
const frameNow = () => state.settings?.history.frame ?? 1;
$("hist-first").addEventListener("click", () => goToFrame(1).catch(handleError));
$("hist-prev").addEventListener("click", () => goToFrame(frameNow() - 1).catch(handleError));
$("hist-next").addEventListener("click", () => goToFrame(frameNow() + 1).catch(handleError));
$("hist-last").addEventListener("click", () => goToFrame(state.settings?.history.frameCount ?? 1).catch(handleError));
$<HTMLInputElement>("hist-slider").addEventListener("change", (e) =>
  goToFrame(Number((e.target as HTMLInputElement).value)).catch(handleError),
);
$("hist-play").addEventListener("click", async () => {
  state.historyPlaying = !state.historyPlaying;
  $("hist-play").classList.toggle("on", state.historyPlaying);
  if (!state.historyPlaying) return;
  if (frameNow() >= (state.settings?.history.frameCount ?? 1)) await goToFrame(1).catch(handleError);
  while (state.historyPlaying && state.settings?.history.enabled) {
    const h = state.settings.history;
    if (h.frame >= h.frameCount) break;
    try {
      await goToFrame(h.frame + 1);
    } catch (e) {
      handleError(e);
      break;
    }
    await sleep(120);
  }
  state.historyPlaying = false;
  $("hist-play").classList.remove("on");
});
$("hist-export").addEventListener("click", () => {
  toast("Reading every history frame… the CSV download starts when all frames are read.", "info");
  const a = document.createElement("a");
  a.href = api.historyExportUrl(Number($<HTMLSelectElement>("points").value));
  a.download = "";
  a.click();
});

// ---------- save / load menu ----------

const saveMenu = $<HTMLDetailsElement>("save-menu");
saveMenu.querySelectorAll("button").forEach((b) => b.addEventListener("click", () => (saveMenu.open = false)));
document.addEventListener("click", (e) => {
  if (saveMenu.open && !saveMenu.contains(e.target as Node)) saveMenu.open = false;
});

$("setup-save-btn").addEventListener("click", () => {
  if (!state.connected) return toast("Not connected.", "info");
  const a = document.createElement("a");
  a.href = api.setupUrl;
  a.download = "";
  a.click();
});
$("setup-load-btn").addEventListener("click", () => {
  if (!state.connected) return toast("Not connected.", "info");
  $<HTMLInputElement>("setup-file").click();
});
$<HTMLInputElement>("setup-file").addEventListener("change", async (e) => {
  const input = e.target as HTMLInputElement;
  const file = input.files?.[0];
  input.value = "";
  if (!file) return;
  if (!confirm(`Load "${file.name}" into the scope? This replaces all of its current settings.`)) return;
  await act(() => api.loadSetup(file));
  toast("Setup loaded.", "info");
});

{
  const slot = $<HTMLSelectElement>("mem-slot");
  slot.replaceChildren(...Array.from({ length: 20 }, (_, i) => new Option(String(i + 1), String(i + 1))));
}
$("memory-btn").addEventListener("click", () => {
  if (!state.connected) return toast("Not connected.", "info");
  $<HTMLDialogElement>("mem-dialog").showModal();
});
$("mem-close").addEventListener("click", () => $<HTMLDialogElement>("mem-dialog").close());
$("mem-recall").addEventListener("click", async () => {
  const n = Number($<HTMLSelectElement>("mem-slot").value);
  $<HTMLDialogElement>("mem-dialog").close();
  await act(() => api.recallMemory(n));
  toast(`Recalled setup ${n}.`, "info");
});
$("mem-store").addEventListener("click", async () => {
  const n = Number($<HTMLSelectElement>("mem-slot").value);
  if (!confirm(`Store the current setup in memory ${n}? Whatever is stored there now is overwritten.`)) return;
  $<HTMLDialogElement>("mem-dialog").close();
  await act(() => api.saveMemory(n));
  toast(`Stored in memory ${n}.`, "info");
});
$("default-setup-btn").addEventListener("click", async () => {
  if (!state.connected) return toast("Not connected.", "info");
  if (!confirm("Reset the scope to its default setup? All current settings are lost unless saved first.")) return;
  await act(() => api.recallMemory(0));
  toast("Default setup recalled.", "info");
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
  setSelect($("interp"), s.acquisition.sinXInterpolation ? "1" : "0");

  document.querySelectorAll<HTMLButtonElement>("#trig-mode button").forEach((b) => {
    b.classList.toggle("active", b.dataset.mode === s.trigger.mode);
  });
  renderAcquisition(s);
  renderTrigger(s.trigger);
  syncActiveChannel(s.channels);
  for (const ch of s.channels) renderChannel(ch);
  renderHistory(s.history);
}

// ---------- acquisition ----------

const AVERAGES = [4, 16, 32, 64, 128, 256, 512, 1024];
const ACQ_MODES: [string, string][] = [
  ["SAMPLING", "Normal"],
  ["PEAK_DETECT", "Peak detect"],
  ["HIGH_RES", "High-res"],
  ...AVERAGES.map((n): [string, string] => [`AVERAGE,${n}`, `Average ×${n}`]),
];
$<HTMLSelectElement>("acq-mode").replaceChildren(...ACQ_MODES.map(([v, label]) => new Option(label, v)));

function renderAcquisition(s: ScopeSettings): void {
  const a = s.acquisition;
  setSelect($("acq-mode"), a.mode === "AVERAGE" ? `AVERAGE,${a.averages}` : a.mode);
  // With one channel on, the ADCs interleave and memory doubles (14K…14M); with more, 7K…7M.
  const single = s.channels.filter((c) => c.enabled).length <= 1;
  const depths = single ? ["14K", "140K", "1.4M", "14M"] : ["7K", "70K", "700K", "7M"];
  if (!depths.includes(a.memoryDepth)) depths.push(a.memoryDepth);
  const mem = $<HTMLSelectElement>("mem-depth");
  if (mem.dataset.key !== depths.join()) {
    mem.replaceChildren(...depths.map((d) => new Option(`${d.replace("K", " k").replace("M", " M")}pts`, d)));
    mem.dataset.key = depths.join();
  }
  setSelect(mem, a.memoryDepth);
}

$<HTMLSelectElement>("acq-mode").addEventListener("change", (e) => {
  const [mode, n] = (e.target as HTMLSelectElement).value.split(",");
  act(() => api.acquisition(n ? { mode, averages: Number(n) } : { mode }));
});
$<HTMLSelectElement>("mem-depth").addEventListener("change", (e) =>
  act(() => api.acquisition({ memoryDepth: (e.target as HTMLSelectElement).value })),
);
$<HTMLSelectElement>("interp").addEventListener("change", (e) =>
  act(() => api.acquisition({ sinXInterpolation: (e.target as HTMLSelectElement).value === "1" })),
);

// ---------- trigger ----------

/** Condition choices per trigger family; values are the scope's TRSE hold types. */
const CONDITIONS: Record<string, { label: string; options: [string, string][] }> = {
  GLIT: { label: "Width", options: [["PS", "shorter than"], ["PL", "longer than"], ["P2", "between"], ["P1", "outside"]] },
  RUNT: { label: "Width", options: [["PS", "shorter than"], ["PL", "longer than"], ["P2", "between"], ["P1", "outside"]] },
  SLEW: { label: "Time", options: [["IS", "shorter than"], ["IL", "longer than"], ["I2", "between"], ["I1", "outside"]] },
  INTV: { label: "Interval", options: [["IS", "shorter than"], ["IL", "longer than"], ["I2", "between"], ["I1", "outside"]] },
};
const isRange = (hold: string | null) => hold !== null && ["P2", "P1", "I2", "I1"].includes(hold);

function renderTrigger(t: TriggerSettings): void {
  setSelect($("trig-type"), t.type);
  setSelect($("trig-source"), t.source);
  // Which rows a trigger type uses: pattern has no single source; window and video have no slope.
  const isPattern = t.type === "PA";
  const isVideo = t.type === "TV";
  const isWindow = t.type === "WIN";
  $("trig-source").hidden = isPattern;
  $("trig-edge-row").hidden = isPattern || isVideo || isWindow;
  $("trig-level-row").hidden = isPattern || isVideo;
  if (t.slope) setSelect($("trig-slope"), t.slope);
  if (t.coupling) setSelect($("trig-coupling"), t.coupling);
  $("trig-slope-label").textContent = t.type === "EDGE" ? "Edge" : "Polarity";
  setInput($("trig-level"), t.level);
  const twoLevels = t.type === "SLEW" || t.type === "RUNT" || isWindow;
  $("trig-level-label").textContent = isWindow ? "Upper" : twoLevels ? "High level" : "Level";
  $("trig-level2-label").textContent = isWindow ? "Lower" : "Low level";
  $("trig-level2-row").hidden = !twoLevels;
  setInput($("trig-level2"), t.level2);
  $("set50-btn").hidden = t.type !== "EDGE";

  $("trig-video-row").hidden = $("trig-line-row").hidden = !isVideo;
  if (t.video) {
    setSelect($("trig-standard"), t.video.standard);
    setSelect($("trig-sync"), t.video.sync);
    const select = t.video.sync === "SELECT";
    $("trig-line").hidden = !select;
    setInput($("trig-line"), t.video.line);
    // Only interlaced standards have two fields.
    $("trig-field").hidden = !select || !["NTSC", "PAL", "1080I/50", "1080I/60"].includes(t.video.standard);
    if (t.video.field) setSelect($("trig-field"), String(t.video.field));
  }

  $("trig-pattern").hidden = $("trig-logic-row").hidden = $("trig-pattern-hint").hidden = !isPattern;
  if (t.pattern) renderPattern(t.pattern);

  $("trig-holdoff-row").hidden = t.type !== "EDGE";
  setInput($("trig-holdoff"), t.type === "EDGE" && t.holdType === "TI" ? t.holdValue : null);

  const cond = CONDITIONS[t.type];
  $("trig-cond-row").hidden = !cond;
  $("trig-time-row").hidden = !cond && t.type !== "DROP";
  $("trig-time-label").textContent = t.type === "DROP" ? "Timeout" : "Time";
  if (cond) {
    $("trig-cond-label").textContent = cond.label;
    const sel = $<HTMLSelectElement>("trig-cond");
    if (sel.dataset.key !== t.type) {
      sel.replaceChildren(...cond.options.map(([v, label]) => new Option(label, v)));
      sel.dataset.key = t.type;
    }
    if (t.holdType) setSelect(sel, t.holdType);
  }
  const range = isRange(t.holdType);
  $("trig-time2").hidden = $("trig-time2-sep").hidden = !range;
  setInput($("trig-time"), t.holdValue);
  setInput($("trig-time2"), t.holdValue2);
}

$<HTMLSelectElement>("trig-type").addEventListener("change", (e) =>
  act(() => api.trigger({ type: (e.target as HTMLSelectElement).value })),
);

/** One row per channel: high / low / don't care. Thresholds are set on the scope (it reports none in pattern mode). */
function renderPattern(p: NonNullable<TriggerSettings["pattern"]>): void {
  const box = $("trig-pattern");
  if (box.dataset.channels !== String(state.channelCount)) {
    box.dataset.channels = String(state.channelCount);
    box.replaceChildren(
      ...Array.from({ length: state.channelCount }, (_, i) => {
        const ch = `C${i + 1}`;
        const row = document.createElement("div");
        row.className = "row";
        row.innerHTML = `<label style="color:${channelColor(i + 1)}">${ch}</label>
          <select data-ch="${ch}" data-f="state" title="High or low compared with the channel's threshold, or don't care">
            <option value="H">High</option><option value="L">Low</option><option value="X">Don't care</option>
          </select>`;
        row.querySelector("select")!.addEventListener("change", (e) =>
          act(() => api.trigger({ patternStates: { [ch]: (e.target as HTMLSelectElement).value } })),
        );

        return row;
      }),
    );
  }
  box.querySelectorAll<HTMLSelectElement>("select[data-f=state]").forEach((s) => setSelect(s, p.states[s.dataset.ch!] ?? "X"));

  setSelect($("trig-logic"), p.logic);
}

$<HTMLSelectElement>("trig-logic").addEventListener("change", (e) =>
  act(() => api.trigger({ patternLogic: (e.target as HTMLSelectElement).value })),
);
$<HTMLSelectElement>("trig-standard").addEventListener("change", (e) =>
  act(() => api.trigger({ type: "TV", videoStandard: (e.target as HTMLSelectElement).value })),
);
$<HTMLSelectElement>("trig-sync").addEventListener("change", (e) =>
  act(() => api.trigger({ type: "TV", videoSync: (e.target as HTMLSelectElement).value })),
);
$<HTMLInputElement>("trig-line").addEventListener("change", (e) =>
  act(() => api.trigger({ type: "TV", videoLine: Math.round(Number((e.target as HTMLInputElement).value)) })),
);
$<HTMLSelectElement>("trig-field").addEventListener("change", (e) =>
  act(() => api.trigger({ type: "TV", videoField: Number((e.target as HTMLSelectElement).value) })),
);
$<HTMLSelectElement>("trig-cond").addEventListener("change", (e) =>
  act(() => api.trigger({ holdType: (e.target as HTMLSelectElement).value })),
);
$<HTMLInputElement>("trig-holdoff").addEventListener("change", (e) => {
  const v = Number((e.target as HTMLInputElement).value);
  act(() => api.trigger(v > 0 ? { holdType: "TI", holdValue: v } : { holdType: "OFF" }));
});
$<HTMLInputElement>("trig-time").addEventListener("change", (e) =>
  act(() => api.trigger({ holdValue: Number((e.target as HTMLInputElement).value) })),
);
$<HTMLInputElement>("trig-time2").addEventListener("change", (e) =>
  act(() => api.trigger({ holdValue2: Number((e.target as HTMLInputElement).value) })),
);
$<HTMLInputElement>("trig-level2").addEventListener("change", (e) =>
  act(() => api.trigger({ level2: Number((e.target as HTMLInputElement).value) })),
);
$("set50-btn").addEventListener("click", () => act(() => api.set50()));

function setSelect(select: HTMLSelectElement, value: string): void {
  if (document.activeElement !== select) select.value = value;
}

// ---------- channel panels ----------

function buildChannelPanels(): void {
  const container = $("channels");
  container.replaceChildren();
  const src = $<HTMLSelectElement>("trig-source");
  src.replaceChildren();
  for (let n = 1; n <= state.channelCount; n++) {
    src.add(new Option(`C${n}`, `C${n}`));
    const panel = document.createElement("section");
    panel.className = "panel channel";
    panel.id = `ch${n}`;
    panel.style.setProperty("--ch", channelColor(n));
    panel.innerHTML = `
      <h2><label class="switch"><input type="checkbox" data-f="enabled" /><span></span></label>
        <button class="ch-name" data-f="name" title="Like the scope's channel button: on → to front → off">Channel ${n}</button>
        <button class="flag-btn bw-btn" data-f="bwl" title="20 MHz bandwidth limit">20M</button>
        <button class="flag-btn" data-f="inv" title="Invert the channel">INV</button>
        <button class="flag-btn" data-f="more" title="Unit and skew">⋯</button></h2>
      <div class="row"><label>Volts/div</label>
        <div class="stepper">
          <button data-dir="-1" aria-label="More sensitive" title="Shift+click: fine step">‹</button>
          <select data-f="voltsPerDiv"></select>
          <button data-dir="1" aria-label="Less sensitive" title="Shift+click: fine step">›</button>
        </div></div>
      <div class="row"><label>Offset</label><div class="unit-input"><input type="number" step="any" data-f="offset" /><span data-f="offset-unit">V</span></div></div>
      <div class="row"><label>Input</label>
        <div class="pair">
          <select data-f="coupling" title="Coupling"><option value="D1M">DC</option><option value="A1M">AC</option><option value="GND">GND</option></select>
          <select data-f="probe" title="Probe attenuation"></select>
        </div></div>
      <div class="row extra-row" data-f="extra" hidden><label>Unit · skew</label>
        <div class="pair">
          <select data-f="unit" title="Unit (A for current probes)"><option value="V">Volts</option><option value="A">Amps</option></select>
          <div class="unit-input"><input type="number" step="any" min="-100" max="100" data-f="skew" title="Channel-to-channel skew, ±100 ns" /><span>ns</span></div>
        </div></div>`;
    container.append(panel);

    const field = <T extends HTMLElement>(f: string) => panel.querySelector<T>(`[data-f="${f}"]`)!;
    fillSelect(field("probe"), PROBES, (v) => `${v}×`);
    field<HTMLButtonElement>("bwl").addEventListener("click", (e) => {
      e.preventDefault();
      const ch = state.settings?.channels.find((c) => c.channel === n);
      if (ch) act(() => api.channel(n, { bandwidthLimit: !ch.bandwidthLimit }));
    });
    field<HTMLButtonElement>("inv").addEventListener("click", (e) => {
      e.preventDefault();
      const ch = state.settings?.channels.find((c) => c.channel === n);
      if (ch) act(() => api.channel(n, { inverted: !ch.inverted }));
    });
    field<HTMLButtonElement>("more").addEventListener("click", (e) => {
      e.preventDefault();
      const extra = field("extra");
      extra.hidden = !extra.hidden;
      field("more").classList.toggle("on", !extra.hidden);
    });
    field<HTMLSelectElement>("unit").addEventListener("change", (e) =>
      act(() => api.channel(n, { unit: (e.target as HTMLSelectElement).value })),
    );
    field<HTMLInputElement>("skew").addEventListener("change", (e) =>
      act(() => api.channel(n, { skew: Number((e.target as HTMLInputElement).value) * 1e-9 })),
    );
    field<HTMLInputElement>("enabled").addEventListener("change", (e) => {
      const on = (e.target as HTMLInputElement).checked;
      if (on) state.activeChannel = n;
      act(() => api.channel(n, { enabled: on }));
    });
    field<HTMLButtonElement>("name").addEventListener("click", () => pressChannel(n));
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
      b.addEventListener("click", (e) => {
        const ch = state.settings?.channels.find((c) => c.channel === n);
        if (!ch) return;
        const dir = Number(b.dataset.dir);
        // Shift+click: fine step of about 2 %; the scope rounds to its nearest fine setting.
        const next = e.shiftKey ? ch.voltsPerDiv * (dir > 0 ? 1.02 : 1 / 1.02) : stepValue(voltDivs(ch.probe), ch.voltsPerDiv, dir);
        if (next !== undefined) act(() => api.channel(n, { voltsPerDiv: next }));
      }),
    );
  }
}

/**
 * The scope's channel button: an off channel is switched on and selected, an on channel is brought to the front,
 * and the selected channel is switched off (the next enabled channel becomes the selected one).
 */
function pressChannel(n: number): void {
  const ch = state.settings?.channels.find((c) => c.channel === n);
  if (!ch) return;
  if (!ch.enabled) {
    state.activeChannel = n;
    act(() => api.channel(n, { enabled: true }));
  } else if (state.activeChannel !== n) {
    state.activeChannel = n;
    renderSettings();
    render();
  } else {
    act(() => api.channel(n, { enabled: false }));
  }
}

/** Keeps the selected channel on an enabled one, if any. */
function syncActiveChannel(channels: ChannelSettings[]): void {
  const on = channels.filter((c) => c.enabled).map((c) => c.channel);
  if (on.length > 0 && !on.includes(state.activeChannel)) state.activeChannel = on[0];
  plot.active = state.activeChannel;
}

function renderChannel(ch: ChannelSettings): void {
  const panel = document.getElementById(`ch${ch.channel}`);
  if (!panel) return;
  const field = <T extends HTMLElement>(f: string) => panel.querySelector<T>(`[data-f="${f}"]`)!;
  panel.classList.toggle("off", !ch.enabled);
  panel.classList.toggle("active", ch.enabled && ch.channel === state.activeChannel);
  field<HTMLInputElement>("enabled").checked = ch.enabled;
  field("bwl").classList.toggle("on", ch.bandwidthLimit);
  field("inv").classList.toggle("on", ch.inverted);
  setSelect(field("unit"), ch.unit);
  setInput(field("skew"), ch.skew * 1e9);
  field("offset-unit").textContent = ch.unit;
  // Re-format when the unit changes (dataset.key only tracks the values).
  const vdiv = field<HTMLSelectElement>("voltsPerDiv");
  if (vdiv.dataset.unit !== ch.unit) {
    vdiv.dataset.key = "";
    vdiv.dataset.unit = ch.unit;
  }
  fillSelect(vdiv, voltDivs(ch.probe), (v) => si(v, ch.unit), ch.voltsPerDiv);
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
/** Start x of a zoom drag on an empty part of the plot. */
let zoomFrom: number | null = null;

function canvasPoint(e: MouseEvent): [number, number] {
  const r = canvas.getBoundingClientRect();
  return [e.clientX - r.left, e.clientY - r.top];
}

/** Sets (or clears) the zoom window, keeping it inside the record and above ~20 samples. */
function setZoom(view: { start: number; span: number } | null): void {
  const record = plot.recordWindow(state.frame);
  if (view && record && state.frame) {
    const minSpan = 20 / state.frame.sampleRate;
    const span = Math.min(record.span, Math.max(minSpan, view.span));
    const start = Math.min(record.start + record.span - span, Math.max(record.start, view.start));
    view = span >= record.span * 0.999 ? null : { start, span };
  }
  plot.view = view;
  plot.clearPersistence();
  renderZoomInfo();
  render();
}

function renderZoomInfo(): void {
  const record = plot.recordWindow(state.frame);
  const zoomed = !!(plot.view && record);
  $("zoom-info").hidden = $("zoom-reset").hidden = !zoomed;
  if (zoomed) {
    $("zoom-info").textContent = `Zoom ×${(record!.span / plot.view!.span).toPrecision(3)}  ·  ${si(plot.view!.span / 14, "s")}/div`;
  }
}

canvas.addEventListener("pointerdown", (e) => {
  if (e.button !== 0 || plot.xy) return;
  const [x, y] = canvasPoint(e);
  dragging = plot.hitCursor(x, y);
  if (!dragging && state.frame) zoomFrom = x;
  canvas.setPointerCapture(e.pointerId);
});
canvas.addEventListener("pointermove", (e) => {
  const [x, y] = canvasPoint(e);
  if (dragging) {
    plot.moveCursor(dragging, x, y);
  } else if (zoomFrom !== null) {
    plot.selection = [zoomFrom, x];
  } else {
    const hit = plot.hitCursor(x, y);
    canvas.style.cursor = hit ? (hit.kind === "t" ? "ew-resize" : "ns-resize") : "crosshair";
  }
  plot.hoverX = x;
  render();
});
canvas.addEventListener("pointerup", (e) => {
  const [x] = canvasPoint(e);
  if (zoomFrom !== null && Math.abs(x - zoomFrom) > 6) {
    const t0 = plot.timeAtX(Math.min(zoomFrom, x));
    const t1 = plot.timeAtX(Math.max(zoomFrom, x));
    setZoom({ start: t0, span: t1 - t0 });
  }
  // A moved time cursor moves the measurement gate on the scope too.
  if (dragging?.kind === "t" && state.gate) syncScopeGate().catch(handleError);
  dragging = null;
  zoomFrom = null;
  plot.selection = null;
  render();
});
canvas.addEventListener("pointerleave", () => {
  if (dragging || zoomFrom !== null) return;
  plot.hoverX = null;
  render();
});
canvas.addEventListener("dblclick", () => setZoom(null));
canvas.addEventListener(
  "wheel",
  (e) => {
    const record = plot.recordWindow(state.frame);
    if (!record || plot.xy) return;
    e.preventDefault();
    // Zoom around the time under the mouse.
    const [x] = canvasPoint(e);
    const t = plot.timeAtX(x);
    const current = plot.view ?? record;
    const factor = e.deltaY < 0 ? 1 / 1.25 : 1.25;
    const span = current.span * factor;
    setZoom({ start: t - ((t - current.start) / current.span) * span, span });
  },
  { passive: false },
);
$("zoom-reset").addEventListener("click", () => setZoom(null));
new ResizeObserver(() => {
  plot.resize();
  plot.clearPersistence();
  render();
}).observe($("plot-wrap"));

// ---------- XY and persistence ----------

$("xy-btn").addEventListener("click", () => {
  plot.xy = !plot.xy;
  $("xy-btn").classList.toggle("on", plot.xy);
  if (plot.xy) setZoom(null);
  plot.clearPersistence();
  render();
});
$<HTMLSelectElement>("persist").addEventListener("change", (e) => {
  plot.persistence = Number((e.target as HTMLSelectElement).value);
  plot.clearPersistence();
  render();
});

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
  const f = displayFrame();
  if (!f || f.channels.length === 0) return toast("No waveform to export.", "info");
  const len = Math.max(...f.channels.map((c) => c.volts.length));
  const base = f.channels[0];
  const rows = [["time_s", ...f.channels.map((c) => `${channelName(c.channel)}_${unitOf(c) || "ratio"}`)].join(",")];
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

// ---------- virtual front panel ----------

type PanelKey = [label: string, key: number, title?: string];

/** Key codes of $$SY_FP (from klumw/sdsremote, checked on an SDS1202X-E); knobs turn with ±1, push with 0. */
const PANEL_GROUPS: { title: string; keys: PanelKey[]; knobs?: PanelKey[] }[] = [
  {
    title: "Menu",
    keys: [["Cursors", 22], ["Acquire", 27], ["Save/Recall", 28], ["Measure", 26], ["Display", 23], ["Utility", 24],
      ["Clear sweeps", 47], ["History", 48], ["Decode", 29]],
    knobs: [["Adjust", 15, "Universal knob: changes the selected menu item, push to select"]],
  },
  { title: "Run control", keys: [["Auto setup", 11], ["Run/Stop", 12]] },
  {
    title: "Vertical",
    keys: [["CH1", 39, "Selects channel 1; pressed again switches it off"], ["CH2", 40, "Selects channel 2; pressed again switches it off"],
      ["Math", 31, "The scope's math menu; pressed again switches the scope's math display off"], ["Ref", 32]],
    knobs: [["CH1 V/div", 35, "Push: coarse/fine"], ["CH1 position", 43, "Push: zero"], ["CH2 V/div", 36, "Push: coarse/fine"], ["CH2 position", 44, "Push: zero"]],
  },
  { title: "Horizontal", keys: [["Roll", 49]], knobs: [["Time/div", 7], ["Position", 10, "Push: zero delay"]] },
  { title: "Trigger", keys: [["Setup", 18], ["Auto", 17], ["Normal", 19], ["Single", 20]], knobs: [["Level", 16, "Push: 50 %"]] },
];

const fp = { open: false, queue: Promise.resolve(), pending: 0, shotBusy: false, shotAgain: false, timer: 0, url: "" };

function buildFrontPanel(): void {
  const soft = $("fp-softkeys");
  for (let i = 1; i <= 6; i++) soft.append(panelButton("", i, `Soft key ${i} (below the scope's menu item)`));
  soft.append(panelButton("Menu", 0, "Shows or hides the scope's soft-key menu"));
  const controls = $("fp-controls");
  for (const g of PANEL_GROUPS) {
    const section = document.createElement("section");
    section.className = "fp-group";
    section.innerHTML = `<h3>${g.title}</h3>`;
    const keys = document.createElement("div");
    keys.className = "fp-keys";
    for (const [label, key, title] of g.keys) keys.append(panelButton(label, key, title));
    section.append(keys);
    for (const [label, key, title] of g.knobs ?? []) section.append(panelKnob(label, key, title));
    controls.append(section);
  }
}

function panelButton(label: string, key: number, title?: string): HTMLButtonElement {
  const b = document.createElement("button");
  b.textContent = label;
  if (title) b.title = title;
  b.addEventListener("click", () => pressPanel(key, 1));
  return b;
}

/** A knob: turn left/right one step, push in the middle, or use the mouse wheel over it. */
function panelKnob(label: string, key: number, title?: string): HTMLElement {
  const knob = document.createElement("div");
  knob.className = "fp-knob";
  knob.title = `${title ? `${title}. ` : ""}Mouse wheel turns the knob.`;
  for (const [text, value, tip] of [["⟲", -1, "Turn left"], [label, 0, "Push"], ["⟳", 1, "Turn right"]] as const) {
    const b = document.createElement("button");
    b.textContent = text;
    b.title = tip;
    if (value === 0) b.className = "push";
    b.addEventListener("click", () => pressPanel(key, value));
    knob.append(b);
  }
  knob.addEventListener(
    "wheel",
    (e) => {
      e.preventDefault();
      pressPanel(key, e.deltaY < 0 ? 1 : -1);
    },
    { passive: false },
  );
  return knob;
}

/** Sends key presses in order; once the last one is done, the screen and the settings are refreshed. */
function pressPanel(key: number, value: number): void {
  if (!state.connected) return;
  fp.pending++;
  fp.queue = fp.queue.then(async () => {
    try {
      await api.panel(key, value);
    } catch (e) {
      handleError(e);
    } finally {
      fp.pending--;
    }
    if (fp.pending === 0) {
      // The scope needs a moment to draw the new menu.
      setTimeout(refreshPanelScreen, 300);
      await refreshSettings().catch(handleError);
    }
  });
}

async function refreshPanelScreen(): Promise<void> {
  if (!fp.open || !state.connected) return;
  if (fp.shotBusy) {
    fp.shotAgain = true;
    return;
  }
  fp.shotBusy = true;
  try {
    const bmp = await api.screenshot();
    URL.revokeObjectURL(fp.url);
    fp.url = URL.createObjectURL(bmp);
    $<HTMLImageElement>("fp-img").src = fp.url;
    $("fp-status").textContent = "";
  } catch (e) {
    $("fp-status").textContent = e instanceof Error ? e.message : String(e);
  } finally {
    fp.shotBusy = false;
    if (fp.shotAgain) {
      fp.shotAgain = false;
      void refreshPanelScreen();
    }
  }
}

function schedulePanelRefresh(): void {
  clearInterval(fp.timer);
  const ms = Number($<HTMLSelectElement>("fp-rate").value);
  if (fp.open && ms > 0) fp.timer = window.setInterval(refreshPanelScreen, ms);
}

function setPanelOpen(open: boolean): void {
  if (open && !state.connected) return toast("Connect to the oscilloscope first.");
  fp.open = open;
  $("panel-btn").classList.toggle("on", open);
  const dialog = $<HTMLDialogElement>("panel-dialog");
  if (open && !dialog.open) dialog.show();
  if (!open && dialog.open) dialog.close();
  schedulePanelRefresh();
  if (open) void refreshPanelScreen();
}

buildFrontPanel();
$("panel-btn").addEventListener("click", () => setPanelOpen(!fp.open));
$("fp-close").addEventListener("click", () => setPanelOpen(false));
$("panel-dialog").addEventListener("close", () => fp.open && setPanelOpen(false));
$("fp-rate").addEventListener("change", schedulePanelRefresh);

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

// ---------- self-update (Windows desktop app) ----------

const UPDATE_PHASES: Record<string, string> = {
  starting: "Starting…",
  downloading: "Downloading",
  verifying: "Checking the download…",
  installing: "Installing…",
  restarting: "Restarting ScopeDeck…",
};

$("update-later").addEventListener("click", () => $<HTMLDialogElement>("update-dialog").close());
$("update-now").addEventListener("click", async () => {
  const button = $<HTMLButtonElement>("update-now");
  button.disabled = true;
  $("update-later").hidden = true;
  $("update-note").hidden = true;
  $("update-progress").hidden = false;
  try {
    let p = await api.installUpdate();
    // The window closes by itself when the new version starts.
    while (p.phase !== "failed") {
      const label = UPDATE_PHASES[p.phase] ?? p.phase;
      $("update-phase").textContent = p.phase === "downloading" ? `${label} ${p.percent} %` : label;
      $<HTMLProgressElement>("update-bar").value = p.phase === "downloading" ? p.percent : 100;
      if (p.phase === "restarting") return;
      await sleep(300);
      p = await api.updateProgress();
    }
    throw new Error(p.error ?? "The update failed.");
  } catch (e) {
    $("update-progress").hidden = true;
    $("update-note").hidden = false;
    $("update-note").textContent = `${e instanceof Error ? e.message : String(e)} Nothing was changed; you can try again or update manually.`;
    button.disabled = false;
    $("update-later").hidden = false;
  }
});

// ---------- startup ----------

(async () => {
  plot.resize();
  render();
  try {
    const status = await api.status();
    $("version").textContent = status.version ? `v${status.version}` : "";
    // Runs in the background; nothing is shown when offline or up to date.
    api.update().then((u) => {
      if (!u.available || !u.url) return;
      const badge = $<HTMLAnchorElement>("update-badge");
      badge.href = u.url;
      badge.textContent = `Update available: v${u.latest}`;
      badge.title = `ScopeDeck v${u.latest} is available (you have v${u.current}).`;
      badge.hidden = false;
      badge.addEventListener("click", (e) => {
        e.preventDefault();
        $("update-text").textContent = `ScopeDeck v${u.latest} is available. You have v${u.current}.`;
        $<HTMLAnchorElement>("update-notes").href = u.url!;
        $("update-now").hidden = !u.canInstall;
        $("update-note").hidden = u.canInstall;
        $("update-note").textContent = u.whyNot ?? "";
        $<HTMLDialogElement>("update-dialog").showModal();
      });
    }, () => {});
    $<HTMLInputElement>("host").value = status.host;
    if (status.connected && status.identity) await onConnected(status.identity.model, status.identity.firmware);
  } catch (e) {
    handleError(e);
  }
})();
