import type { ChannelWaveform } from "./api";

export interface Measurements {
  max: number;
  min: number;
  pkpk: number;
  mean: number;
  rms: number;
  frequency: number;
  duty: number;
  /** Too few samples per cycle: frequency, period and duty are not reported. */
  aliased: boolean;
}

/**
 * Measurements of a trace: the server's, made on the densely read record (and gate window), when present;
 * otherwise (math, references) computed here from the transferred samples within the window.
 */
export function measure(wf: ChannelWaveform, window: [number, number] | null = null): Measurements {
  const s = wf.stats;
  if (s) {
    return {
      max: s.max,
      min: s.min,
      pkpk: s.max - s.min,
      mean: s.mean,
      rms: s.rms,
      frequency: s.frequency ?? NaN,
      duty: s.duty ?? NaN,
      aliased: s.aliased,
    };
  }
  if (window) {
    const i0 = Math.max(0, Math.ceil((window[0] - wf.firstPointTime) / wf.sampleInterval));
    const i1 = Math.min(wf.volts.length, Math.floor((window[1] - wf.firstPointTime) / wf.sampleInterval) + 1);
    wf = { ...wf, firstPointTime: wf.firstPointTime + i0 * wf.sampleInterval, volts: wf.volts.slice(i0, Math.max(i0, i1)) };
  }
  const v = wf.volts;
  if (v.length === 0) return { max: NaN, min: NaN, pkpk: NaN, mean: NaN, rms: NaN, frequency: NaN, duty: NaN, aliased: false };
  let max = -Infinity;
  let min = Infinity;
  let sum = 0;
  let sumSq = 0;
  for (const x of v) {
    if (x > max) max = x;
    if (x < min) min = x;
    sum += x;
    sumSq += x * x;
  }
  const n = v.length || 1;
  const pkpk = max - min;

  // Rising crossings of the mid level with 10 % hysteresis.
  const mid = (max + min) / 2;
  const hyst = pkpk * 0.1;
  const rising: number[] = [];
  let high = v[0] > mid;
  let highSamples = 0;
  for (let i = 1; i < v.length; i++) {
    if (!high && v[i] > mid + hyst) {
      high = true;
      rising.push(i);
    } else if (high && v[i] < mid - hyst) {
      high = false;
    }
    if (v[i] > mid) highSamples++;
  }
  let frequency = NaN;
  let duty = NaN;
  let aliased = false;
  // Below ~0.3 div peak-to-peak the trace is mostly noise; crossings would give nonsense frequencies.
  const hasSignal = pkpk > wf.voltsPerDiv * 0.3;
  if (rising.length >= 2 && hasSignal) {
    const periods = rising.length - 1;
    const span = (rising[rising.length - 1] - rising[0]) * wf.sampleInterval;
    frequency = periods / span;
    let hi = 0;
    for (let i = rising[0]; i < rising[rising.length - 1]; i++) if (v[i] > mid) hi++;
    duty = hi / (rising[rising.length - 1] - rising[0]);
    // With fewer than ~6 transferred samples per cycle the waveform is aliased (e.g. 1 kHz at 100 ms/div
    // shows up as a few hundred Hz): report nothing rather than a wrong number.
    if (1 / (frequency * wf.sampleInterval) < 6) {
      aliased = true;
      frequency = NaN;
      duty = NaN;
    }
  } else if (hasSignal) {
    duty = highSamples / n;
  }

  return { max, min, pkpk, mean: sum / n, rms: Math.sqrt(sumSq / n), frequency, duty, aliased };
}

/**
 * Delay of channel B's rising edges after channel A's (the nearest B edge to each A edge, averaged) and the
 * phase in degrees (−180…180] at A's frequency. One edge each is enough for the delay; the phase needs A's
 * frequency (two edges). Null when either channel has no edges.
 */
export function delayBetween(a: ChannelWaveform, b: ChannelWaveform): { delay: number; phase: number | null } | null {
  const ea = a.stats?.risingEdges ?? [];
  const eb = b.stats?.risingEdges ?? [];
  const f = a.stats?.frequency;
  if (ea.length === 0 || eb.length === 0) return null;
  const period = f ? 1 / f : Infinity;
  const deltas: number[] = [];
  let j = 0;
  for (const t of ea) {
    while (j + 1 < eb.length && Math.abs(eb[j + 1] - t) <= Math.abs(eb[j] - t)) j++;
    const d = eb[j] - t;
    if (Math.abs(d) <= period / 2) deltas.push(d);
  }
  if (deltas.length === 0) return null;
  const delay = deltas.reduce((s, d) => s + d, 0) / deltas.length;
  if (!f) return { delay, phase: null };
  let phase = ((delay / period) * 360) % 360;
  if (phase > 180) phase -= 360;
  if (phase <= -180) phase += 360;
  return { delay, phase };
}
