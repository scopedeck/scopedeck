import type { ChannelWaveform } from "./api";

export type WindowName = "hann" | "hamming" | "blackman-harris" | "flattop" | "rect";

export const WINDOWS: Record<WindowName, string> = {
  hann: "Hann",
  hamming: "Hamming",
  "blackman-harris": "Blackman-Harris",
  flattop: "Flat top (amplitude)",
  rect: "Rectangular",
};

function windowValue(name: WindowName, i: number, n: number): number {
  const x = (2 * Math.PI * i) / (n - 1);
  switch (name) {
    case "hann":
      return 0.5 - 0.5 * Math.cos(x);
    case "hamming":
      return 0.54 - 0.46 * Math.cos(x);
    case "blackman-harris":
      return 0.35875 - 0.48829 * Math.cos(x) + 0.14128 * Math.cos(2 * x) - 0.01168 * Math.cos(3 * x);
    case "flattop":
      return 0.21557895 - 0.41663158 * Math.cos(x) + 0.277263158 * Math.cos(2 * x) - 0.083578947 * Math.cos(3 * x) + 0.006947368 * Math.cos(4 * x);
    case "rect":
      return 1;
  }
}

/** In-place iterative radix-2 FFT; re/im length must be a power of two. */
export function fft(re: Float64Array, im: Float64Array): void {
  const n = re.length;
  for (let i = 1, j = 0; i < n; i++) {
    let bit = n >> 1;
    for (; j & bit; bit >>= 1) j ^= bit;
    j ^= bit;
    if (i < j) {
      [re[i], re[j]] = [re[j], re[i]];
      [im[i], im[j]] = [im[j], im[i]];
    }
  }
  for (let len = 2; len <= n; len <<= 1) {
    const ang = (-2 * Math.PI) / len;
    const wr = Math.cos(ang);
    const wi = Math.sin(ang);
    for (let i = 0; i < n; i += len) {
      let cr = 1;
      let ci = 0;
      for (let k = 0; k < len / 2; k++) {
        const a = i + k;
        const b = a + len / 2;
        const tr = re[b] * cr - im[b] * ci;
        const ti = re[b] * ci + im[b] * cr;
        re[b] = re[a] - tr;
        im[b] = im[a] - ti;
        re[a] += tr;
        im[a] += ti;
        const ncr = cr * wr - ci * wi;
        ci = cr * wi + ci * wr;
        cr = ncr;
      }
    }
  }
}

export interface Spectrum {
  channel: number;
  /** Frequency spacing between bins in Hz. */
  df: number;
  /** RMS amplitude per bin in dBV (0 dBV = 1 Vrms). */
  dbv: Float64Array;
  peak: { frequency: number; dbv: number } | null;
}

/**
 * Amplitude spectrum of the transferred samples. The record is windowed, zero-padded to a power of two
 * and normalized by the window sum, so a sine reads its RMS value regardless of length or window.
 */
export function spectrum(wf: ChannelWaveform, windowName: WindowName): Spectrum {
  const len = wf.volts.length;
  let n = 1;
  while (n < len) n <<= 1;
  const re = new Float64Array(n);
  const im = new Float64Array(n);
  let mean = 0;
  for (const v of wf.volts) mean += v;
  mean /= len || 1;
  let wsum = 0;
  for (let i = 0; i < len; i++) {
    const w = windowValue(windowName, i, len);
    wsum += w;
    // DC is removed before windowing so its leakage does not mask low frequencies; bin 0 is restored below.
    re[i] = (wf.volts[i] - mean) * w;
  }
  fft(re, im);

  const bins = n / 2;
  const dbv = new Float64Array(bins);
  for (let k = 0; k < bins; k++) {
    const amplitude = ((k === 0 ? 1 : 2) * Math.hypot(re[k], im[k])) / wsum;
    const rms = k === 0 ? Math.abs(mean) : amplitude / Math.SQRT2;
    dbv[k] = 20 * Math.log10(Math.max(rms, 1e-9));
  }
  const df = 1 / (n * wf.sampleInterval);

  let peak: Spectrum["peak"] = null;
  for (let k = 2; k < bins; k++) {
    if (!peak || dbv[k] > peak.dbv) peak = { frequency: k * df, dbv: dbv[k] };
  }
  return { channel: wf.channel, df, dbv, peak };
}
