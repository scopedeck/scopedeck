// Saving files and opening links. In a browser (and the Windows app's WebView2) a download link does both; the
// macOS/Linux app's system web view does neither, so there the app saves into the Downloads folder and opens the
// default browser (src/Siglent.Photino).

/** Photino adds window.external.sendMessage to its web view. */
export const nativeWindow =
  typeof (window as unknown as { external?: { sendMessage?: unknown } }).external?.sendMessage === "function";

const headers = { "X-ScopeDeck": "1" };

/** Where the file was saved (native window), or "" when the browser took it. */
export type Saved = string;

async function result(r: Response): Promise<Saved> {
  if (!r.ok) throw new Error((await r.json().catch(() => null))?.detail ?? `Saving failed (${r.status}).`);
  return ((await r.json()) as { path: string }).path;
}

/** Saves a file made in the page. */
export async function saveBlob(blob: Blob, name: string): Promise<Saved> {
  if (nativeWindow)
    return result(await fetch(`/desktop/save?name=${encodeURIComponent(name)}`, { method: "POST", headers, body: blob }));
  const a = document.createElement("a");
  a.href = URL.createObjectURL(blob);
  a.download = name;
  a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 1000);
  return "";
}

/** Saves a download from the server (the server names the file). */
export async function saveUrl(url: string): Promise<Saved> {
  if (nativeWindow)
    return result(await fetch("/desktop/save-url", {
      method: "POST",
      headers: { ...headers, "Content-Type": "application/json" },
      body: JSON.stringify({ url }),
    }));
  const a = document.createElement("a");
  a.href = url;
  a.download = "";
  a.click();
  return "";
}

// External links (release notes, the update badge) open in the default browser.
if (nativeWindow)
  document.addEventListener("click", (e) => {
    const a = (e.target as Element).closest?.("a[target=_blank]") as HTMLAnchorElement | null;
    if (!a?.href.startsWith("https:")) return;
    e.preventDefault();
    void fetch("/desktop/open", {
      method: "POST",
      headers: { ...headers, "Content-Type": "application/json" },
      body: JSON.stringify({ url: a.href }),
    });
  });

// Script errors go to the app's log (stderr): the system web view has no console to look at.
if (nativeWindow) {
  const report = (message: string) =>
    void fetch("/desktop/log", { method: "POST", headers, body: message.slice(0, 4000) }).catch(() => {});
  window.addEventListener("error", (e) => report(`${e.message} at ${e.filename}:${e.lineno}:${e.colno}`));
  window.addEventListener("unhandledrejection", (e) => report(`Unhandled rejection: ${e.reason?.stack ?? e.reason}`));
}
