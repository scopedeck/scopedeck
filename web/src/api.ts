export interface ScopeIdentity {
  manufacturer: string;
  model: string;
  serial: string;
  firmware: string;
}

export interface Status {
  version: string;
  connected: boolean;
  host: string;
  port: number;
  identity: ScopeIdentity | null;
  channelCount: number | null;
}

export interface ChannelSettings {
  channel: number;
  enabled: boolean;
  voltsPerDiv: number;
  offset: number;
  coupling: string;
  probe: number;
  bandwidthLimit: boolean;
  inverted: boolean;
  /** V or A */
  unit: string;
  /** Channel-to-channel skew in seconds (±100 ns). */
  skew: number;
}

/**
 * type: EDGE, GLIT (pulse), SLEW, INTV (interval), RUNT, DROP (dropout), WIN (window), PA (pattern) or TV.
 * holdType: OFF/TI (edge holdoff, dropout), PS/PL/P2/P1 (pulse, runt), IS/IL/I2/I1 (slope, interval).
 */
export interface TriggerSettings {
  mode: string;
  type: string;
  source: string;
  slope: string | null;
  level: number | null;
  coupling: string | null;
  holdType: string | null;
  holdValue: number | null;
  holdValue2: number | null;
  level2: number | null;
  video: { standard: string; sync: string; line: number | null; field: number | null } | null;
  /** states per channel: H, L or X. The thresholds are set on the scope. */
  pattern: { states: Record<string, string>; logic: string } | null;
}

export interface AcquisitionSettings {
  /** SAMPLING, PEAK_DETECT, AVERAGE or HIGH_RES */
  mode: string;
  averages: number;
  memoryDepth: string;
  sinXInterpolation: boolean;
}

export interface HistoryState {
  enabled: boolean;
  frame: number;
  frameCount: number;
  frameTime: string | null;
}

export interface ScopeSettings {
  timePerDiv: number;
  triggerDelay: number;
  sampleRate: number;
  status: string;
  acquisition: AcquisitionSettings;
  trigger: TriggerSettings;
  channels: ChannelSettings[];
  history: HistoryState;
}

/** Channel 0 is the app-side math trace, which also carries its unit. */
export interface ChannelWaveform {
  channel: number;
  voltsPerDiv: number;
  offset: number;
  firstPointTime: number;
  sampleInterval: number;
  memoryPoints: number;
  volts: number[] | Float32Array;
  unit?: string;
  /** Min/max envelope per sample interval (when the record was read denser than the returned points). */
  min?: number[] | null;
  max?: number[] | null;
  /** Measurements on the densely read record (or the gate window), computed by the server. */
  stats?: WaveformStats | null;
}

export interface WaveformStats {
  max: number;
  min: number;
  mean: number;
  rms: number;
  frequency: number | null;
  duty: number | null;
  aliased: boolean;
  /** Times of rising mid-level crossings, for delay and phase between channels. */
  risingEdges: number[];
}

export interface WaveformFrame {
  timePerDiv: number;
  triggerDelay: number;
  sampleRate: number;
  channels: ChannelWaveform[];
}

export type ChannelUpdate = Partial<Omit<ChannelSettings, "channel">>;
export type TriggerUpdate = Partial<Omit<TriggerSettings, "level" | "holdValue" | "holdValue2" | "level2" | "video" | "pattern">> & {
  level?: number;
  holdValue?: number;
  holdValue2?: number;
  level2?: number;
  videoStandard?: string;
  videoSync?: string;
  videoLine?: number;
  videoField?: number;
  patternStates?: Record<string, string>;
  patternLogic?: string;
};
export type AcquisitionUpdate = Partial<AcquisitionSettings>;

/** phase: idle, starting, downloading, verifying, installing, restarting or failed. */
export interface UpdateProgress {
  phase: string;
  percent: number;
  error: string | null;
}

export class ApiError extends Error {
  constructor(message: string, readonly status: number) {
    super(message);
  }
}

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  const raw = body instanceof Blob;
  const res = await fetch(url, {
    method,
    headers: body === undefined ? undefined : { "Content-Type": raw ? "application/octet-stream" : "application/json" },
    body: body === undefined ? undefined : raw ? body : JSON.stringify(body),
  });
  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`;
    try {
      const problem = await res.json();
      message = problem.detail ?? problem.title ?? message;
    } catch {
      /* not JSON */
    }
    throw new ApiError(message, res.status);
  }
  if (res.status === 204) return undefined as T;
  const type = res.headers.get("Content-Type") ?? "";
  return (type.includes("json") ? res.json() : res.blob()) as Promise<T>;
}

export const api = {
  status: () => request<Status>("GET", "/api/status"),
  /** Result of the server's once-per-run check for a newer GitHub release. */
  update: () =>
    request<{ current: string; latest: string | null; url: string | null; available: boolean; canInstall: boolean; whyNot: string | null }>(
      "GET",
      "/api/update",
    ),
  /** Starts downloading and installing the update (Windows desktop app only). */
  installUpdate: () => request<UpdateProgress>("POST", "/api/update/install"),
  updateProgress: () => request<UpdateProgress>("GET", "/api/update/progress"),
  connect: (host: string) => request<ScopeIdentity>("POST", "/api/connect", { host }),
  disconnect: () => request<void>("POST", "/api/disconnect"),
  settings: () => request<ScopeSettings>("GET", "/api/settings"),
  /** start/end: part of the record to read (0–1); a narrow range is read at full resolution (zoom). */
  /** gate: time window (s, relative to the trigger) the server's measurements cover. */
  frame: (maxPoints: number, start = 0, end = 1, gate: [number, number] | null = null) =>
    request<WaveformFrame>(
      "GET",
      `/api/frame?maxPoints=${maxPoints}${start > 0 || end < 1 ? `&start=${start}&end=${end}` : ""}${gate ? `&gateStart=${gate[0]}&gateEnd=${gate[1]}` : ""}`,
    ),
  /** Limits the scope's own measurements to a time window; null switches the gate off. */
  gate: (window: [number, number] | null) => request<void>("POST", "/api/gate", window ? { start: window[0], end: window[1] } : {}),
  delay: (a: number, b: number) => request<Record<string, number | null>>("GET", `/api/delay?a=${a}&b=${b}`),
  screenshot: () => request<Blob>("GET", "/api/screenshot"),
  /** The scope's own measurements (PAVA? ALL); null where the scope reports none. */
  measurements: (n: number) => request<Record<string, number | null>>("GET", `/api/measurements/${n}`),
  /** Full-memory capture as CSV; a plain URL so the browser streams the large file to disk. */
  captureUrl: "/api/capture",
  channel: (n: number, update: ChannelUpdate) => request<void>("POST", `/api/channel/${n}`, update),
  timebase: (update: { timePerDiv?: number; triggerDelay?: number }) => request<void>("POST", "/api/timebase", update),
  acquisition: (update: AcquisitionUpdate) => request<void>("POST", "/api/acquisition", update),
  trigger: (update: TriggerUpdate) => request<void>("POST", "/api/trigger", update),
  set50: () => request<void>("POST", "/api/trigger/set50"),
  autoSetup: () => request<void>("POST", "/api/autosetup"),
  history: (enabled: boolean) => request<HistoryState>("POST", "/api/history", { enabled }),
  historyFrame: (frame: number) => request<HistoryState>("POST", "/api/history/frame", { frame }),
  historyExportUrl: (maxPoints: number) => `/api/history/export?maxPoints=${maxPoints}`,
  setupUrl: "/api/setup",
  loadSetup: (file: Blob) => request<void>("POST", "/api/setup", file),
  saveMemory: (slot: number) => request<void>("POST", `/api/setup/memory/${slot}/save`),
  recallMemory: (slot: number) => request<void>("POST", `/api/setup/memory/${slot}/recall`),
  scpi: (command: string) => request<{ response: string | null }>("POST", "/api/scpi", { command }),
};
