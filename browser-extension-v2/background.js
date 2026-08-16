// Correntra Catch v2.1 — background service worker.
// Video capture is gone by design; this worker only hands browser downloads
// to the Correntra agent over the loopback HTTP bridge (IDM-style), which is
// fully observable and independent of native messaging.

const BRIDGE = "http://127.0.0.1:27410";

async function getSettings() {
  return chrome.storage.local.get({ masterEnabled: true });
}

function bridgeFetch(path, options, timeoutMs) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  return fetch(BRIDGE + path, { ...options, signal: controller.signal })
    .then((response) => (response.ok ? response.json() : null))
    .catch(() => null)
    .finally(() => clearTimeout(timer));
}

function baseNameFromPath(value) {
  if (typeof value !== "string") return "download";
  const segment = value.split(/[\\/]/).pop() || "download";
  return segment.slice(0, 200) || "download";
}

// Chrome sometimes reports a placeholder name ("download") before the real
// one is known; prefer a name derived from the URL path so files keep their
// extension.
function effectiveDownloadName(chromeFilename, url) {
  const base = baseNameFromPath(chromeFilename);
  const hasExt = /\.[a-z0-9]{1,8}$/i.test(base);
  if (hasExt && base.toLowerCase() !== "download") return base;
  try {
    const fromUrl = baseNameFromPath(new URL(url).pathname);
    if (/\.[a-z0-9]{1,8}$/i.test(fromUrl)) return fromUrl;
  } catch {
    // keep fallback
  }
  return base || "download";
}

function pauseDownload(id) {
  return new Promise((resolve) => {
    try {
      chrome.downloads.pause(id, () => resolve(!chrome.runtime.lastError));
    } catch {
      resolve(false);
    }
  });
}

function cancelAndErase(id) {
  chrome.downloads.cancel(id, () => {
    void chrome.runtime.lastError;
    chrome.downloads.erase({ id }, () => void chrome.runtime.lastError);
  });
}

function resumeDownload(id) {
  chrome.downloads.resume(id, () => void chrome.runtime.lastError);
}

async function handleCreatedDownload(download) {
  const settings = await getSettings();
  if (!settings.masterEnabled || download.state !== "in_progress" || download.byExtensionId === chrome.runtime.id) {
    return;
  }
  const fullUrl = /^https?:\/\//i.test(download.finalUrl || "") ? download.finalUrl : download.url;
  if (!/^https?:\/\//i.test(fullUrl || "")) return;

  if (!(await pauseDownload(download.id))) return;

  const headers = { "User-Agent": navigator.userAgent.slice(0, 512) };
  if (download.referrer && /^https?:\/\//i.test(download.referrer)) headers.Referer = download.referrer;

  const result = await bridgeFetch("/takeover", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      browserDownloadId: download.id,
      url: download.url,
      finalUrl: fullUrl,
      filename: effectiveDownloadName(download.filename, fullUrl),
      ...(download.mime ? { mime: String(download.mime).slice(0, 100) } : {}),
      ...(download.totalBytes > 0 ? { totalBytes: download.totalBytes } : {}),
      ...(download.referrer ? { referrer: download.referrer } : {}),
      incognito: download.incognito,
      headers
    })
  }, 4000);

  if (result && result.accepted) {
    cancelAndErase(download.id);
  } else {
    resumeDownload(download.id);
  }
}

chrome.downloads.onCreated.addListener((download) => {
  void handleCreatedDownload(download);
});

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (typeof message !== "object" || message === null) return false;
  switch (message.type) {
    case "popup.getState":
      void (async () => {
        const settings = await getSettings();
        const ping = await bridgeFetch("/ping", {}, 1500);
        sendResponse({
          ok: true,
          masterEnabled: !!settings.masterEnabled,
          hostOnline: !!(ping && ping.healthy)
        });
      })();
      return true;
    case "settings.set":
      void chrome.storage.local.set({
        ...(typeof message.masterEnabled === "boolean" ? { masterEnabled: message.masterEnabled } : {})
      }).then(() => sendResponse({ ok: true }));
      return true;
    default:
      sendResponse({ ok: false, error: "unknown" });
      return false;
  }
});
