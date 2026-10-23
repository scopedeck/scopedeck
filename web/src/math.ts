import type { ChannelWaveform, WaveformFrame } from "./api";

/** Channel number used for the math trace in a display frame. */
export const MATH_CHANNEL = 0;

export type MathOp = "add" | "sub" | "mul" | "div" | "diff" | "integ" | "sqrt";

export const MATH_OPS: Record<MathOp, { label: string; binary: boolean; unit: string }> = {
  add: { label: "A + B", binary: true, unit: "V" },
  sub: { label: "A − B", binary: true, unit: "V" },
  mul: { label: "A × B", binary: true, unit: "V²" },
  div: { label: "A ÷ B", binary: true, unit: "" },
  diff: { label: "d/dt A", binary: false, unit: "V/s" },
  integ: { label: "∫ A dt", binary: false, unit: "Vs" },
  sqrt: { label: "√A", binary: false, unit: "√V" },
};

export interface MathSettings {
  op: MathOp;
  a: number;
  b: number;
  /** Units per division; null picks a 1-2-5 value that fits the trace in about 6 divisions. */
  scale: number | null;
  /** Value at the screen center line. */
  offset: number;
}

/**
 * Computes the math trace from the transferred samples of a frame. Both channels share the time base
 * (same timebase and decimation), so samples are combined index by index. Returns null if a source is off.
 */
export function computeMath(frame: WaveformFrame, s: MathSettings): ChannelWaveform | null {
  const a = frame.channels.find((c) => c.channel === s.a);
  const b = frame.channels.find((c) => c.channel === s.b);
  const def = MATH_OPS[s.op];
  if (!a || (def.binary && !b)) return null;

  const n = def.binary ? Math.min(a.volts.length, b!.volts.length) : a.volts.length;
  const out = new Float32Array(n);
  const x = a.volts;
  const y = b?.volts ?? x;
  const dt = a.sampleInterval;
  switch (s.op) {
    case "add": for (let i = 0; i < n; i++) out[i] = x[i] + y[i]; break;
    case "sub": for (let i = 0; i < n; i++) out[i] = x[i] - y[i]; break;
    case "mul": for (let i = 0; i < n; i++) out[i] = x[i] * y[i]; break;
    case "div": for (let i = 0; i < n; i++) out[i] = y[i] === 0 ? NaN : x[i] / y[i]; break;
    case "sqrt": for (let i = 0; i < n; i++) out[i] = x[i] < 0 ? NaN : Math.sqrt(x[i]); break;
    case "diff": {
      // Central difference over ±k samples (about 1 % of the record): differencing neighbours
      // would amplify ADC noise far above the signal's own slope.
      const k = Math.max(1, Math.round(n / 200));
      for (let i = 0; i < n; i++) {
        const lo = Math.max(0, i - k);
        const hi = Math.min(n - 1, i + k);
        out[i] = hi > lo ? (x[hi] - x[lo]) / ((hi - lo) * dt) : 0;
      }
      break;
    }
    case "integ": {
      // Trapezoidal running integral from the first sample.
      let sum = 0;
      out[0] = 0;
      for (let i = 1; i < n; i++) {
        sum += ((x[i] + x[i - 1]) / 2) * dt;
        out[i] = sum;
      }
      break;
    }
  }

  return {
    channel: MATH_CHANNEL,
    voltsPerDiv: s.scale ?? autoScale(out),
    // The plot draws (v + offset) / div around the center; the math "offset" is the value at the center.
    offset: -s.offset,
    firstPointTime: a.firstPointTime,
    sampleInterval: dt,
    memoryPoints: a.memoryPoints,
    volts: out,
    unit: def.unit,
  };
}

/** Smallest 1-2-5 step that shows the trace's peak-to-peak span in about six divisions. */
function autoScale(v: Float32Array): number {
  let lo = Infinity;
  let hi = -Infinity;
  for (const x of v) {
    if (!Number.isFinite(x)) continue;
    if (x < lo) lo = x;
    if (x > hi) hi = x;
  }
  const span = Math.max(hi - lo, Math.abs(hi), Math.abs(lo), 1e-12);
  const target = span / 6;
  const decade = 10 ** Math.floor(Math.log10(target));
  for (const m of [1, 2, 5, 10]) if (m * decade >= target) return m * decade;
  return 10 * decade;
}
