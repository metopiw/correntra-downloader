"use strict";

const MIN_WIDTH = 140;
const MIN_HEIGHT = 72;
const BAR_HEIGHT = 20;
const BAR_GAP = 4;
const MENU_WIDTH = 196;
const STYLE = `
  .bar {
    position: fixed;
    height: ${BAR_HEIGHT}px;
    padding: 0 9px;
    box-sizing: border-box;
    font: 600 11px/${BAR_HEIGHT}px "Segoe UI", Tahoma, sans-serif;
    color: #fff;
    background: linear-gradient(#ffb347, #ef8a00);
    border: 1px solid #c56e00;
    border-radius: 2px;
    box-shadow: 0 1px 2px rgba(0,0,0,.35);
    cursor: pointer;
    opacity: .5;
    white-space: nowrap;
    user-select: none;
    pointer-events: auto;
    z-index: 2147483647;
  }
  .bar:hover, .bar.is-open, .bar.is-busy { opacity: 1; }
  .menu {
    position: fixed;
    min-width: ${MENU_WIDTH}px;
    max-width: 280px;
    max-height: 260px;
    overflow-x: hidden;
    overflow-y: auto;
    background: #fff;
    color: #1a1a1a;
    border: 1px solid #c8c8c8;
    box-shadow: 0 3px 10px rgba(0,0,0,.28);
    font: 12px/22px "Segoe UI", Tahoma, sans-serif;
    pointer-events: auto;
    z-index: 2147483647;
  }
  .row {
    display: block;
    width: 100%;
    padding: 2px 12px;
    box-sizing: border-box;
    border: 0;
    background: transparent;
    text-align: left;
    color: inherit;
    font: inherit;
    cursor: pointer;
    white-space: nowrap;
  }
  .row:hover { background: #ffe6b0; }
  .row.is-status { color: #666; cursor: default; }
  .row.is-status:hover { background: transparent; }
`;

const turkish = (navigator.language || "").toLowerCase().startsWith("tr");
const overlays = new Map();
let shadow = null;
let host = null;
let enabled = true;
let openMedia = null;

function text(kind, key) {
  const audio = kind === "audio";
  const tr = {
    bar: audio ? "Bu müziği indir" : "Bu videoyu indir",
    loading: "Kaliteler alınıyor…",
    empty: "Kalite bulunamadı",
    down: "Correntra çalışmıyor",
    drm: "Korumalı içerik",
    fail: "Liste alınamadı",
    sending: "Correntra’ya gönderiliyor…",
  };
  const en = {
    bar: audio ? "Download this audio" : "Download this video",
    loading: "Looking up qualities…",
    empty: "No qualities found",
    down: "Correntra is not running",
    drm: "Protected media",
    fail: "Could not list qualities",
    sending: "Sending to Correntra…",
  };
  return (turkish ? tr : en)[key];
}

function candidateId() {
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  let raw = "";
  for (let i = 0; i < bytes.length; i += 1) {
    raw += String.fromCharCode(bytes[i]);
  }
  const token = btoa(raw).replace(/[^A-Za-z0-9_-]/g, "A").slice(0, 22);
  return "c_" + token;
}

function isHttp(url) {
  return typeof url === "string" && /^https?:\/\//i.test(url);
}

function isFragment(url) {
  return /googlevideo\.com|\/videoplayback|tiktokcdn|byteoversea|fbcdn\.net|cdninstagram|twimg\.com/i.test(url);
}

function looksDirect(url) {
  return /\.(mp4|webm|mkv|mov|m4v|m3u8|mpd|m4a|mp3|aac|ogg|opus|flac)(\?|#|$)/i.test(url.split("?")[0]);
}

function send(payload) {
  return new Promise((resolve) => {
    try {
      chrome.runtime.sendMessage(payload, (response) => {
        if (chrome.runtime.lastError) {
          resolve({ accepted: false, reason: chrome.runtime.lastError.message });
          return;
        }
        resolve(response || { accepted: false, reason: "empty-response" });
      });
    } catch (error) {
      resolve({ accepted: false, reason: String(error) });
    }
  });
}

function ensureRoot() {
  if (host && host.isConnected && shadow) {
    return shadow;
  }

  host = document.createElement("div");
  host.setAttribute("data-correntra-overlay", "1");
  host.style.cssText = "all:initial;position:fixed;inset:0;width:100%;height:100%;pointer-events:none;z-index:2147483646;";
  shadow = host.attachShadow({ mode: "closed" });
  const style = document.createElement("style");
  style.textContent = STYLE;
  shadow.appendChild(style);
  (document.documentElement || document.body).appendChild(host);
  return shadow;
}

function mediaKind(element) {
  if (element.tagName === "AUDIO") {
    return "audio";
  }
  if (element.mozHasAudio === false && element.videoWidth > 0) {
    return "video";
  }
  return element.tagName === "AUDIO" ? "audio" : "video";
}

function isUsable(element) {
  if (!element || !element.isConnected) {
    return false;
  }
  if (element.tagName === "VIDEO" && element.disablePictureInPicture && element.dataset.correntraSkip) {
    return false;
  }
  const rect = element.getBoundingClientRect();
  if (rect.width < MIN_WIDTH || rect.height < (element.tagName === "AUDIO" ? 24 : MIN_HEIGHT)) {
    return false;
  }
  if (rect.bottom < 0 || rect.right < 0 || rect.top > window.innerHeight || rect.left > window.innerWidth) {
    return false;
  }
  const style = window.getComputedStyle(element);
  if (style.display === "none" || style.visibility === "hidden" || Number(style.opacity) === 0) {
    return false;
  }
  return true;
}

async function pickUrl(element) {
  const src = element.currentSrc || element.src || "";
  if (isHttp(src) && looksDirect(src) && !isFragment(src)) {
    return src;
  }
  const sniffed = await send({ type: "correntra.sniffed" });
  const hit = (sniffed.urls || []).find((url) => isHttp(url) && looksDirect(url) && !isFragment(url));
  if (hit) {
    return hit;
  }
  if (isHttp(src) && !isFragment(src)) {
    return src;
  }
  return location.href;
}

function pageTitle() {
  return (document.title || "media").replace(/\s+[-\u2013|].*$/, "").trim() || "media";
}

function stopPage(event) {
  event.preventDefault();
  event.stopPropagation();
  if (typeof event.stopImmediatePropagation === "function") {
    event.stopImmediatePropagation();
  }
}

function closeMenu() {
  for (const overlay of overlays.values()) {
    overlay.bar.classList.remove("is-open");
    overlay.menu.hidden = true;
    overlay.menu.replaceChildren();
  }
  openMedia = null;
}

function placeOverlay(element, overlay) {
  const rect = element.getBoundingClientRect();
  const barWidth = Math.ceil(overlay.bar.getBoundingClientRect().width || overlay.bar.offsetWidth || 128);
  const left = Math.max(8, rect.right - barWidth - BAR_GAP);
  const top = Math.max(0, rect.top + BAR_GAP);
  overlay.bar.style.left = left + "px";
  overlay.bar.style.top = top + "px";

  if (overlay.menu.hidden) {
    return;
  }

  const menuHeight = overlay.menu.offsetHeight || 0;
  const below = top + BAR_HEIGHT + 1;
  const openUp = below + menuHeight > window.innerHeight - 8 && top - menuHeight > 8;
  overlay.menu.style.left = Math.max(8, Math.min(left, window.innerWidth - MENU_WIDTH - 8)) + "px";
  overlay.menu.style.top = (openUp ? Math.max(0, top - menuHeight - 1) : below) + "px";
}

function statusRow(overlay, kind, key) {
  const row = document.createElement("div");
  row.className = "row is-status";
  row.textContent = text(kind, key);
  overlay.menu.replaceChildren(row);
  overlay.menu.hidden = false;
  overlay.bar.classList.add("is-open");
  placeOverlay(overlay.element, overlay);
}

function reasonKey(reason) {
  if (reason === "agent-down") {
    return "down";
  }
  if (reason === "drm-protected") {
    return "drm";
  }
  return "fail";
}

async function showQualities(overlay) {
  const kind = overlay.kind;
  overlay.bar.classList.add("is-busy", "is-open");
  statusRow(overlay, kind, "loading");
  openMedia = overlay.element;

  const url = await pickUrl(overlay.element);
  overlay.url = url;
  const payload = {
    type: "correntra.resolve",
    url,
    pageUrl: location.href,
    referrer: document.referrer || location.href,
    title: pageTitle(),
    candidateId: overlay.candidateId,
  };
  const result = await send(payload);
  if (openMedia !== overlay.element) {
    return;
  }

  overlay.bar.classList.remove("is-busy");
  if (!result.accepted) {
    statusRow(overlay, kind, reasonKey(result.reason));
    return;
  }

  const qualities = Array.isArray(result.mediaQualities) ? result.mediaQualities : [];
  if (qualities.length === 0) {
    statusRow(overlay, kind, "empty");
    return;
  }

  overlay.menu.replaceChildren();
  for (const quality of qualities) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "row";
    button.textContent = quality.displayName || quality.id;
    button.addEventListener("pointerdown", stopPage, true);
    button.addEventListener("click", (event) => {
      stopPage(event);
      void startQuality(overlay, quality);
    }, true);
    overlay.menu.appendChild(button);
  }
  overlay.menu.hidden = false;
  placeOverlay(overlay.element, overlay);
}

async function startQuality(overlay, quality) {
  statusRow(overlay, overlay.kind, "sending");
  const result = await send({
    type: "correntra.start",
    url: overlay.url || location.href,
    pageUrl: location.href,
    referrer: document.referrer || location.href,
    title: pageTitle(),
    candidateId: overlay.candidateId,
    kind: overlay.kind,
    container: quality.container || "mp4",
    formatId: quality.id,
  });
  if (!result.accepted) {
    statusRow(overlay, overlay.kind, reasonKey(result.reason));
    return;
  }
  closeMenu();
}

function attach(element) {
  if (overlays.has(element) || !enabled) {
    return;
  }

  const root = ensureRoot();
  const kind = mediaKind(element);
  const bar = document.createElement("div");
  bar.className = "bar";
  bar.textContent = text(kind, "bar");
  bar.setAttribute("role", "button");
  const menu = document.createElement("div");
  menu.className = "menu";
  menu.hidden = true;
  root.appendChild(bar);
  root.appendChild(menu);

  const overlay = {
    element,
    bar,
    menu,
    kind,
    candidateId: candidateId(),
    url: location.href,
  };
  overlays.set(element, overlay);

  const block = (event) => stopPage(event);
  bar.addEventListener("pointerdown", block, true);
  bar.addEventListener("mousedown", block, true);
  bar.addEventListener("mouseup", block, true);
  menu.addEventListener("pointerdown", block, true);
  menu.addEventListener("mousedown", block, true);
  bar.addEventListener("click", (event) => {
    stopPage(event);
    if (openMedia === element && !menu.hidden) {
      closeMenu();
      return;
    }
    closeMenu();
    void showQualities(overlay);
  }, true);

  placeOverlay(element, overlay);
}

function detach(element) {
  const overlay = overlays.get(element);
  if (!overlay) {
    return;
  }
  overlay.bar.remove();
  overlay.menu.remove();
  overlays.delete(element);
  if (openMedia === element) {
    openMedia = null;
  }
}

function sync() {
  if (!enabled) {
    for (const element of [...overlays.keys()]) {
      detach(element);
    }
    return;
  }

  ensureRoot();
  const live = new Set(document.querySelectorAll("video, audio"));
  for (const element of live) {
    if (isUsable(element)) {
      attach(element);
      const overlay = overlays.get(element);
      if (overlay) {
        overlay.bar.style.display = "";
        overlay.kind = mediaKind(element);
        overlay.bar.textContent = overlay.bar.classList.contains("is-open")
          ? overlay.bar.textContent
          : text(overlay.kind, "bar");
        placeOverlay(element, overlay);
      }
    } else if (overlays.has(element)) {
      const overlay = overlays.get(element);
      overlay.bar.style.display = "none";
      overlay.menu.hidden = true;
    }
  }
  for (const element of [...overlays.keys()]) {
    if (!live.has(element) || !element.isConnected) {
      detach(element);
    }
  }
}

document.addEventListener("click", (event) => {
  if (!openMedia) {
    return;
  }
  const overlay = overlays.get(openMedia);
  if (!overlay) {
    return;
  }
  const path = event.composedPath();
  if (path.includes(overlay.bar) || path.includes(overlay.menu)) {
    return;
  }
  closeMenu();
}, true);

document.addEventListener("keydown", (event) => {
  if (event.key === "Escape") {
    closeMenu();
  }
}, true);

const observer = new MutationObserver(() => sync());
observer.observe(document.documentElement, { childList: true, subtree: true });
window.addEventListener("scroll", sync, true);
window.addEventListener("resize", sync);
document.addEventListener("fullscreenchange", sync);

let frame = 0;
function tick() {
  frame += 1;
  if (frame % 2 === 0) {
    sync();
  }
  requestAnimationFrame(tick);
}
requestAnimationFrame(tick);

void send({ type: "correntra.enabled" }).then((result) => {
  enabled = !result || result.enabled !== false;
  sync();
});

chrome.storage.onChanged.addListener((changes, area) => {
  if (area !== "local" || !changes.captureEnabled) {
    return;
  }
  enabled = changes.captureEnabled.newValue !== false;
  closeMenu();
  sync();
});
