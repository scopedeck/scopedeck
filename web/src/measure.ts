import type { ChannelWaveform } from "./api";

export interface Measurements {
  max: number;
  min: number;
  pkpk: number;
  mean: number;
  rms: number;
  frequency: number;
  duty: number;
}

/** Basic measurements computed from the transferred (possibly decimated) samples. */
export function measure(wf: ChannelWaveform): Measurements {
  const v = wf.volts;
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
  // Below ~0.3 div peak-to-peak the trace is mostly noise; crossings would give nonsense frequencies.
  const hasSignal = pkpk > wf.voltsPerDiv * 0.3;
  if (rising.length >= 2 && hasSignal) {
    const periods = rising.length - 1;
    const span = (rising[rising.length - 1] - rising[0]) * wf.sampleInterval;
    frequency = periods / span;
    let hi = 0;
    for (let i = rising[0]; i < rising[rising.length - 1]; i++) if (v[i] > mid) hi++;
    duty = hi / (rising[rising.length - 1] - rising[0]);
  } else if (hasSignal) {
    duty = highSamples / n;
  }

  return { max, min, pkpk, mean: sum / n, rms: Math.sqrt(sumSq / n), frequency, duty };
}
