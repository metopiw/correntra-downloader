"use strict";

/* Correntra Catch — IDM-style download interceptor (MV3, from scratch)
 *
 * Two independent layers, either one is enough:
 *  1)  Page click interceptor (content.js → correntra.takeoverUrl) catches
 *      left-clicks on obvious file links (.bin/.zip/.exe/… or [download])
 *      BEFORE the browser even creates a DownloadItem.
 *  2)  chrome.downloads API (onCreated + onDeterminingFilename) catches
 *      everything else — Content-Disposition, blob:, right-click "Save as",
 *      fetch/XHR-initiated downloads, JS navigation to a file, etc.
 *
 * Both layers funnel into the same agent bridge (127.0.0.1:27410). Browser
 * transfer is PAUSED FIRST (IDM-style) so fast servers never finish in
 * Chrome while we ping/post. Resume only on failure.
 */

const AGENT = "http://127.0.0.1:27410";
const CAPTURE_LOG_KEY = "captureLog";
const CAPTURE_LOG_LIMIT = 20;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------
async function isCaptureEnabled() {
  const v = await chrome.storage.local.get({ captureEnabled: true });
  return v.captureEnabled !== false;
}

async function pingAgent() {
  for (let i = 0; i < 2; i++) {
    try {
      const r = await fetch(AGENT + "/ping", { cache: "no-store", signal: AbortSignal.timeout(2200) });
      if (r.ok) return true;
    } catch {}
    if (i === 0) await new Promise((res) => setTimeout(res, 120));
  }
  return false;
}

async function postAgent(path, body, ms) {
  const r = await fetch(AGENT + path, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(body),
    signal: AbortSignal.timeout(ms),
  });
  if (!r.ok) throw new Error("agent-http-" + r.status);
  return r.json();
}

function isHttpUrl(v) {
  return typeof v === "string" && /^https?:\/\//i.test(v);
}
function fileNameOf(url, fallback) {
  try {
    const u = new URL(url);
    const n = decodeURIComponent(u.pathname.split("/").pop() || "");
    return n || fallback || "download";
  } catch { return fallback || "download"; }
}

// ---------------------------------------------------------------------------
// Diagnostics visible in popup + toolbar badge
// ---------------------------------------------------------------------------
async function rememberCapture(entry) {
  try {
    const cur = await chrome.storage.local.get({ [CAPTURE_LOG_KEY]: [] });
    const log = Array.isArray(cur[CAPTURE_LOG_KEY]) ? cur[CAPTURE_LOG_KEY] : [];
    log.unshift({ at: Date.now(), ...entry });
    await chrome.storage.local.set({ [CAPTURE_LOG_KEY]: log.slice(0, CAPTURE_LOG_LIMIT) });
  } catch {}
}
function flashBadge(text, color) {
  try {
    chrome.action.setBadgeBackgroundColor({ color }).catch(() => {});
    chrome.action.setBadgeText({ text }).catch(() => {});
    setTimeout(() => chrome.action.setBadgeText({ text: "" }).catch(() => {}), 5500);
  } catch {}
}

// ---------------------------------------------------------------------------
// MV3 service worker keep-alive
// onCreated's async work used to die when the SW was reclaimed mid-ping.
// Keep an alarm + short interval alive only while a takeover is in flight.
// ---------------------------------------------------------------------------
let keepAliveDepth = 0;
function keepAlivePush() {
  keepAliveDepth++;
  try { chrome.alarms.create("cc-keepalive", { periodInMinutes: 0.35 }); } catch {}
}
function keepAlivePop() {
  keepAliveDepth = Math.max(0, keepAliveDepth - 1);
  if (keepAliveDepth === 0) {
    try { chrome.alarms.clear("cc-keepalive", () => {}); } catch {}
  }
}
try {
  chrome.alarms.onAlarm.addListener((a) => {
    if (a && a.name === "cc-keepalive" && keepAliveDepth > 0) {
      // Touch storage to keep SW alive; do not loop forever if depth is 0.
      chrome.runtime.getPlatformInfo(() => {});
    }
  });
} catch {}

// ---------------------------------------------------------------------------
// Media sniffing for video overlay (unchanged behaviour, kept for parity)
// ---------------------------------------------------------------------------
const mediaByTab = new Map();
function looksLikeFragment(url) {
  return /googlevideo\.com|\/videoplayback|tiktokcdn|byteoversea|fbcdn\.net|cdninstagram|twimg\.com/i.test(url);
}
function looksLikeMedia(url, type, headers) {
  if (type === "media") return !looksLikeFragment(url);
  const path = String(url).split("?")[0].toLowerCase();
  if (/\.(m3u8|mpd|mp4|webm|mkv|m4a|mp3)(\.|$)/.test(path) && !looksLikeFragment(url)) return true;
  const ct = (headers || []).find((h) => h.name.toLowerCase() === "content-type")?.value || "";
  return /^(video|audio)\//i.test(ct) || /mpegurl|dash\+xml/i.test(ct);
}
chrome.webRequest.onCompleted.addListener(
  (d) => {
    if (d.tabId < 0 || !looksLikeMedia(d.url, d.type, d.responseHeaders)) return;
    const cur = mediaByTab.get(d.tabId) || [];
    if (!cur.includes(d.url)) { cur.unshift(d.url); mediaByTab.set(d.tabId, cur.slice(0, 8)); }
  },
  { urls: ["http://*/*", "https://*/*"] },
  ["responseHeaders"]
);
chrome.tabs.onRemoved.addListener((id) => mediaByTab.delete(id));

// ---------------------------------------------------------------------------
// Runtime messages (video + direct URL takeover from content click interceptor)
// ---------------------------------------------------------------------------
chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  handleMessage(msg, sender).then(sendResponse).catch((e) => {
    sendResponse({ accepted: false, reason: String(e && e.message ? e.message : e) });
  });
  return true;
});

async function handleMessage(msg, sender) {
  if (!msg || typeof msg.type !== "string") return { accepted: false, reason: "invalid-message" };

  if (msg.type === "correntra.enabled") return { enabled: await isCaptureEnabled() };
  if (msg.type === "correntra.ping") return { online: await pingAgent() };
  if (msg.type === "correntra.sniffed") {
    const tid = sender.tab && sender.tab.id;
    return { urls: tid != null ? mediaByTab.get(tid) || [] : [] };
  }

  // Direct URL takeover from content click interceptor (layer 1)
  if (msg.type === "correntra.takeoverUrl") {
    if (!(await isCaptureEnabled())) return { accepted: false, reason: "disabled" };
    if (!isHttpUrl(msg.url)) return { accepted: false, reason: "non-http" };
    keepAlivePush();
    try {
      if (!(await pingAgent())) {
        await rememberCapture({ fileName: fileNameOf(msg.url, "download"), outcome: "agent-unreachable" });
        return { accepted: false, reason: "agent-down" };
      }
      const res = await postAgent("/takeover", {
        url: msg.url,
        finalUrl: msg.url,
        filename: fileNameOf(msg.url, "download"),
        mime: "",
        referrer: msg.referrer || msg.pageUrl || "",
        headers: msg.referrer ? { Referer: msg.referrer } : {},
      }, 8000);
      if (res && res.accepted && res.jobId) {
        await rememberCapture({ fileName: fileNameOf(msg.url, "download"), outcome: "captured", jobId: res.jobId });
        flashBadge("✓", "#1a9c5b");
        return { accepted: true, jobId: res.jobId };
      }
      await rememberCapture({ fileName: fileNameOf(msg.url, "download"), outcome: "rejected", reason: (res && res.reason) || "unknown" });
      return { accepted: false, reason: (res && res.reason) || "rejected" };
    } catch (e) {
      await rememberCapture({ fileName: fileNameOf(msg.url, "download"), outcome: "error", reason: String(e && e.message ? e.message : e) });
      return { accepted: false, reason: String(e && e.message ? e.message : e) };
    } finally { keepAlivePop(); }
  }

  if (msg.type === "correntra.resolve" || msg.type === "correntra.start") {
    if (!(await isCaptureEnabled())) return { accepted: false, reason: "disabled" };
    if (!(await pingAgent())) return { accepted: false, reason: "agent-down" };
    if (msg.type === "correntra.resolve") {
      return postAgent("/media/resolve", {
        url: msg.url, pageUrl: msg.pageUrl, referrer: msg.referrer, title: msg.title, candidateId: msg.candidateId,
      }, 35000);
    }
    return postAgent("/media/start", {
      url: msg.url, pageUrl: msg.pageUrl, referrer: msg.referrer, candidateId: msg.candidateId,
      media: { kind: msg.kind, title: msg.title, container: msg.container, formatId: msg.formatId },
    }, 15000);
  }
  return { accepted: false, reason: "unsupported" };
}

// ---------------------------------------------------------------------------
// Download interception — Layer 2 (downloads API)
// ---------------------------------------------------------------------------
const takeoverInFlight = new Set();

async function getDownloadState(id) {
  try {
    const items = await chrome.downloads.search({ id });
    return items && items[0] ? items[0].state : null;
  } catch { return null; }
}

// Primary: fires as soon as Chrome creates the DownloadItem.
chrome.downloads.onCreated.addListener((item) => {
  void takeOverDownload(item, "created");
});

// Backup: fires right before the filename is chosen. If onCreated was missed
// (SW was cold, event coalesced, etc.) this still catches the download.
// Must call suggest() or the download stalls.
try {
  chrome.downloads.onDeterminingFilename.addListener((item, suggest) => {
    // If onCreated already handled this id, just suggest through.
    if (takeoverInFlight.has(item.id)) {
      try { suggest({ filename: item.filename, conflictAction: "uniquify" }); } catch {}
      return;
    }
    // Quick http filter synchronously — non-http never needs Correntra.
    const u = item.finalUrl || item.url || "";
    if (!isHttpUrl(u)) {
      try { suggest({ filename: item.filename, conflictAction: "uniquify" }); } catch {}
      return;
    }
    void takeOverDownload(item, "determining");
    try { suggest({ filename: item.filename, conflictAction: "uniquify" }); } catch {}
  });
} catch {}

async function takeOverDownload(item, source) {
  if (!item || item.byExtensionId) return;
  if (takeoverInFlight.has(item.id)) return;
  const url = item.finalUrl || item.url || "";
  if (!isHttpUrl(url)) return;

  const base = { fileName: (item.filename || "").split(/[\\/]/).pop() || fileNameOf(url, "download") };

  if (!(await isCaptureEnabled())) {
    await rememberCapture({ ...base, outcome: "capture-off" });
    return;
  }

  // Dedupe + keepalive for the whole async chain.
  takeoverInFlight.add(item.id);
  keepAlivePush();
  let resumed = false;
  const resume = async () => {
    if (resumed) return;
    resumed = true;
    try { await chrome.downloads.resume(item.id); } catch {}
  };

  try {
    // 1) Pause immediately — IDM does this before any network/io.
    try { await chrome.downloads.pause(item.id); } catch {}

    // If Chrome already finished a tiny file between creation and pause,
    // re-downloading in Correntra would be wasteful.
    const st = await getDownloadState(item.id);
    if (st === "complete") {
      await rememberCapture({ ...base, outcome: "already-complete" });
      return;
    }

    // 2) Agent must be reachable; otherwise hand back to Chrome silently
    //    but with diagnostics so the user knows WHY it fell through.
    if (!(await pingAgent())) {
      await rememberCapture({ ...base, outcome: "agent-unreachable" });
      flashBadge("!", "#d43a3a");
      await resume();
      return;
    }

    // 3) Offer to agent. The agent creates a NeedsInput job and fires the
    //    desktop confirmation dialog. We cancel Chrome's copy immediately
    //    so only Correntra's transfer runs.
    const result = await postAgent("/takeover", {
      url: item.url,
      finalUrl: item.finalUrl || item.url,
      filename: item.filename,
      mime: item.mime || "",
      referrer: item.referrer || "",
      headers: item.referrer ? { Referer: item.referrer } : {},
    }, 8000);

    if (result && result.accepted && result.jobId) {
      try { await chrome.downloads.cancel(item.id); } catch {}
      try { await chrome.downloads.erase({ id: item.id }); } catch {}
      await rememberCapture({ ...base, outcome: "captured", jobId: result.jobId });
      flashBadge("✓", "#1a9c5b");
      return;
    }

    await rememberCapture({ ...base, outcome: "rejected", reason: (result && result.reason) || "unknown" });
    flashBadge("!", "#d43a3a");
    await resume();
  } catch (e) {
    await rememberCapture({ ...base, outcome: "error", reason: String(e && e.message ? e.message : e) });
    flashBadge("!", "#d43a3a");
    await resume();
  } finally {
    takeoverInFlight.delete(item.id);
    keepAlivePop();
  }
}
