"use strict";

const AGENT = "http://127.0.0.1:27410";
const mediaByTab = new Map();

async function isCaptureEnabled() {
  const stored = await chrome.storage.local.get({ captureEnabled: true });
  return stored.captureEnabled !== false;
}

async function pingAgent() {
  try {
    const response = await fetch(AGENT + "/ping", { cache: "no-store", signal: AbortSignal.timeout(700) });
    return response.ok;
  } catch {
    return false;
  }
}

async function postAgent(path, body, timeoutMs) {
  const response = await fetch(AGENT + path, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(body),
    signal: AbortSignal.timeout(timeoutMs),
  });
  if (!response.ok) {
    throw new Error("agent-http-" + response.status);
  }
  return response.json();
}

function isHttpUrl(value) {
  return typeof value === "string" && /^https?:\/\//i.test(value);
}

function looksLikeFragment(url) {
  return /googlevideo\.com|\/videoplayback|tiktokcdn|byteoversea|fbcdn\.net|cdninstagram|twimg\.com/i.test(url);
}

function looksLikeMedia(url, type, headers) {
  if (type === "media") {
    return !looksLikeFragment(url);
  }
  const path = String(url).split("?")[0].toLowerCase();
  if (/\.(m3u8|mpd|mp4|webm|mkv|m4a|mp3)(\.|$)/.test(path) && !looksLikeFragment(url)) {
    return true;
  }
  const contentType = (headers || []).find((header) => header.name.toLowerCase() === "content-type")?.value || "";
  return /^(video|audio)\//i.test(contentType) || /mpegurl|dash\+xml/i.test(contentType);
}

chrome.webRequest.onCompleted.addListener(
  (details) => {
    if (details.tabId < 0 || !looksLikeMedia(details.url, details.type, details.responseHeaders)) {
      return;
    }
    const existing = mediaByTab.get(details.tabId) || [];
    if (!existing.includes(details.url)) {
      existing.unshift(details.url);
      mediaByTab.set(details.tabId, existing.slice(0, 8));
    }
  },
  { urls: ["http://*/*", "https://*/*"] },
  ["responseHeaders"]
);

chrome.tabs.onRemoved.addListener((tabId) => mediaByTab.delete(tabId));

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  handleMessage(message, sender).then(sendResponse).catch((error) => {
    sendResponse({ accepted: false, reason: String(error && error.message ? error.message : error) });
  });
  return true;
});

async function handleMessage(message, sender) {
  if (!message || typeof message.type !== "string") {
    return { accepted: false, reason: "invalid-message" };
  }

  if (message.type === "correntra.enabled") {
    return { enabled: await isCaptureEnabled() };
  }

  if (message.type === "correntra.ping") {
    return { online: await pingAgent() };
  }

  if (message.type === "correntra.sniffed") {
    const tabId = sender.tab && sender.tab.id;
    return { urls: tabId != null ? mediaByTab.get(tabId) || [] : [] };
  }

  if (message.type === "correntra.resolve" || message.type === "correntra.start") {
    if (!(await isCaptureEnabled())) {
      return { accepted: false, reason: "disabled" };
    }
    if (!(await pingAgent())) {
      return { accepted: false, reason: "agent-down" };
    }

    if (message.type === "correntra.resolve") {
      return postAgent("/media/resolve", {
        url: message.url,
        pageUrl: message.pageUrl,
        referrer: message.referrer,
        title: message.title,
        candidateId: message.candidateId,
      }, 35000);
    }

    return postAgent("/media/start", {
      url: message.url,
      pageUrl: message.pageUrl,
      referrer: message.referrer,
      candidateId: message.candidateId,
      media: {
        kind: message.kind,
        title: message.title,
        container: message.container,
        formatId: message.formatId,
      },
    }, 15000);
  }

  return { accepted: false, reason: "unsupported" };
}

const takeoverInFlight = new Set();

chrome.downloads.onCreated.addListener((item) => {
  void takeOverDownload(item);
});

async function takeOverDownload(item) {
  if (!(await isCaptureEnabled()) || !item || item.byExtensionId) {
    return;
  }
  if (!isHttpUrl(item.url) || takeoverInFlight.has(item.id)) {
    return;
  }

  takeoverInFlight.add(item.id);
  try {
    if (!(await pingAgent())) {
      return;
    }

    try {
      await chrome.downloads.pause(item.id);
    } catch {
      // Chrome may have already finished tiny files; still try to cancel after.
    }

    const result = await postAgent("/takeover", {
      url: item.url,
      finalUrl: item.finalUrl || item.url,
      filename: item.filename,
      mime: item.mime,
      referrer: item.referrer,
      headers: item.referrer ? { Referer: item.referrer } : {},
    }, 8000);

    if (result && result.accepted && result.jobId) {
      try {
        await chrome.downloads.cancel(item.id);
      } catch {
        // Job is already in Correntra; leaving a leftover Chrome entry is worse.
      }
      try {
        await chrome.downloads.erase({ id: item.id });
      } catch {
        // Erase is cosmetic.
      }
      return;
    }

    try {
      await chrome.downloads.resume(item.id);
    } catch {
      // Browser continues if resume is rejected.
    }
  } catch {
    try {
      await chrome.downloads.resume(item.id);
    } catch {
      // Agent unreachable: never eat the browser download.
    }
  } finally {
    takeoverInFlight.delete(item.id);
  }
}
