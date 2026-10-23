const PREFIXES: [number, string][] = [
  [1e9, "G"],
  [1e6, "M"],
  [1e3, "k"],
  [1, ""],
  [1e-3, "m"],
  [1e-6, "µ"],
  [1e-9, "n"],
  [1e-12, "p"],
];

/** Engineering notation: 0.0002 → "200 µV". */
export function si(value: number, unit: string, digits = 3): string {
  if (!Number.isFinite(value)) return "—";
  if (value === 0) return `0 ${unit}`;
  const abs = Math.abs(value);
  const [scale, prefix] = PREFIXES.find(([s]) => abs >= s * 0.9995) ?? PREFIXES[PREFIXES.length - 1];
  const scaled = value / scale;
  return `${Number(scaled.toPrecision(digits))} ${prefix}${unit}`;
}

/** 1-2-5 sequence between min and max inclusive. */
export function series125(min: number, max: number): number[] {
  const out: number[] = [];
  for (let decade = 1e-12; decade <= max; decade *= 10) {
    for (const m of [1, 2, 5]) {
      const v = Number((m * decade).toPrecision(3));
      if (v >= min * 0.999 && v <= max * 1.001) out.push(v);
    }
  }
  return out;
}
