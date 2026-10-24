import "./style.css";
import { api, ApiError, type ChannelSettings, type HistoryState, type ScopeSettings, type TriggerSettings, type WaveformFrame } from "./api";
import { series125, si } from "./format";
import { spectrum, WINDOWS, type Spectrum, type WindowName } from "./fft";
import { computeMath, MATH_CHANNEL, MATH_OPS, type MathOp, type MathSettings } from "./math";
import { measure } from "./measure";
import { channelColor, channelName, ScopePlot, type CursorHit, type CursorMode } from "./plot";
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
  fftOn: false,
  spectrum: null as Spectrum | null,
  /** Measurement log rows while logging, otherwise null. */
  log: null as string[] | null,
  math: null as MathSettings | null,
  historyPlaying: false,
};

/** The scope's frame plus the app-side math trace (channel 0) when math is on. */
function displayFrame(): WaveformFrame | null {
  const f = state.frame;
  if (!f || !state.math) return f;
  const m = computeMath(f, state.math);
  return m ? { ...f, channels: [...f.channels, m] } : f;
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
  for (const wf of f.channels) {
    let cells: string[];
    if (scope && wf.channel !== MATH_CHANNEL) {
      const m = state.scopeMeasurements.get(wf.channel);
      cells = SCOPE_COLUMNS.map(([key, , fmt]) => (m?.[key] == null ? "—" : fmt(m[key]!)));
    } else if (scope) {
      cells = SCOPE_COLUMNS.map(() => "—");
    } else {
      const m = measure(wf);
      cells = APP_COLUMNS.map(([, fn]) => fn(m, unitOf(wf)));
    }
    const row = body.insertRow();
    row.innerHTML = `<th style="color:${channelColor(wf.channel)}">${channelName(wf.channel)}</th>${cells.map((c) => `<td>${c}</td>`).join("")}`;
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
  const traces = [...Array.from({ length: state.channelCount }, (_, i) => i + 1), ...(state.math ? [MATH_CHANNEL] : [])];
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
  render();
});
for (const id of ["math-op", "math-a", "math-b", "math-scale", "math-offset"]) $(id).addEventListener("change", updateMath);

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
  state.frame = await api.frame(Number($<HTMLSelectElement>("points").value));
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
  if (t.slope) setSelect($("trig-slope"), t.slope);
  if (t.coupling) setSelect($("trig-coupling"), t.coupling);
  setInput($("trig-level"), t.level);
  $("trig-level-label").textContent = t.type === "SLEW" || t.type === "RUNT" ? "High level" : "Level";
  $("trig-level2-row").hidden = !(t.type === "SLEW" || t.type === "RUNT");
  setInput($("trig-level2"), t.level2);
  $("set50-btn").hidden = t.type !== "EDGE";

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
      <h2><label class="switch"><input type="checkbox" data-f="enabled" /><span></span></label> Channel ${n}
        <button class="bw-btn" data-f="bwl" title="20 MHz bandwidth limit">BW 20M</button></h2>
      <div class="row"><label>Volts/div</label>
        <div class="stepper">
          <button data-dir="-1" aria-label="More sensitive">‹</button>
          <select data-f="voltsPerDiv"></select>
          <button data-dir="1" aria-label="Less sensitive">›</button>
        </div></div>
      <div class="row"><label>Offset</label><div class="unit-input"><input type="number" step="any" data-f="offset" /><span>V</span></div></div>
      <div class="row"><label>Input</label>
        <div class="pair">
          <select data-f="coupling" title="Coupling"><option value="D1M">DC</option><option value="A1M">AC</option><option value="GND">GND</option></select>
          <select data-f="probe" title="Probe attenuation"></select>
        </div></div>`;
    container.append(panel);

    const field = <T extends HTMLElement>(f: string) => panel.querySelector<T>(`[data-f="${f}"]`)!;
    fillSelect(field("probe"), PROBES, (v) => `${v}×`);
    field<HTMLButtonElement>("bwl").addEventListener("click", (e) => {
      e.preventDefault();
      const ch = state.settings?.channels.find((c) => c.channel === n);
      if (ch) act(() => api.channel(n, { bandwidthLimit: !ch.bandwidthLimit }));
    });
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
  field("bwl").classList.toggle("on", ch.bandwidthLimit);
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
    $<HTMLInputElement>("host").value = status.host;
    if (status.connected && status.identity) await onConnected(status.identity.model, status.identity.firmware);
  } catch (e) {
    handleError(e);
  }
})();
