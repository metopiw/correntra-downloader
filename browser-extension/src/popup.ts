import { OPAQUE_ID_PATTERN } from "./shared/candidate-index";
import { currentSettings, loadSettings, setSessionEnabled } from "./shared/settings";
import type { PublicCandidate, RuntimeResponse } from "./shared/types";

const ALL_SITE_ORIGINS = ["http://*/*", "https://*/*"];

const masterToggle = document.querySelector<HTMLInputElement>("#master-toggle")!;
const siteToggle = document.querySelector<HTMLInputElement>("#site-toggle")!;
const sessionToggle = document.querySelector<HTMLInputElement>("#session-toggle")!;
const siteHost = document.querySelector<HTMLElement>("#site-host")!;
const candidateList = document.querySelector<HTMLElement>("#candidate-list")!;
const candidateCount = document.querySelector<HTMLElement>("#candidate-count")!;
const hostStatus = document.querySelector<HTMLElement>("#host-status")!;
const notice = document.querySelector<HTMLElement>("#notice")!;
const onboarding = document.querySelector<HTMLElement>("#onboarding")!;

let activeTabId = -1;
let activePageUrl = "";
let currentHost = "";

function text(key: string, fallback = key): string {
  return chrome.i18n.getMessage(key) || fallback;
}

function localizePage(): void {
  document.documentElement.lang = chrome.i18n.getUILanguage().toLowerCase().startsWith("tr") ? "tr" : "en";
  document.querySelectorAll<HTMLElement>("[data-i18n]").forEach((element) => {
    const key = element.dataset.i18n;
    if (key) element.textContent = text(key, element.textContent ?? key);
  });
}

function sendMessage(request: Record<string, unknown>): Promise<RuntimeResponse> {
  return new Promise((resolve) => {
    chrome.runtime.sendMessage(request, (response: RuntimeResponse | undefined) => {
      if (chrome.runtime.lastError) {
        resolve({ ok: false, error: "appUnavailable" });
        return;
      }
      resolve(response ?? { ok: false, error: "appUnavailable" });
    });
  });
}

function currentTab(): Promise<chrome.tabs.Tab | null> {
  return new Promise((resolve) => {
    chrome.tabs.query({ active: true, currentWindow: true }, (tabs) => {
      if (chrome.runtime.lastError) {
        resolve(null);
        return;
      }
      resolve(tabs[0] ?? null);
    });
  });
}

function showNotice(key: string, isError = false): void {
  notice.textContent = key ? text(key, key) : "";
  notice.dataset.error = String(isError);
}

function formatBytes(value: number | undefined): string {
  if (!value || value <= 0) return text("unknownSize", "Unknown size");
  const units = ["B", "KB", "MB", "GB", "TB"];
  let size = value;
  let index = 0;
  while (size >= 1024 && index < units.length - 1) {
    size /= 1024;
    index++;
  }
  return `${size >= 10 || index === 0 ? size.toFixed(0) : size.toFixed(1)} ${units[index]}`;
}

function isCandidate(value: unknown): value is PublicCandidate {
  if (typeof value !== "object" || value === null || Array.isArray(value)) return false;
  const candidate = value as Partial<PublicCandidate>;
  return typeof candidate.id === "string" && OPAQUE_ID_PATTERN.test(candidate.id) && typeof candidate.title === "string" && typeof candidate.kind === "string";
}

function renderCandidates(rawCandidates: unknown): void {
  const candidates = Array.isArray(rawCandidates) ? rawCandidates.filter(isCandidate) : [];
  candidateList.replaceChildren();
  candidateCount.textContent = String(candidates.length);
  for (const candidate of candidates) {
    const row = document.createElement("article");
    row.className = "candidate";
    const icon = document.createElement("span");
    icon.className = "candidate-icon";
    icon.textContent = candidate.kind === "audio" ? "♫" : "▶";
    const copy = document.createElement("div");
    copy.className = "candidate-copy";
    const title = document.createElement("p");
    title.className = "candidate-title";
    title.textContent = candidate.title;
    const meta = document.createElement("p");
    meta.className = "candidate-meta";
    meta.textContent = [candidate.quality, candidate.container?.toUpperCase(), formatBytes(candidate.approxBytes)].filter(Boolean).join(" · ");
    copy.append(title, meta);
    const button = document.createElement("button");
    button.type = "button";
    button.className = "download-button";
    button.textContent = text("download", "Download");
    button.addEventListener("click", () => void startDownload(button, candidate.id));
    row.append(icon, copy, button);
    candidateList.append(row);
  }
}

async function startDownload(button: HTMLButtonElement, candidateId: string): Promise<void> {
  button.disabled = true;
  showNotice("");
  const response = await sendMessage({
    type: "candidate.start",
    candidateId,
    tabId: activeTabId,
    includeSession: currentSettings().sessionEnabled
  });
  button.disabled = false;
  showNotice(response.ok ? "queued" : response.error ?? "appUnavailable", !response.ok);
}

async function refreshState(): Promise<void> {
  const response = await sendMessage({ type: "popup.getState", tabId: activeTabId, pageUrl: activePageUrl });
  if (!response.ok) return;
  masterToggle.checked = response.masterEnabled === true;
  currentHost = typeof response.host === "string" ? response.host : "";
  siteHost.textContent = currentHost || text("siteUnavailable", "Unavailable on this page");
  siteToggle.disabled = !currentHost || !masterToggle.checked;
  siteToggle.checked = response.siteEnabled === true;
  sessionToggle.disabled = !masterToggle.checked || !currentHost;
  onboarding.hidden = masterToggle.checked;
  renderCandidates(response.candidates);
}

masterToggle.addEventListener("change", () => void (async () => {
  masterToggle.disabled = true;
  if (masterToggle.checked) {
    const granted = await chrome.permissions.request({ origins: ALL_SITE_ORIGINS });
    if (!granted) {
      masterToggle.checked = false;
      masterToggle.disabled = false;
      showNotice("permissionDenied", true);
      return;
    }
  }
  const response = await sendMessage({ type: "settings.master.set", enabled: masterToggle.checked });
  if (!response.ok) {
    masterToggle.checked = false;
    showNotice(response.error ?? "permissionRequired", true);
  }
  masterToggle.disabled = false;
  await refreshState();
})());

siteToggle.addEventListener("change", () => void (async () => {
  if (!currentHost) return;
  siteToggle.disabled = true;
  const response = await sendMessage({
    type: "settings.site.set",
    host: currentHost,
    enabled: siteToggle.checked,
    tabId: activeTabId
  });
  if (!response.ok) siteToggle.checked = !siteToggle.checked;
  siteToggle.disabled = false;
})());

sessionToggle.addEventListener("change", () => void (async () => {
  if (sessionToggle.checked) {
    const granted = await chrome.permissions.request({ permissions: ["cookies"] });
    if (!granted) {
      sessionToggle.checked = false;
      showNotice("sessionPermissionDenied", true);
      return;
    }
  }
  await setSessionEnabled(sessionToggle.checked);
})());

async function checkHost(): Promise<void> {
  const response = await sendMessage({ type: "host.ping" });
  const online = response.available === true;
  hostStatus.dataset.state = online ? "online" : "offline";
  hostStatus.textContent = text(online ? "hostOnline" : "hostOffline", online ? "App ready" : "App offline");
}

async function initialize(): Promise<void> {
  localizePage();
  const tab = await currentTab();
  activeTabId = tab?.id ?? -1;
  activePageUrl = tab?.url ?? "";
  await loadSettings();
  sessionToggle.checked = currentSettings().sessionEnabled;
  await Promise.all([refreshState(), checkHost()]);
}

void initialize();
