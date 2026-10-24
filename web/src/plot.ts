import type { ChannelWaveform, ScopeSettings, WaveformFrame } from "./api";
import { si } from "./format";

export const CHANNEL_COLORS = ["#f5d742", "#e85ce8", "#4fc3f7", "#5ee08a"];
export const MATH_COLOR = "#e8edf2";
/** Reference traces R1–R4 (channel numbers 101–104): muted, distinct from the live channels. */
export const REF_BASE = 100;
const REF_COLORS = ["#8fb8de", "#d9a37e", "#9ccf9a", "#c7a3d6"];

export const isRef = (ch: number) => ch > REF_BASE;
/** Channel 0 is the app-side math trace; 101–104 are references R1–R4. */
export const channelColor = (ch: number) =>
  ch === 0 ? MATH_COLOR : isRef(ch) ? REF_COLORS[(ch - REF_BASE - 1) % REF_COLORS.length] : CHANNEL_COLORS[ch - 1];
export const channelName = (ch: number) => (ch === 0 ? "M" : isRef(ch) ? `R${ch - REF_BASE}` : `C${ch}`);

export const H_DIVS = 14;
export const V_DIVS = 8;
const GRID = "rgba(150, 160, 175, 0.22)";
const GRID_AXIS = "rgba(150, 160, 175, 0.45)";
const TRIGGER = "#ff8a3d";
const CURSOR_T = "#7fdcff";
const BACKGROUND = "#07090c";

export type CursorMode = "off" | "time" | "volt" | "both";

/** Cursor positions in physical units, so they stay put while frames update. */
export interface Cursors {
  mode: CursorMode;
  t: [number, number];
  v: [number, number];
  /** Trace the voltage cursors refer to. */
  ref: number;
}

export interface CursorHit {
  kind: "t" | "v";
  index: 0 | 1;
}

/** Visible time window; null shows the whole record (the scope's screen). */
export interface TimeView {
  start: number;
  span: number;
}

/**
 * Canvas rendering of a 14×8 division scope graticule with traces, markers and cursors, plus an XY mode,
 * persistence (afterglow) and an app-side horizontal zoom.
 */
export class ScopePlot {
  private readonly ctx: CanvasRenderingContext2D;
  private readonly glow = document.createElement("canvas");
  private readonly glowCtx = this.glow.getContext("2d")!;
  private width = 0;
  private height = 0;
  private dpr = 1;
  private frame: WaveformFrame | null = null;
  private glowAt = 0;

  hoverX: number | null = null;
  cursors: Cursors = { mode: "off", t: [0, 0], v: [0, 0], ref: 1 };
  /** Zoom window; null = the full record. */
  view: TimeView | null = null;
  /** Pixel range being dragged out for a zoom, drawn as a shaded band. */
  selection: [number, number] | null = null;
  /** XY mode: plots the first trace (X) against the second (Y). */
  xy = false;
  /** Persistence decay time in ms; 0 = off, Infinity = infinite. */
  persistence = 0;

  constructor(private readonly canvas: HTMLCanvasElement) {
    this.ctx = canvas.getContext("2d")!;
  }

  /** Plot area in CSS pixels, leaving room for side markers. */
  get area() {
    const pad = 14;
    return { x: pad, y: 8, w: this.width - pad * 2, h: this.height - 16 };
  }

  resize(): void {
    const rect = this.canvas.getBoundingClientRect();
    this.dpr = window.devicePixelRatio || 1;
    this.width = rect.width;
    this.height = rect.height;
    for (const c of [this.canvas, this.glow]) {
      c.width = Math.round(rect.width * this.dpr);
      c.height = Math.round(rect.height * this.dpr);
    }
    this.ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
    this.glowCtx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
  }

  // ---------- coordinate mapping (valid after a frame has been drawn) ----------

  /** The whole record: the scope's 14 divisions. */
  recordWindow(frame = this.frame): TimeView | null {
    if (!frame) return null;
    return { start: frame.triggerDelay - (frame.timePerDiv * H_DIVS) / 2, span: frame.timePerDiv * H_DIVS };
  }

  private get window(): TimeView {
    return this.view ?? this.recordWindow()!;
  }

  xOfTime(t: number): number {
    const a = this.area;
    const w = this.window;
    return a.x + ((t - w.start) / w.span) * a.w;
  }

  timeAtX(x: number): number {
    const a = this.area;
    const w = this.window;
    return w.start + ((x - a.x) / a.w) * w.span;
  }

  /** Vertical scale of a trace: its own V/div and offset. */
  private scaleOf(channel: number): { vdiv: number; offset: number } | null {
    const wf = this.frame?.channels.find((c) => c.channel === channel);
    return wf ? { vdiv: wf.voltsPerDiv, offset: wf.offset } : null;
  }

  yOfVolt(v: number, channel: number): number | null {
    const s = this.scaleOf(channel);
    if (!s) return null;
    const a = this.area;
    return a.y + a.h / 2 - ((v + s.offset) / s.vdiv) * (a.h / V_DIVS);
  }

  voltAtY(y: number, channel: number): number | null {
    const s = this.scaleOf(channel);
    if (!s) return null;
    const a = this.area;
    return ((a.y + a.h / 2 - y) / (a.h / V_DIVS)) * s.vdiv - s.offset;
  }

  /** Sample value of a trace at time t (nearest sample), or undefined outside its record. */
  valueAt(channel: number, t: number): number | undefined {
    const wf = this.frame?.channels.find((c) => c.channel === channel);
    if (!wf) return undefined;
    return wf.volts[Math.round((t - wf.firstPointTime) / wf.sampleInterval)];
  }

  /** Places cursors at ±3 div (time) and ±2 div (voltage) around the view center. */
  resetCursors(): void {
    const f = this.frame;
    if (!f) return;
    const w = this.window;
    const center = w.start + w.span / 2;
    this.cursors.t = [center - (3 * w.span) / H_DIVS, center + (3 * w.span) / H_DIVS];
    const ref = f.channels.find((c) => c.channel === this.cursors.ref) ?? f.channels[0];
    if (ref) {
      this.cursors.ref = ref.channel;
      this.cursors.v = [2 * ref.voltsPerDiv - ref.offset, -2 * ref.voltsPerDiv - ref.offset];
    }
  }

  hitCursor(x: number, y: number): CursorHit | null {
    if (!this.frame || this.xy) return null;
    const { mode, t, v, ref } = this.cursors;
    const tol = 6;
    if (mode === "time" || mode === "both") {
      for (const i of [0, 1] as const) if (Math.abs(this.xOfTime(t[i]) - x) <= tol) return { kind: "t", index: i };
    }
    if (mode === "volt" || mode === "both") {
      for (const i of [0, 1] as const) {
        const cy = this.yOfVolt(v[i], ref);
        if (cy !== null && Math.abs(cy - y) <= tol) return { kind: "v", index: i };
      }
    }
    return null;
  }

  moveCursor(hit: CursorHit, x: number, y: number): void {
    if (hit.kind === "t") this.cursors.t[hit.index] = this.timeAtX(x);
    else {
      const v = this.voltAtY(y, this.cursors.ref);
      if (v !== null) this.cursors.v[hit.index] = v;
    }
  }

  // ---------- persistence ----------

  clearPersistence(): void {
    this.glowCtx.clearRect(0, 0, this.width, this.height);
    this.glowAt = 0;
  }

  /** Adds a newly acquired frame to the afterglow; older frames fade with the decay time. */
  accumulate(frame: WaveformFrame): void {
    if (!this.persistence) return;
    const now = performance.now();
    const g = this.glowCtx;
    if (this.glowAt && Number.isFinite(this.persistence)) {
      const fade = 1 - Math.exp(-(now - this.glowAt) / this.persistence);
      g.globalCompositeOperation = "destination-out";
      g.fillStyle = `rgba(0,0,0,${fade})`;
      g.fillRect(0, 0, this.width, this.height);
      g.globalCompositeOperation = "source-over";
    }
    this.glowAt = now;
    const previous = this.frame;
    this.frame = frame; // the mapping functions use the frame being drawn
    g.save();
    const a = this.area;
    g.beginPath();
    g.rect(a.x, a.y, a.w, a.h);
    g.clip();
    g.globalAlpha = 0.35;
    if (this.xy) this.traceXY(g, frame);
    else for (const wf of frame.channels) if (!isRef(wf.channel)) this.trace(g, wf, 1);
    g.restore();
    this.frame = previous;
  }

  // ---------- drawing ----------

  draw(frame: WaveformFrame | null, settings: ScopeSettings | null): void {
    const { ctx } = this;
    const a = this.area;
    this.frame = frame;
    ctx.clearRect(0, 0, this.width, this.height);
    this.drawGrid();
    if (!frame) return;

    if (this.persistence) {
      ctx.save();
      ctx.setTransform(1, 0, 0, 1, 0, 0);
      ctx.drawImage(this.glow, 0, 0);
      ctx.restore();
    }

    if (this.xy) {
      ctx.save();
      ctx.beginPath();
      ctx.rect(a.x, a.y, a.w, a.h);
      ctx.clip();
      this.traceXY(ctx, frame);
      ctx.restore();
      return;
    }

    ctx.save();
    ctx.beginPath();
    ctx.rect(a.x, a.y, a.w, a.h);
    ctx.clip();
    // References first, so live traces are drawn on top of them.
    for (const wf of [...frame.channels].sort((p, q) => Number(isRef(q.channel)) - Number(isRef(p.channel)))) {
      this.trace(ctx, wf, isRef(wf.channel) ? 0.8 : 1);
    }
    ctx.restore();

    // Ground markers on the left edge (channels and math).
    for (const wf of frame.channels) {
      if (isRef(wf.channel)) continue;
      const y = this.yOfVolt(0, wf.channel)!;
      this.marker(a.x - 1, clamp(y, a.y, a.y + a.h), "right", channelColor(wf.channel), wf.channel === 0 ? "M" : String(wf.channel));
    }

    // Trigger position (top) and level (right edge).
    const tx = this.xOfTime(0);
    if (tx >= a.x && tx <= a.x + a.w) this.marker(tx, a.y - 1, "down", TRIGGER, "");
    const trig = settings?.trigger;
    const src = trig?.source.match(/^C(\d)$/);
    if (trig?.level != null && src) {
      const y = this.yOfVolt(trig.level, Number(src[1]));
      if (y !== null && y >= a.y && y <= a.y + a.h) {
        this.marker(a.x + a.w + 1, y, "left", TRIGGER, "");
        this.hline(y, "rgba(255, 138, 61, 0.35)", [4, 4]);
      }
    }

    this.drawCursors();

    if (this.selection) {
      const [x0, x1] = this.selection;
      ctx.fillStyle = "rgba(79, 163, 255, 0.15)";
      ctx.fillRect(Math.min(x0, x1), a.y, Math.abs(x1 - x0), a.h);
      ctx.strokeStyle = "rgba(79, 163, 255, 0.7)";
      ctx.strokeRect(Math.min(x0, x1) + 0.5, a.y + 0.5, Math.abs(x1 - x0), a.h - 1);
    }

    if (this.hoverX !== null && this.hoverX >= a.x && this.hoverX <= a.x + a.w) {
      ctx.strokeStyle = "rgba(255,255,255,0.35)";
      ctx.beginPath();
      ctx.moveTo(this.hoverX + 0.5, a.y);
      ctx.lineTo(this.hoverX + 0.5, a.y + a.h);
      ctx.stroke();
    }
  }

  /** Draws one trace; only the visible samples are stroked, so zoomed views stay fast. */
  private trace(ctx: CanvasRenderingContext2D, wf: ChannelWaveform, alpha: number): void {
    const a = this.area;
    const color = channelColor(wf.channel);
    const v = wf.volts;
    const w = this.window;
    const first = Math.max(0, Math.floor((w.start - wf.firstPointTime) / wf.sampleInterval) - 1);
    const last = Math.min(v.length - 1, Math.ceil((w.start + w.span - wf.firstPointTime) / wf.sampleInterval) + 1);
    if (last <= first) return;
    ctx.save();
    ctx.globalAlpha *= alpha;
    ctx.strokeStyle = color;
    ctx.lineWidth = isRef(wf.channel) ? 1 : 1.25;
    if (!isRef(wf.channel)) {
      ctx.shadowColor = color;
      ctx.shadowBlur = 3;
    }
    const scale = a.h / V_DIVS / wf.voltsPerDiv;
    const cy = a.y + a.h / 2;
    ctx.beginPath();
    for (let i = first; i <= last; i++) {
      const x = this.xOfTime(wf.firstPointTime + i * wf.sampleInterval);
      const y = cy - (v[i] + wf.offset) * scale;
      if (i === first) ctx.moveTo(x, y);
      else ctx.lineTo(x, y);
    }
    ctx.stroke();
    ctx.restore();
  }

  /** XY: first two live traces, each on its own scale; a square of 8×8 divisions in the middle. */
  private traceXY(ctx: CanvasRenderingContext2D, frame: WaveformFrame): void {
    const live = frame.channels.filter((c) => !isRef(c.channel));
    if (live.length < 2) return;
    const [xw, yw] = live;
    const a = this.area;
    const div = a.h / V_DIVS;
    const cx = a.x + a.w / 2;
    const cy = a.y + a.h / 2;
    const n = Math.min(xw.volts.length, yw.volts.length);
    ctx.save();
    ctx.strokeStyle = channelColor(yw.channel);
    ctx.lineWidth = 1.25;
    ctx.beginPath();
    for (let i = 0; i < n; i++) {
      const x = cx + ((xw.volts[i] + xw.offset) / xw.voltsPerDiv) * div;
      const y = cy - ((yw.volts[i] + yw.offset) / yw.voltsPerDiv) * div;
      if (i === 0) ctx.moveTo(x, y);
      else ctx.lineTo(x, y);
    }
    ctx.stroke();
    ctx.restore();
  }

  private drawCursors(): void {
    const { mode, t, v, ref } = this.cursors;
    const a = this.area;
    const { ctx } = this;
    ctx.font = "11px system-ui, sans-serif";
    if (mode === "time" || mode === "both") {
      t.forEach((time, i) => {
        const x = Math.round(this.xOfTime(time)) + 0.5;
        if (x < a.x || x > a.x + a.w) return;
        ctx.strokeStyle = CURSOR_T;
        ctx.setLineDash([6, 3]);
        ctx.beginPath();
        ctx.moveTo(x, a.y);
        ctx.lineTo(x, a.y + a.h);
        ctx.stroke();
        ctx.setLineDash([]);
        this.tag(`X${i + 1}`, x + 3, a.y + a.h - 18, CURSOR_T);
      });
    }
    if (mode === "volt" || mode === "both") {
      const color = channelColor(ref);
      v.forEach((volt, i) => {
        const y = this.yOfVolt(volt, ref);
        if (y === null || y < a.y || y > a.y + a.h) return;
        this.hline(Math.round(y) + 0.5, color, [6, 3]);
        this.tag(`Y${i + 1}`, a.x + a.w - 26, y - 17, color);
      });
    }
  }

  private tag(text: string, x: number, y: number, color: string): void {
    const { ctx } = this;
    ctx.fillStyle = "rgba(7, 9, 12, 0.85)";
    ctx.fillRect(x, y, 22, 15);
    ctx.fillStyle = color;
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    ctx.fillText(text, x + 11, y + 8);
  }

  private hline(y: number, color: string, dash: number[]): void {
    const { ctx } = this;
    const a = this.area;
    ctx.strokeStyle = color;
    ctx.setLineDash(dash);
    ctx.beginPath();
    ctx.moveTo(a.x, y);
    ctx.lineTo(a.x + a.w, y);
    ctx.stroke();
    ctx.setLineDash([]);
  }

  /** Time and per-trace value at the hover position. */
  readout(): { time: string; values: { channel: number; text: string }[] } | null {
    if (this.hoverX === null || !this.frame || this.xy) return null;
    const t = this.timeAtX(this.hoverX);
    const values = this.frame.channels.map((wf) => {
      const v = this.valueAt(wf.channel, t);
      return { channel: wf.channel, text: v === undefined ? "—" : si(v, wf.unit ?? "V", 4) };
    });
    return { time: si(t, "s", 4), values };
  }

  private drawGrid(): void {
    const { ctx } = this;
    const a = this.area;
    ctx.fillStyle = BACKGROUND;
    ctx.fillRect(a.x, a.y, a.w, a.h);
    ctx.lineWidth = 1;
    // XY mode uses a square 8×8 grid; time mode 14×8.
    const cols = this.xy ? V_DIVS : H_DIVS;
    const gw = this.xy ? (a.h / V_DIVS) * V_DIVS : a.w;
    const gx = this.xy ? a.x + (a.w - gw) / 2 : a.x;
    for (let i = 0; i <= cols; i++) {
      const x = Math.round(gx + (i * gw) / cols) + 0.5;
      ctx.strokeStyle = i === cols / 2 ? GRID_AXIS : GRID;
      ctx.beginPath();
      ctx.moveTo(x, a.y);
      ctx.lineTo(x, a.y + a.h);
      ctx.stroke();
    }
    for (let j = 0; j <= V_DIVS; j++) {
      const y = Math.round(a.y + (j * a.h) / V_DIVS) + 0.5;
      ctx.strokeStyle = j === V_DIVS / 2 ? GRID_AXIS : GRID;
      ctx.beginPath();
      ctx.moveTo(gx, y);
      ctx.lineTo(gx + gw, y);
      ctx.stroke();
    }
    // Minor ticks on the center axes.
    ctx.strokeStyle = GRID_AXIS;
    const cx = Math.round(gx + gw / 2) + 0.5;
    const cy = Math.round(a.y + a.h / 2) + 0.5;
    ctx.beginPath();
    for (let i = 0; i < cols * 5; i++) {
      const x = Math.round(gx + (i * gw) / (cols * 5)) + 0.5;
      ctx.moveTo(x, cy - 3);
      ctx.lineTo(x, cy + 3);
    }
    for (let j = 0; j < V_DIVS * 5; j++) {
      const y = Math.round(a.y + (j * a.h) / (V_DIVS * 5)) + 0.5;
      ctx.moveTo(cx - 3, y);
      ctx.lineTo(cx + 3, y);
    }
    ctx.stroke();
  }

  private marker(x: number, y: number, dir: "right" | "left" | "down", color: string, label: string): void {
    const { ctx } = this;
    const s = 7;
    ctx.fillStyle = color;
    ctx.beginPath();
    if (dir === "right") {
      ctx.moveTo(x - 2 * s + 2, y - s);
      ctx.lineTo(x - 4, y - s);
      ctx.lineTo(x, y);
      ctx.lineTo(x - 4, y + s);
      ctx.lineTo(x - 2 * s + 2, y + s);
    } else if (dir === "left") {
      ctx.moveTo(x + s + 2, y - s);
      ctx.lineTo(x + 4, y - s);
      ctx.lineTo(x, y);
      ctx.lineTo(x + 4, y + s);
      ctx.lineTo(x + s + 2, y + s);
    } else {
      ctx.moveTo(x - s, y - s);
      ctx.lineTo(x + s, y - s);
      ctx.lineTo(x, y + 1);
    }
    ctx.closePath();
    ctx.fill();
    if (label) {
      ctx.fillStyle = BACKGROUND;
      ctx.font = "bold 10px system-ui, sans-serif";
      ctx.textAlign = "center";
      ctx.textBaseline = "middle";
      ctx.fillText(label, x - s + 1, y + 0.5);
    }
  }
}

function clamp(v: number, lo: number, hi: number): number {
  return Math.min(hi, Math.max(lo, v));
}
