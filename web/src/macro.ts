import type { MacroStep } from "./api";
import { si } from "./format";

/** Calls a macro may contain; the server checks the same list when a macro is saved. */
const ALLOWED = /^\/api\/(channel\/\d|timebase|acquisition|trigger|trigger\/set50|autosetup|panel|passfail|history|history\/frame|setup\/memory\/\d{1,2}\/recall|scpi)$/;

/** Longest delay kept between recorded steps ("recorded timing"). */
export const MAX_RECORDED_DELAY_MS = 60_000;

/**
 * Whether a call changes the scope and may be replayed: SCPI queries change nothing, and storing a setup or a
 * mask in the scope's memory is never replayed.
 */
export function recordable(path: string, body: unknown): boolean {
  if (!ALLOWED.test(path)) return false;
  if (path !== "/api/scpi") return true;
  const command = (body as { command?: unknown } | undefined)?.command;
  return typeof command === "string" && !command.includes("?") && !/^\s*(\*SAV|PFSL.*SAVE|PF_SAVELOAD.*SAVE)/i.test(command);
}

/** Checks steps from an imported file before they can be run. */
export function validSteps(steps: unknown): steps is MacroStep[] {
  if (!Array.isArray(steps) || steps.length === 0) return false;
  return steps.every((s) => {
    if (typeof s !== "object" || s === null) return false;
    const step = s as MacroStep;
    if (step.path === null || step.path === undefined) return typeof step.waitMs === "number" && step.waitMs >= 0 && step.waitMs <= 3_600_000;
    return typeof step.path === "string" && recordable(step.path, step.body);
  });
}

/** A short readable line for the step list; keyNames/knobs describe the front-panel keys. */
export function describeStep(step: MacroStep, keyNames: Map<number, string>, knobs: Set<number>): string {
  if (!step.path) return `Pause ${si((step.waitMs ?? 0) / 1000, "s")}`;
  const body = (step.body ?? {}) as Record<string, unknown>;
  const fields = () =>
    Object.entries(body)
      .map(([k, v]) => `${k} ${typeof v === "object" ? JSON.stringify(v) : String(v)}`)
      .join(", ");
  const channel = step.path.match(/^\/api\/channel\/(\d)$/);
  if (channel) return `C${channel[1]}: ${fields()}`;
  const recall = step.path.match(/^\/api\/setup\/memory\/(\d+)\/recall$/);
  if (recall) return `Recall setup memory ${recall[1]}`;
  switch (step.path) {
    case "/api/panel": {
      const key = Number(body.key);
      const value = Number(body.value ?? 1);
      const action = !knobs.has(key) ? "" : value === 0 ? " push" : value < 0 ? " ⟲" : " ⟳";
      return `Key ${keyNames.get(key) ?? key}${action}`;
    }
    case "/api/scpi":
      return `SCPI ${String(body.command)}`;
    case "/api/trigger/set50":
      return "Trigger level 50 %";
    case "/api/autosetup":
      return "Auto setup";
    case "/api/timebase":
      return `Timebase: ${fields()}`;
    case "/api/acquisition":
      return `Acquisition: ${fields()}`;
    case "/api/trigger":
      return `Trigger: ${fields()}`;
    case "/api/passfail":
      return `Pass/Fail: ${fields()}`;
    default:
      return `${step.path.replace("/api/", "")}: ${fields()}`;
  }
}
