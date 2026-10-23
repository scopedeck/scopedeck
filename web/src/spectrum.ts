import type { Spectrum } from "./fft";
import { si } from "./format";
import { channelColor } from "./plot";

const X_DIVS = 10;
const Y_DIVS = 10;
const DB_PER_DIV = 10;

/** Canvas rendering of an amplitude spectrum: linear frequency, 10 dB/div. */
export class SpectrumPlot {
  private readonly ctx: CanvasRenderingContext2D;
  private width = 0;
  private height = 0;
  private fMax = 1;
  private top = 0;
  hoverX: number | null = null;

  constructor(private readonly canvas: HTMLCanvasElement) {
    this.ctx = canvas.getContext("2d")!;
  }

  private get area() {
    return { x: 54, y: 8, w: this.width - 64, h: this.height - 30 };
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

  /** @param span Fraction of the Nyquist frequency to show (1 = full). */
  draw(s: Spectrum | null, span: number): void {
    const { ctx } = this;
    const a = this.area;
    ctx.clearRect(0, 0, this.width, this.height);
    ctx.fillStyle = "#07090c";
    ctx.fillRect(a.x, a.y, a.w, a.h);
    if (!s) return;

    this.fMax = s.df * s.dbv.length * span;
    // Top of the scale: 10 dB above the strongest component, rounded to 10 dB.
    let max = -Infinity;
    for (let k = 1; k < s.dbv.length; k++) if (s.dbv[k] > max) max = s.dbv[k];
    this.top = Math.ceil((max + 5) / DB_PER_DIV) * DB_PER_DIV;

    ctx.lineWidth = 1;
    ctx.font = "11px system-ui, sans-serif";
    ctx.fillStyle = "#8591a0";
    for (let i = 0; i <= X_DIVS; i++) {
      const x = Math.round(a.x + (i * a.w) / X_DIVS) + 0.5;
      ctx.strokeStyle = "rgba(150, 160, 175, 0.22)";
      ctx.beginPath();
      ctx.moveTo(x, a.y);
      ctx.lineTo(x, a.y + a.h);
      ctx.stroke();
      ctx.textAlign = i === 0 ? "left" : i === X_DIVS ? "right" : "center";
      ctx.textBaseline = "top";
      ctx.fillText(si((this.fMax * i) / X_DIVS, "Hz"), x, a.y + a.h + 6);
    }
    for (let j = 0; j <= Y_DIVS; j++) {
      const y = Math.round(a.y + (j * a.h) / Y_DIVS) + 0.5;
      ctx.strokeStyle = "rgba(150, 160, 175, 0.22)";
      ctx.beginPath();
      ctx.moveTo(a.x, y);
      ctx.lineTo(a.x + a.w, y);
      ctx.stroke();
      ctx.textAlign = "right";
      ctx.textBaseline = "middle";
      ctx.fillText(`${this.top - j * DB_PER_DIV}`, a.x - 6, y);
    }
    ctx.save();
    ctx.translate(12, a.y + a.h / 2);
    ctx.rotate(-Math.PI / 2);
    ctx.textAlign = "center";
    ctx.fillText("dBV", 0, 0);
    ctx.restore();

    ctx.save();
    ctx.beginPath();
    ctx.rect(a.x, a.y, a.w, a.h);
    ctx.clip();
    const color = channelColor(s.channel);
    ctx.strokeStyle = color;
    ctx.fillStyle = color + "22";
    const bins = Math.min(s.dbv.length, Math.ceil(this.fMax / s.df) + 1);
    // Draw the per-pixel maximum so narrow peaks survive when there are more bins than pixels.
    const columns = Math.max(1, Math.floor(a.w));
    ctx.beginPath();
    ctx.moveTo(a.x, a.y + a.h);
    let k = 0;
    for (let px = 0; px <= columns; px++) {
      const kEnd = Math.min(bins, Math.floor(((px + 1) / columns) * (this.fMax / s.df)) + 1);
      let m = -Infinity;
      for (; k < kEnd; k++) if (s.dbv[k] > m) m = s.dbv[k];
      if (m === -Infinity) continue;
      ctx.lineTo(a.x + px * (a.w / columns), this.yOf(m));
    }
    ctx.lineTo(a.x + a.w, a.y + a.h);
    ctx.fill();
    ctx.stroke();

    if (s.peak && s.peak.frequency <= this.fMax) {
      const x = this.xOf(s.peak.frequency);
      const y = this.yOf(s.peak.dbv);
      ctx.fillStyle = "#ff8a3d";
      ctx.beginPath();
      ctx.moveTo(x, y - 3);
      ctx.lineTo(x - 5, y - 11);
      ctx.lineTo(x + 5, y - 11);
      ctx.fill();
    }
    if (this.hoverX !== null && this.hoverX >= a.x && this.hoverX <= a.x + a.w) {
      ctx.strokeStyle = "rgba(255,255,255,0.35)";
      ctx.beginPath();
      ctx.moveTo(this.hoverX + 0.5, a.y);
      ctx.lineTo(this.hoverX + 0.5, a.y + a.h);
      ctx.stroke();
    }
    ctx.restore();
  }

  /** Frequency and level of the strongest bin near the hover position (±3 px). */
  readout(s: Spectrum): string | null {
    if (this.hoverX === null) return null;
    const a = this.area;
    const f0 = ((this.hoverX - 3 - a.x) / a.w) * this.fMax;
    const f1 = ((this.hoverX + 3 - a.x) / a.w) * this.fMax;
    let best = -1;
    for (let k = Math.max(0, Math.floor(f0 / s.df)); k <= Math.min(s.dbv.length - 1, Math.ceil(f1 / s.df)); k++) {
      if (best < 0 || s.dbv[k] > s.dbv[best]) best = k;
    }
    return best < 0 ? null : `${si(best * s.df, "Hz", 5)}  ${s.dbv[best].toFixed(1)} dBV`;
  }

  private xOf(f: number): number {
    const a = this.area;
    return a.x + (f / this.fMax) * a.w;
  }

  private yOf(db: number): number {
    const a = this.area;
    return a.y + ((this.top - db) / (DB_PER_DIV * Y_DIVS)) * a.h;
  }
}
