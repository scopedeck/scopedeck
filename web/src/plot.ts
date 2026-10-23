import type { ScopeSettings, WaveformFrame } from "./api";
import { si } from "./format";

export const CHANNEL_COLORS = ["#f5d742", "#e85ce8", "#4fc3f7", "#5ee08a"];
export const MATH_COLOR = "#e8edf2";

/** Channel 0 is the app-side math trace. */
export const channelColor = (ch: number) => (ch === 0 ? MATH_COLOR : CHANNEL_COLORS[ch - 1]);
export const channelName = (ch: number) => (ch === 0 ? "M" : `C${ch}`);
export const H_DIVS = 14;
export const V_DIVS = 8;
const GRID = "rgba(150, 160, 175, 0.22)";
const GRID_AXIS = "rgba(150, 160, 175, 0.45)";
const TRIGGER = "#ff8a3d";
const CURSOR_T = "#7fdcff";

export type CursorMode = "off" | "time" | "volt" | "both";

/** Cursor positions in physical units, so they stay put while frames update. */
export interface Cursors {
  mode: CursorMode;
  t: [number, number];
  v: [number, number];
  /** Channel the voltage cursors refer to. */
  ref: number;
}

export interface CursorHit {
  kind: "t" | "v";
  index: 0 | 1;
}

/** Canvas rendering of a 14×8 division scope graticule with traces, markers and cursors. */
export class ScopePlot {
  private readonly ctx: CanvasRenderingContext2D;
  private width = 0;
  private height = 0;
  private frame: WaveformFrame | null = null;
  hoverX: number | null = null;
  cursors: Cursors = { mode: "off", t: [0, 0], v: [0, 0], ref: 1 };

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
    const dpr = window.devicePixelRatio || 1;
    this.width = rect.width;
    this.height = rect.height;
    this.canvas.width = Math.round(rect.width * dpr);
    this.canvas.height = Math.round(rect.height * dpr);
    this.ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  }

  // ---------- coordinate mapping (valid after a frame has been drawn) ----------

  private get tLeft(): number {
    const f = this.frame!;
    return f.triggerDelay - (f.timePerDiv * H_DIVS) / 2;
  }

  xOfTime(t: number): number {
    const a = this.area;
    return a.x + ((t - this.tLeft) / this.frame!.timePerDiv) * (a.w / H_DIVS);
  }

  timeAtX(x: number): number {
    const a = this.area;
    return this.tLeft + ((x - a.x) / a.w) * H_DIVS * this.frame!.timePerDiv;
  }

  /** Vertical scale of a channel: its own V/div and offset. */
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

  /** Sample value of a channel at time t (nearest sample), or undefined outside the record. */
  valueAt(channel: number, t: number): number | undefined {
    const wf = this.frame?.channels.find((c) => c.channel === channel);
    if (!wf) return undefined;
    return wf.volts[Math.round((t - wf.firstPointTime) / wf.sampleInterval)];
  }

  /** Places cursors at ±3 div (time) and ±2 div (voltage) around the screen center. */
  resetCursors(): void {
    const f = this.frame;
    if (!f) return;
    this.cursors.t = [f.triggerDelay - 3 * f.timePerDiv, f.triggerDelay + 3 * f.timePerDiv];
    const ref = f.channels.find((c) => c.channel === this.cursors.ref) ?? f.channels[0];
    if (ref) {
      this.cursors.ref = ref.channel;
      this.cursors.v = [2 * ref.voltsPerDiv - ref.offset, -2 * ref.voltsPerDiv - ref.offset];
    }
  }

  hitCursor(x: number, y: number): CursorHit | null {
    if (!this.frame) return null;
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

  // ---------- drawing ----------

  draw(frame: WaveformFrame | null, settings: ScopeSettings | null): void {
    const { ctx } = this;
    const a = this.area;
    this.frame = frame;
    ctx.clearRect(0, 0, this.width, this.height);
    this.drawGrid();
    if (!frame) return;

    ctx.save();
    ctx.beginPath();
    ctx.rect(a.x, a.y, a.w, a.h);
    ctx.clip();
    for (const wf of frame.channels) {
      const color = channelColor(wf.channel);
      ctx.strokeStyle = color;
      ctx.lineWidth = 1.25;
      ctx.shadowColor = color;
      ctx.shadowBlur = 3;
      ctx.beginPath();
      const v = wf.volts;
      for (let i = 0; i < v.length; i++) {
        const x = this.xOfTime(wf.firstPointTime + i * wf.sampleInterval);
        const y = this.yOfVolt(v[i], wf.channel)!;
        if (i === 0) ctx.moveTo(x, y);
        else ctx.lineTo(x, y);
      }
      ctx.stroke();
    }
    ctx.restore();

    // Ground markers on the left edge.
    for (const wf of frame.channels) {
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

    if (this.hoverX !== null && this.hoverX >= a.x && this.hoverX <= a.x + a.w) {
      ctx.strokeStyle = "rgba(255,255,255,0.35)";
      ctx.beginPath();
      ctx.moveTo(this.hoverX + 0.5, a.y);
      ctx.lineTo(this.hoverX + 0.5, a.y + a.h);
      ctx.stroke();
    }
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

  /** Time and per-channel voltage at the hover position. */
  readout(): { time: string; values: { channel: number; text: string }[] } | null {
    if (this.hoverX === null || !this.frame) return null;
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
    ctx.fillStyle = "#07090c";
    ctx.fillRect(a.x, a.y, a.w, a.h);
    ctx.lineWidth = 1;
    for (let i = 0; i <= H_DIVS; i++) {
      const x = Math.round(a.x + (i * a.w) / H_DIVS) + 0.5;
      ctx.strokeStyle = i === H_DIVS / 2 ? GRID_AXIS : GRID;
      ctx.beginPath();
      ctx.moveTo(x, a.y);
      ctx.lineTo(x, a.y + a.h);
      ctx.stroke();
    }
    for (let j = 0; j <= V_DIVS; j++) {
      const y = Math.round(a.y + (j * a.h) / V_DIVS) + 0.5;
      ctx.strokeStyle = j === V_DIVS / 2 ? GRID_AXIS : GRID;
      ctx.beginPath();
      ctx.moveTo(a.x, y);
      ctx.lineTo(a.x + a.w, y);
      ctx.stroke();
    }
    // Minor ticks on the center axes.
    ctx.strokeStyle = GRID_AXIS;
    const cx = Math.round(a.x + a.w / 2) + 0.5;
    const cy = Math.round(a.y + a.h / 2) + 0.5;
    ctx.beginPath();
    for (let i = 0; i < H_DIVS * 5; i++) {
      const x = Math.round(a.x + (i * a.w) / (H_DIVS * 5)) + 0.5;
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
      ctx.fillStyle = "#07090c";
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
