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
}

export interface TriggerSettings {
  mode: string;
  type: string;
  source: string;
  slope: string | null;
  level: number | null;
  coupling: string | null;
}

export interface ScopeSettings {
  timePerDiv: number;
  triggerDelay: number;
  sampleRate: number;
  status: string;
  trigger: TriggerSettings;
  channels: ChannelSettings[];
}

export interface ChannelWaveform {
  channel: number;
  voltsPerDiv: number;
  offset: number;
  firstPointTime: number;
  sampleInterval: number;
  memoryPoints: number;
  volts: number[];
}

export interface WaveformFrame {
  timePerDiv: number;
  triggerDelay: number;
  sampleRate: number;
  channels: ChannelWaveform[];
}

export type ChannelUpdate = Partial<Omit<ChannelSettings, "channel">>;
export type TriggerUpdate = Partial<Pick<TriggerSettings, "mode" | "source" | "slope" | "coupling">> & { level?: number };

export class ApiError extends Error {
  constructor(message: string, readonly status: number) {
    super(message);
  }
}

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  const res = await fetch(url, {
    method,
    headers: body === undefined ? undefined : { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
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
  trigger: (update: TriggerUpdate) => request<void>("POST", "/api/trigger", update),
  autoSetup: () => request<void>("POST", "/api/autosetup"),
  scpi: (command: string) => request<{ response: string | null }>("POST", "/api/scpi", { command }),
};
