import { CandidateRepository } from "./background/candidate-repository";
import { createEphemeralAuthContext, sendNativeRequest } from "./background/native-client";
import { classifyMedia } from "./shared/classify";
import { OPAQUE_ID_PATTERN } from "./shared/candidate-index";
import {
  currentSettings,
  isHostEnabled,
  loadSettings,
  setHostEnabled,
  setMasterEnabled
} from "./shared/settings";
import type { PublicCandidate, RuntimeRequest, RuntimeResponse } from "./shared/types";
import { baseNameFromPath, isSocialVideoHost, parseCaptureUrl, siteKeyFromUrl } from "./shared/url";

const ALL_SITE_ORIGINS = ["http://*/*", "https://*/*"];
const CONTENT_SCRIPT_ID = "correntra-page-integration-v1";
// The native host validator requires opaque candidate IDs shaped c_<22 chars>;
// page captures use this fixed synthetic ID.
const PAGE_CANDIDATE_ID = "c_pagecapture00000000000";
const TAKEOVER_TIMEOUT_MS = 4_000;
const MEDIA_START_TIMEOUT_MS = 4_000;
// yt-dlp format enumeration can take noticeably longer than a manifest fetch.
const PAGE_RESOLVE_TIMEOUT_MS = 25_000;
const PAGE_START_TIMEOUT_MS = 6_000;

const candidates = new CandidateRepository();
const settingsReady = loadSettings();
const candidatesReady = candidates.initialize();

interface WebRequestLike {
  tabId: number;
  url: string;
  initiator?: string;
  documentUrl?: string;
  responseHeaders?: chrome.webRequest.HttpHeader[];
}

function sendTabMessage(tabId: number, message: Record<string, unknown>): void {
  try {
    chrome.tabs.sendMessage(tabId, message, () => void chrome.runtime.lastError);
  } catch {
    // The page can disappear between a network event and delivery.
  }
}

function queryTabs(queryInfo: chrome.tabs.QueryInfo): Promise<chrome.tabs.Tab[]> {
  return new Promise((resolve) => {
    chrome.tabs.query(queryInfo, (tabs) => {
      if (chrome.runtime.lastError) {
        resolve([]);
        return;
      }
      resolve(tabs);
    });
  });
}

function getTab(tabId: number): Promise<chrome.tabs.Tab | null> {
  return new Promise((resolve) => {
    chrome.tabs.get(tabId, (tab) => {
      if (chrome.runtime.lastError) {
        resolve(null);
        return;
      }
      resolve(tab);
    });
  });
}

async function hasSiteAccess(): Promise<boolean> {
  return chrome.permissions.contains({ origins: ALL_SITE_ORIGINS });
}

async function registeredContentScriptExists(): Promise<boolean> {
  try {
    const scripts = await chrome.scripting.getRegisteredContentScripts({ ids: [CONTENT_SCRIPT_ID] });
    return scripts.some((script) => script.id === CONTENT_SCRIPT_ID);
  } catch {
    return false;
  }
}

async function enableContentIntegration(): Promise<void> {
  if (!(await hasSiteAccess())) {
    return;
  }
  if (!(await registeredContentScriptExists())) {
    await chrome.scripting.registerContentScripts([
      {
        id: CONTENT_SCRIPT_ID,
        js: ["content.js"],
        matches: ALL_SITE_ORIGINS,
        runAt: "document_idle",
        allFrames: false,
        persistAcrossSessions: true
      }
    ]);
  }
}

async function disableContentIntegration(): Promise<void> {
  if (await registeredContentScriptExists()) {
    await chrome.scripting.unregisterContentScripts({ ids: [CONTENT_SCRIPT_ID] });
  }
  for (const tab of await queryTabs({})) {
    if (typeof tab.id === "number") {
      sendTabMessage(tab.id, { type: "integration.state", enabled: false });
    }
  }
}

async function injectIntoExistingPages(): Promise<void> {
  for (const tab of await queryTabs({})) {
    if (typeof tab.id !== "number" || !parseCaptureUrl(tab.url)) {
      continue;
    }
    try {
      await chrome.scripting.executeScript({ target: { tabId: tab.id }, files: ["content.js"] });
    } catch {
      // Restricted browser pages and tabs closed during iteration are expected.
    }
  }
}

async function updateActionBadge(): Promise<void> {
  await loadSettings();
  const settings = currentSettings();
  await chrome.action.setBadgeText({ text: settings.masterEnabled ? "ON" : "" });
  if (settings.masterEnabled) {
    await chrome.action.setBadgeBackgroundColor({ color: "#27B88A" });
  }
}

async function synchronizeIntegration(): Promise<void> {
  await settingsReady;
  const settings = currentSettings();
  if (settings.masterEnabled && (await hasSiteAccess())) {
    await enableContentIntegration();
  } else {
    if (settings.masterEnabled) {
      await setMasterEnabled(false);
    }
    await disableContentIntegration();
  }
  await updateActionBadge();
}

function downloadAction(action: (callback: () => void) => void): Promise<boolean> {
  return new Promise((resolve) => {
    try {
      action(() => resolve(!chrome.runtime.lastError));
    } catch {
      resolve(false);
    }
  });
}

function findDownload(id: number): Promise<chrome.downloads.DownloadItem | null> {
  return new Promise((resolve) => {
    chrome.downloads.search({ id }, (items) => {
      if (chrome.runtime.lastError) {
        resolve(null);
        return;
      }
      resolve(items[0] ?? null);
    });
  });
}

async function pauseDownload(id: number): Promise<boolean> {
  const paused = await downloadAction((callback) => chrome.downloads.pause(id, callback));
  if (paused) {
    return true;
  }
  return (await findDownload(id))?.paused === true;
}

async function resumeDownloadSafely(id: number): Promise<void> {
  const item = await findDownload(id);
  if (!item || item.state !== "in_progress" || !item.paused) {
    return;
  }
  await downloadAction((callback) => chrome.downloads.resume(id, callback));
}

async function cancelAndEraseDownload(id: number): Promise<void> {
  const cancelled = await downloadAction((callback) => chrome.downloads.cancel(id, callback));
  if (!cancelled) {
    return;
  }
  await new Promise<void>((resolve) => {
    chrome.downloads.erase({ id }, () => {
      void chrome.runtime.lastError;
      resolve();
    });
  });
}

async function handleCreatedDownload(download: chrome.downloads.DownloadItem): Promise<void> {
  await settingsReady;
  if (!currentSettings().masterEnabled || download.state !== "in_progress" || download.byExtensionId === chrome.runtime.id) {
    return;
  }

  const fullUrl = parseCaptureUrl(download.finalUrl) ? download.finalUrl : download.url;
  const parsed = parseCaptureUrl(fullUrl);
  const host = siteKeyFromUrl(fullUrl);
  if (!parsed || !isHostEnabled(host)) {
    return;
  }
  if (!(await pauseDownload(download.id))) {
    return;
  }

  const headers: Record<string, string> = { "User-Agent": navigator.userAgent.slice(0, 512) };
  if (download.referrer && parseCaptureUrl(download.referrer)) {
    headers.Referer = download.referrer;
  }

  const result = await sendNativeRequest(
    "takeover.offer",
    {
      browserDownloadId: download.id,
      url: download.url,
      finalUrl: fullUrl,
      filename: effectiveDownloadName(download.filename, fullUrl),
      ...(download.mime ? { mime: download.mime.slice(0, 100) } : {}),
      ...(download.totalBytes > 0 ? { totalBytes: download.totalBytes } : {}),
      ...(download.referrer ? { referrer: download.referrer } : {}),
      incognito: download.incognito,
      headers
    },
    TAKEOVER_TIMEOUT_MS
  );

  if (!result.available) {
    console.warn("Correntra Native Messaging host unavailable; browser download will continue.");
  } else if (!result.accepted) {
    console.warn("Correntra Native Messaging takeover was rejected or timed out.");
  }

  const stillEnabled = currentSettings().masterEnabled && isHostEnabled(host);
  if (result.accepted && stillEnabled) {
    await cancelAndEraseDownload(download.id);
    return;
  }
  await resumeDownloadSafely(download.id);
}

function responseHeader(headers: chrome.webRequest.HttpHeader[] | undefined, name: string): string | undefined {
  const value = headers?.find((header) => header.name.toLowerCase() === name.toLowerCase())?.value;
  return value && value.length <= 2_048 ? value : undefined;
}

// Chrome sometimes reports a placeholder name ("download") before the server
// supplies the real one; fall back to the URL path so files keep a meaningful
// name and extension.
function effectiveDownloadName(chromeFilename: string | undefined, url: string): string {
  const base = baseNameFromPath(chromeFilename);
  const hasRealExtension = /\.[a-z0-9]{1,8}$/i.test(base);
  if (hasRealExtension && base.toLowerCase() !== "download") {
    return base;
  }
  const parsed = parseCaptureUrl(url);
  const fromUrl = parsed ? baseNameFromPath(parsed.pathname) : "download";
  if (/\.[a-z0-9]{1,8}$/i.test(fromUrl)) {
    return fromUrl;
  }
  return base || fromUrl;
}

async function pageUrlForRequest(details: WebRequestLike): Promise<string | undefined> {
  if (parseCaptureUrl(details.documentUrl)) {
    return details.documentUrl;
  }
  if (parseCaptureUrl(details.initiator)) {
    return details.initiator;
  }
  const tab = await getTab(details.tabId);
  return parseCaptureUrl(tab?.url) ? tab?.url : undefined;
}

async function captureNetworkCandidate(details: WebRequestLike): Promise<void> {
  if (details.tabId < 0) {
    return;
  }
  await Promise.all([settingsReady, candidatesReady]);
  if (!currentSettings().masterEnabled) {
    return;
  }

  const pageUrl = await pageUrlForRequest(details);
  const pageHost = siteKeyFromUrl(pageUrl ?? details.url);
  if (!isHostEnabled(pageHost)) {
    return;
  }

  const contentType = responseHeader(details.responseHeaders, "content-type");
  const contentDisposition = responseHeader(details.responseHeaders, "content-disposition");
  const classification = classifyMedia({
    url: details.url,
    contentType,
    contentDisposition,
    initiator: pageUrl
  });
  if (!classification) {
    return;
  }

  const rawLength = responseHeader(details.responseHeaders, "content-length");
  const parsedLength = rawLength && /^\d{1,16}$/.test(rawLength) ? Number(rawLength) : undefined;
  const candidate = await candidates.add({
    tabId: details.tabId,
    pageHost: pageHost ?? "",
    url: details.url,
    ...(pageUrl ? { referrer: pageUrl } : {}),
    classification,
    ...(parsedLength && Number.isSafeInteger(parsedLength) ? { approxBytes: parsedLength } : {})
  });
  if (candidate) {
    sendTabMessage(details.tabId, { type: "candidate.detected", candidate });
  }
}

async function handleDomCandidate(request: RuntimeRequest, sender: chrome.runtime.MessageSender): Promise<RuntimeResponse> {
  const tabId = sender.tab?.id;
  const url = typeof request.url === "string" ? request.url : "";
  const elementKind = request.elementKind === "audio" ? "audio" : request.elementKind === "video" ? "video" : undefined;
  if (typeof tabId !== "number" || !elementKind || !parseCaptureUrl(url)) {
    return { ok: false, error: "invalidCandidate" };
  }
  await Promise.all([settingsReady, candidatesReady]);
  const pageUrl = sender.tab?.url ?? sender.url;
  const pageHost = siteKeyFromUrl(pageUrl);
  if (!isHostEnabled(pageHost)) {
    return { ok: false, error: "disabled" };
  }

  const height = typeof request.videoHeight === "number" ? request.videoHeight : undefined;
  const classification = classifyMedia({ url, initiator: pageUrl, elementKind, videoHeight: height });
  if (!classification) {
    return { ok: false, error: "unsupported" };
  }
  if (classification.source === "network") {
    classification.source = "page";
  }

  const candidate = await candidates.add({
    tabId,
    pageHost: pageHost ?? "",
    url,
    ...(pageUrl ? { referrer: pageUrl } : {}),
    classification
  });
  return candidate ? { ok: true, candidate } : { ok: false, error: "invalidCandidate" };
}

async function resolveCandidate(request: RuntimeRequest, sender: chrome.runtime.MessageSender): Promise<RuntimeResponse> {
  const candidateId = typeof request.candidateId === "string" ? request.candidateId : "";
  if (!OPAQUE_ID_PATTERN.test(candidateId)) {
    return { ok: false, error: "candidateExpired" };
  }
  await Promise.all([settingsReady, candidatesReady]);
  const resolved = await candidates.resolve(candidateId);
  if (!resolved || !isHostEnabled(resolved.candidate.pageHost)) {
    return { ok: false, error: "candidateExpired" };
  }
  if (typeof sender.tab?.id === "number" && sender.tab.id !== resolved.candidate.tabId) {
    return { ok: false, error: "candidateExpired" };
  }

  try {
    const authContext = await createEphemeralAuthContext(
      resolved.secret.fullUrl,
      resolved.secret.referrer,
      false
    );
    const candidate = resolved.candidate;
    const socialPage = isSocialVideoHost(sender.tab?.url);
    const result = await sendNativeRequest(
      "media.resolve",
      {
        candidateId: candidate.id,
        url: resolved.secret.fullUrl,
        ...(parseCaptureUrl(sender.tab?.url) ? { pageUrl: sender.tab!.url } : {}),
        ...(resolved.secret.referrer ? { referrer: resolved.secret.referrer } : {}),
        ...(candidate.title ? { title: candidate.title } : {}),
        ...(authContext?.headers ? { headers: authContext.headers } : {})
      },
      socialPage ? PAGE_RESOLVE_TIMEOUT_MS : MEDIA_START_TIMEOUT_MS
    );
    if (!result.accepted || !result.response?.payload) {
      return { ok: false, error: "appUnavailable" };
    }
    const qualities = result.response.payload.mediaQualities ?? [];
    return { ok: true, qualities };
  } finally {
    /* no transient permission to clean up for resolve */
  }
}

async function startCandidate(request: RuntimeRequest, sender: chrome.runtime.MessageSender): Promise<RuntimeResponse> {
  const candidateId = typeof request.candidateId === "string" ? request.candidateId : "";
  if (!OPAQUE_ID_PATTERN.test(candidateId)) {
    return { ok: false, error: "candidateExpired" };
  }
  await Promise.all([settingsReady, candidatesReady]);
  const resolved = await candidates.resolve(candidateId);
  if (!resolved || !isHostEnabled(resolved.candidate.pageHost)) {
    return { ok: false, error: "candidateExpired" };
  }
  if (typeof sender.tab?.id === "number" && sender.tab.id !== resolved.candidate.tabId) {
    return { ok: false, error: "candidateExpired" };
  }
  if (typeof request.tabId === "number" && request.tabId !== resolved.candidate.tabId) {
    return { ok: false, error: "candidateExpired" };
  }

  const includeSession = request.includeSession === true || currentSettings().sessionEnabled;
  try {
    const authContext = await createEphemeralAuthContext(
      resolved.secret.fullUrl,
      resolved.secret.referrer,
      includeSession
    );
    const candidate = resolved.candidate;
    const result = await sendNativeRequest(
      "media.start",
      {
        candidateId: candidate.id,
        url: resolved.secret.fullUrl,
        ...(parseCaptureUrl(sender.tab?.url) ? { pageUrl: sender.tab!.url } : {}),
        ...(resolved.secret.referrer ? { referrer: resolved.secret.referrer } : {}),
        media: {
          kind: candidate.kind,
          title: candidate.title,
          pageHost: candidate.pageHost,
          source: candidate.source,
          ...(typeof request.formatId === "string" && request.formatId.length <= 220
            ? { formatId: request.formatId }
            : {}),
          ...(candidate.mime ? { mime: candidate.mime } : {}),
          ...(candidate.container ? { container: candidate.container } : {}),
          ...(candidate.codecs ? { codecs: candidate.codecs } : {}),
          ...(candidate.quality ? { quality: candidate.quality } : {}),
          ...(candidate.approxBytes ? { approxBytes: candidate.approxBytes } : {})
        },
        ...(authContext ? { authContext } : {})
      },
      MEDIA_START_TIMEOUT_MS
    );
    return result.accepted ? { ok: true } : { ok: false, error: "appUnavailable" };
  } finally {
    // The cookies permission is managed by the popup setting; never revoke it
    // behind the user's back after a download.
  }
}

async function resolvePageCapture(
  request: RuntimeRequest,
  sender: chrome.runtime.MessageSender
): Promise<RuntimeResponse> {
  await Promise.all([settingsReady, candidatesReady]);
  const tabId = sender.tab?.id;
  const pageUrl = parseCaptureUrl(sender.tab?.url) ? sender.tab!.url! : "";
  if (typeof tabId !== "number" || !pageUrl || !isSocialVideoHost(pageUrl)) {
    return { ok: false, error: "unsupported" };
  }
  const pageHost = siteKeyFromUrl(pageUrl);
  if (!isHostEnabled(pageHost)) {
    return { ok: false, error: "disabled" };
  }

  const title = typeof request.title === "string" ? request.title.slice(0, 140) : "";
  const result = await sendNativeRequest(
    "media.resolve",
    {
      candidateId: PAGE_CANDIDATE_ID,
      url: pageUrl,
      pageUrl,
      ...(title ? { title } : {})
    },
    PAGE_RESOLVE_TIMEOUT_MS
  );
  if (!result.accepted || !result.response?.payload) {
    return { ok: false, error: "appUnavailable" };
  }
  return { ok: true, qualities: result.response.payload.mediaQualities ?? [] };
}

async function startPageCapture(
  request: RuntimeRequest,
  sender: chrome.runtime.MessageSender
): Promise<RuntimeResponse> {
  await Promise.all([settingsReady, candidatesReady]);
  const tabId = sender.tab?.id;
  const pageUrl = parseCaptureUrl(sender.tab?.url) ? sender.tab!.url! : "";
  if (typeof tabId !== "number" || !pageUrl || !isSocialVideoHost(pageUrl)) {
    return { ok: false, error: "unsupported" };
  }
  const pageHost = siteKeyFromUrl(pageUrl);
  if (!isHostEnabled(pageHost)) {
    return { ok: false, error: "disabled" };
  }

  const title = typeof request.title === "string" ? request.title.slice(0, 140) : "";
  const formatId = typeof request.formatId === "string" ? request.formatId.slice(0, 220) : "";
  const audioOnly = formatId.includes("bestaudio");
  const authContext = await createEphemeralAuthContext(
    pageUrl,
    undefined,
    currentSettings().sessionEnabled
  );
  const result = await sendNativeRequest(
    "media.start",
    {
      candidateId: PAGE_CANDIDATE_ID,
      url: pageUrl,
      pageUrl,
      media: {
        kind: audioOnly ? "audio" : "video",
        title: title || "video",
        pageHost: pageHost ?? "",
        source: "page",
        ...(formatId ? { formatId } : {})
      },
      ...(authContext ? { authContext } : {})
    },
    PAGE_START_TIMEOUT_MS
  );
  return result.accepted ? { ok: true } : { ok: false, error: "appUnavailable" };
}

async function handleMessage(request: RuntimeRequest, sender: chrome.runtime.MessageSender): Promise<RuntimeResponse> {
  switch (request.type) {
    case "popup.getState": {
      await Promise.all([settingsReady, candidatesReady]);
      const tabId = typeof request.tabId === "number" ? request.tabId : -1;
      const pageUrl = typeof request.pageUrl === "string" ? request.pageUrl : "";
      const host = siteKeyFromUrl(pageUrl);
      return {
        ok: true,
        masterEnabled: currentSettings().masterEnabled,
        siteEnabled: isHostEnabled(host),
        host,
        hasSiteAccess: await hasSiteAccess(),
        candidates: tabId >= 0 ? await candidates.listForTab(tabId) : []
      };
    }
    case "settings.master.set": {
      const enabled = request.enabled === true;
      if (enabled && !(await hasSiteAccess())) {
        return { ok: false, error: "permissionRequired" };
      }
      await setMasterEnabled(enabled);
      if (enabled) {
        await enableContentIntegration();
        await injectIntoExistingPages();
      } else {
        await disableContentIntegration();
      }
      await updateActionBadge();
      return { ok: true, masterEnabled: enabled };
    }
    case "settings.site.set": {
      const host = typeof request.host === "string" ? request.host : "";
      const enabled = request.enabled === true;
      await setHostEnabled(host, enabled);
      if (typeof request.tabId === "number") {
        sendTabMessage(request.tabId, { type: "integration.state", enabled: isHostEnabled(host) });
      }
      return { ok: true, siteEnabled: isHostEnabled(host) };
    }
    case "content.ready": {
      await Promise.all([settingsReady, candidatesReady]);
      const tabId = sender.tab?.id;
      const host = siteKeyFromUrl(sender.tab?.url ?? sender.url);
      return {
        ok: true,
        enabled: isHostEnabled(host),
        candidates: typeof tabId === "number" ? await candidates.listForTab(tabId) : []
      };
    }
    case "dom.candidate":
      return handleDomCandidate(request, sender);
    case "candidate.resolve":
      return resolveCandidate(request, sender);
    case "candidate.start":
      return startCandidate(request, sender);
    case "page.resolve":
      return resolvePageCapture(request, sender);
    case "page.start":
      return startPageCapture(request, sender);
    case "host.ping": {
      const result = await sendNativeRequest(
        "host.ping",
        { extensionVersion: chrome.runtime.getManifest().version },
        800
      );
      return {
        ok: true,
        available: result.available && result.accepted,
        ...(result.response?.payload.hostVersion ? { hostVersion: result.response.payload.hostVersion } : {})
      };
    }
    default:
      return { ok: false, error: "unknownMessage" };
  }
}

chrome.runtime.onMessage.addListener((message: unknown, sender, sendResponse) => {
  if (typeof message !== "object" || message === null || Array.isArray(message)) {
    return false;
  }
  const request = message as RuntimeRequest;
  void handleMessage(request, sender)
    .then(sendResponse)
    .catch(() => sendResponse({ ok: false, error: "internalError" }));
  return true;
});

chrome.downloads.onCreated.addListener((download) => {
  void handleCreatedDownload(download);
});

chrome.webRequest.onBeforeRequest.addListener(
  (details) => void captureNetworkCandidate(details),
  { urls: ["<all_urls>"], types: ["media", "xmlhttprequest", "other"] }
);

chrome.webRequest.onHeadersReceived.addListener(
  (details) => void captureNetworkCandidate(details),
  { urls: ["<all_urls>"], types: ["main_frame", "sub_frame", "media", "xmlhttprequest", "other"] },
  ["responseHeaders"]
);

chrome.tabs.onRemoved.addListener((tabId) => {
  void candidatesReady.then(() => candidates.removeTab(tabId));
});

chrome.permissions.onRemoved.addListener((permissions) => {
  if (permissions.origins?.some((origin) => ALL_SITE_ORIGINS.includes(origin))) {
    void setMasterEnabled(false).then(disableContentIntegration).then(updateActionBadge);
  }
});

chrome.runtime.onInstalled.addListener(() => void synchronizeIntegration());
chrome.runtime.onStartup.addListener(() => void synchronizeIntegration());
void synchronizeIntegration();
