const MAX_CAPTURE_URL_LENGTH = 16_384;
const MAX_DISPLAY_URL_LENGTH = 260;
const MAX_TITLE_LENGTH = 140;

export function parseCaptureUrl(value: unknown): URL | null {
  if (typeof value !== "string" || value.length === 0 || value.length > MAX_CAPTURE_URL_LENGTH) {
    return null;
  }

  try {
    const url = new URL(value);
    if ((url.protocol !== "http:" && url.protocol !== "https:") || url.username || url.password) {
      return null;
    }
    return url;
  } catch {
    return null;
  }
}

export function siteKeyFromUrl(value: unknown): string | null {
  const url = parseCaptureUrl(value);
  if (!url) {
    return null;
  }
  return url.hostname.toLowerCase().replace(/\.$/, "");
}

export function canonicalCandidateUrl(value: string): string | null {
  const url = parseCaptureUrl(value);
  if (!url) {
    return null;
  }

  const port = url.port ? `:${url.port}` : "";
  const path = url.pathname.replace(/\/{2,}/g, "/");
  return `${url.protocol}//${url.hostname.toLowerCase()}${port}${path}`;
}

export function redactUrlForDisplay(value: string): string {
  const url = parseCaptureUrl(value);
  if (!url) {
    return "";
  }

  url.username = "";
  url.password = "";
  url.search = "";
  url.hash = "";
  const text = url.toString();
  if (text.length <= MAX_DISPLAY_URL_LENGTH) {
    return text;
  }
  return `${text.slice(0, MAX_DISPLAY_URL_LENGTH - 1)}…`;
}

export function storageSafeUrl(value: string): string | undefined {
  const url = parseCaptureUrl(value);
  if (!url || url.search || url.hash || url.href.length > MAX_CAPTURE_URL_LENGTH) {
    return undefined;
  }
  return url.href;
}

export function safeTitle(value: unknown, fallback: string): string {
  const candidate = typeof value === "string" ? value : "";
  const cleaned = candidate
    .replace(/[\u0000-\u001f\u007f]/g, " ")
    .replace(/[<>:"/\\|?*]/g, " ")
    .replace(/\s+/g, " ")
    .trim();
  return (cleaned || fallback).slice(0, MAX_TITLE_LENGTH);
}

export function titleFromUrl(value: string, fallback: string): string {
  const url = parseCaptureUrl(value);
  if (!url) {
    return fallback;
  }

  const segment = url.pathname.split("/").filter(Boolean).at(-1) ?? "";
  try {
    return safeTitle(decodeURIComponent(segment), fallback);
  } catch {
    return safeTitle(segment, fallback);
  }
}

export function redactSensitiveText(value: unknown): string {
  if (typeof value !== "string") {
    return "";
  }

  let result = value.slice(0, 2_000);
  result = result.replace(/\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]+/gi, "$1 [REDACTED]");
  result = result.replace(/\b(cookie|authorization)\s*[:=]\s*[^\r\n;]+/gi, "$1=[REDACTED]");
  result = result.replace(/([?&](?:token|access_token|auth|signature|sig|key|expires|policy)=)[^&#\s]*/gi, "$1[REDACTED]");
  return result;
}

export function baseNameFromPath(value: unknown): string {
  if (typeof value !== "string") {
    return "download";
  }
  const segment = value.split(/[\\/]/).at(-1);
  return safeTitle(segment, "download");
}

const SOCIAL_VIDEO_DOMAINS = [
  "youtube.com",
  "youtu.be",
  "youtube-nocookie.com",
  "facebook.com",
  "fb.watch",
  "fb.com",
  "instagram.com",
  "twitter.com",
  "x.com",
  "tiktok.com",
  "twitch.tv",
  "vimeo.com",
  "reddit.com",
  "redd.it",
  "dailymotion.com",
  "tumblr.com",
  "soundcloud.com"
];

/**
 * Sites whose media is only reachable through extractor logic (yt-dlp on the
 * app side). For these pages the overlay offers a page-level capture even
 * when no raw media URL can be observed in the DOM or network traffic.
 */
export function isSocialVideoHost(value: unknown): boolean {
  const url = parseCaptureUrl(value);
  if (!url) {
    return false;
  }
  const host = url.hostname.toLowerCase();
  return SOCIAL_VIDEO_DOMAINS.some(
    (domain) => host === domain || host.endsWith("." + domain)
  );
}
