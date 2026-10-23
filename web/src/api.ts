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
}

/**
 * type: EDGE, GLIT (pulse), SLEW, INTV (interval), RUNT, DROP (dropout) or TV.
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
}

export interface WaveformFrame {
  timePerDiv: number;
  triggerDelay: number;
  sampleRate: number;
  channels: ChannelWaveform[];
}

export type ChannelUpdate = Partial<Omit<ChannelSettings, "channel">>;
export type TriggerUpdate = Partial<Omit<TriggerSettings, "level" | "holdValue" | "holdValue2" | "level2">> & {
  level?: number;
  holdValue?: number;
  holdValue2?: number;
  level2?: number;
};
export type AcquisitionUpdate = Partial<AcquisitionSettings>;

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
  connect: (host: string) => request<ScopeIdentity>("POST", "/api/connect", { host }),
  disconnect: () => request<void>("POST", "/api/disconnect"),
  settings: () => request<ScopeSettings>("GET", "/api/settings"),
  frame: (maxPoints: number) => request<WaveformFrame>("GET", `/api/frame?maxPoints=${maxPoints}`),
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
