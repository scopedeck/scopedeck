// ScopeDeck start counter: the app sends GET /ping once per run, next to its update check on GitHub.
// Stored per request: day, app version, OS, CPU architecture, desktop/server, country. Nothing else (no IP).
const pick = (value, allowed, fallback) => (allowed.includes(value) ? value : fallback);

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);
    if (url.pathname !== "/ping" || request.method !== "GET")
      return new Response("Not found", { status: 404 });

    const p = url.searchParams;
    const version = (p.get("v") ?? "").slice(0, 32);
    if (!/^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$/.test(version))
      return new Response("Bad version", { status: 400 });

    const row = [
      new Date().toISOString().slice(0, 10),
      version,
      pick(p.get("os"), ["windows", "macos", "linux"], "other"),
      pick(p.get("arch"), ["x64", "arm64"], "other"),
      pick(p.get("kind"), ["desktop", "server"], "server"),
      /^[A-Z]{2}$/.test(request.cf?.country ?? "") ? request.cf.country : "XX",
    ];
    ctx.waitUntil(
      env.DB.prepare(
        "INSERT INTO checks (day, version, os, arch, kind, country, n) VALUES (?, ?, ?, ?, ?, ?, 1) " +
          "ON CONFLICT DO UPDATE SET n = n + 1"
      ).bind(...row).run().catch(() => {})
    );
    return new Response(null, { status: 204 });
  },
};
