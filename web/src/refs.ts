import type { ChannelWaveform } from "./api";
import { REF_BASE } from "./plot";

export const MAX_REFS = 4;

export interface Reference {
  /** Channel number in display frames: 101–104 for R1–R4. */
  id: number;
  /** Where it came from: "C1 at 14:03:21" or a file name. */
  label: string;
  visible: boolean;
  wave: ChannelWaveform;
}

/** Reference traces: frozen copies of a trace, or columns loaded from a CSV export. */
export class References {
  readonly items: Reference[] = [];

  private freeId(): number | null {
    for (let n = 1; n <= MAX_REFS; n++) if (!this.items.some((r) => r.id === REF_BASE + n)) return REF_BASE + n;
    return null;
  }

  /** Freezes a copy of a trace (with its scale at that moment). Returns null when all four slots are used. */
  save(wave: ChannelWaveform, label: string): Reference | null {
    const id = this.freeId();
    if (id === null) return null;
    const ref: Reference = { id, label, visible: true, wave: { ...wave, channel: id, volts: Float32Array.from(wave.volts) } };
    this.items.push(ref);
    this.items.sort((a, b) => a.id - b.id);
    return ref;
  }

  remove(id: number): void {
    const i = this.items.findIndex((r) => r.id === id);
    if (i >= 0) this.items.splice(i, 1);
  }

  visible(): ChannelWaveform[] {
    return this.items.filter((r) => r.visible).map((r) => r.wave);
  }

  /**
   * Loads every value column of a CSV with a time column first ("time_s,C1_V,C2_V", as exported by ScopeDeck)
   * into free slots, scaled to fit about six divisions. Returns the number of references added.
   */
  loadCsv(text: string, fileName: string): number {
    const lines = text.split(/\r?\n/).filter((l) => l.trim());
    if (lines.length < 3) throw new Error("The file has no data rows.");
    const header = lines[0].split(",").map((h) => h.trim());
    const rows = lines.slice(1).map((l) => l.split(",").map(Number));
    const times = rows.map((r) => r[0]);
    if (times.some((t) => !Number.isFinite(t))) throw new Error("The first column must be time in seconds.");
    const dt = (times[times.length - 1] - times[0]) / (times.length - 1);
    if (!(dt > 0)) throw new Error("The time column must increase.");

    let added = 0;
    for (let col = 1; col < header.length; col++) {
      const volts = Float32Array.from(rows, (r) => r[col]);
      if (!volts.some(Number.isFinite)) continue;
      const unit = header[col].split("_").pop() || "V";
      const { scale, center } = fit(volts);
      const ref = this.save(
        { channel: 0, voltsPerDiv: scale, offset: -center, firstPointTime: times[0], sampleInterval: dt, memoryPoints: volts.length, volts, unit },
        `${header[col]} from ${fileName}`,
      );
      if (!ref) break;
      added++;
    }
    if (added === 0) throw new Error(this.freeId() === null ? "All four reference slots are in use." : "No value columns found.");
    return added;
  }
}

/** 1-2-5 scale and center value that show a trace in about six divisions. */
function fit(v: Float32Array): { scale: number; center: number } {
  let lo = Infinity;
  let hi = -Infinity;
  for (const x of v) {
    if (!Number.isFinite(x)) continue;
    if (x < lo) lo = x;
    if (x > hi) hi = x;
  }
  const target = Math.max(hi - lo, 1e-12) / 6;
  const decade = 10 ** Math.floor(Math.log10(target));
  const scale = [1, 2, 5, 10].map((m) => m * decade).find((s) => s >= target) ?? 10 * decade;
  return { scale, center: (hi + lo) / 2 };
}
