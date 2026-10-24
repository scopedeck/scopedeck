import type { ChannelWaveform, WaveformFrame } from "./api";
import { H_DIVS } from "./plot";

/**
 * The pass/fail mask as the app draws it, in screen divisions like the scope's own mask: upper and lower edge
 * (divisions from the center line) at n points spread evenly across the 14 horizontal divisions. NaN where the
 * waveform had no samples. The scope does not report its mask, so this is rebuilt from the same waveform.
 */
export interface Mask {
  channel: number;
  upper: Float32Array;
  lower: Float32Array;
}

/**
 * The band the waveform may move in: around each point, the waveform's extremes within ±xDiv, widened by yDiv.
 * Uses the min/max envelope when the frame has one, so peaks between the transferred points are covered.
 */
export function buildMask(wf: ChannelWaveform, frame: WaveformFrame, xDiv: number, yDiv: number, n = 560): Mask {
  const hi = wf.max ?? wf.volts;
  const lo = wf.min ?? wf.volts;
  const last = wf.volts.length - 1;
  const tdiv = frame.timePerDiv;
  const start = frame.triggerDelay - (H_DIVS / 2) * tdiv;
  const toDiv = (v: number) => (v + wf.offset) / wf.voltsPerDiv;
  const upper = new Float32Array(n).fill(NaN);
  const lower = new Float32Array(n).fill(NaN);
  for (let i = 0; i < n; i++) {
    const center = start + ((i + 0.5) / n) * H_DIVS * tdiv;
    const j0 = Math.max(0, Math.ceil((center - xDiv * tdiv - wf.firstPointTime) / wf.sampleInterval));
    const j1 = Math.min(last, Math.floor((center + xDiv * tdiv - wf.firstPointTime) / wf.sampleInterval));
    if (j1 < j0) continue;
    let max = -Infinity;
    let min = Infinity;
    for (let j = j0; j <= j1; j++) {
      if (hi[j] > max) max = hi[j];
      if (lo[j] < min) min = lo[j];
    }
    upper[i] = toDiv(max) + yDiv;
    lower[i] = toDiv(min) - yDiv;
  }
  return { channel: wf.channel, upper, lower };
}
