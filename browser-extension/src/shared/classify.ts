import type { CandidateSource, MediaClassification, MediaKind } from "./types";
import { parseCaptureUrl, safeTitle, titleFromUrl } from "./url";

export interface ClassificationInput {
  url: string;
  contentType?: string;
  contentDisposition?: string;
  initiator?: string;
  elementKind?: "video" | "audio";
  videoHeight?: number;
}

const VIDEO_EXTENSIONS = new Set(["mp4", "webm", "mkv", "mov", "m4v", "ogv"]);
const AUDIO_EXTENSIONS = new Set(["mp3", "m4a", "aac", "flac", "ogg", "oga", "opus", "wav", "weba"]);
const SEGMENT_EXTENSIONS = new Set(["m4s", "cmfa", "cmfv", "ts", "key"]);

const YOUTUBE_ITAG_QUALITY: Readonly<Record<string, string>> = {
  "18": "360p",
  "22": "720p",
  "37": "1080p",
  "134": "360p",
  "135": "480p",
  "136": "720p",
  "137": "1080p",
  "248": "1080p",
  "399": "1080p",
  "400": "1440p",
  "401": "2160p",
  "140": "M4A",
  "249": "Opus",
  "250": "Opus",
  "251": "Opus"
};

function normalizedMime(value: string | undefined): string | undefined {
  const mime = value?.split(";", 1)[0]?.trim().toLowerCase();
  return mime || undefined;
}

function extensionOf(pathname: string): string {
  const match = /\.([a-z0-9]{1,8})$/i.exec(pathname);
  return match?.[1]?.toLowerCase() ?? "";
}

function sourceFor(url: URL, initiator: string | undefined): CandidateSource {
  const host = url.hostname.toLowerCase();
  const parent = initiator?.toLowerCase() ?? "";
  if (host === "googlevideo.com" || host.endsWith(".googlevideo.com") || parent.includes("youtube.com")) {
    return "youtube";
  }
  if (host.endsWith("cdninstagram.com") || parent.includes("instagram.com")) {
    return "instagram";
  }
  if (host === "video.twimg.com" || parent.includes("twitter.com") || parent.includes("x.com")) {
    return "x";
  }
  return "network";
}

function dispositionFilename(value: string | undefined): string | undefined {
  if (!value || value.length > 2_048) {
    return undefined;
  }

  const utf8 = /filename\*\s*=\s*UTF-8''([^;]+)/i.exec(value)?.[1];
  if (utf8) {
    try {
      return decodeURIComponent(utf8.replace(/^"|"$/g, ""));
    } catch {
      return utf8;
    }
  }

  return /filename\s*=\s*(?:"([^"]+)"|([^;]+))/i.exec(value)?.slice(1).find(Boolean)?.trim();
}

function mimeFromGoogleVideo(url: URL): string | undefined {
  const raw = url.searchParams.get("mime");
  if (!raw || raw.length > 180) {
    return undefined;
  }
  return normalizedMime(raw);
}

function codecsFromUrl(url: URL): string | undefined {
  const rawMime = url.searchParams.get("mime") ?? "";
  const codecMatch = /codecs?=["']?([^;"']+)/i.exec(rawMime)?.[1];
  if (codecMatch) {
    return codecMatch.trim().slice(0, 80);
  }
  const codec = url.searchParams.get("codecs") ?? url.searchParams.get("codec");
  return codec?.slice(0, 80) || undefined;
}

function qualityFrom(url: URL, height: number | undefined): string | undefined {
  if (height && Number.isFinite(height) && height >= 120 && height <= 8_640) {
    return `${Math.round(height)}p`;
  }
  const label = url.searchParams.get("quality_label") ?? url.searchParams.get("quality");
  if (label && /^[a-z0-9 _.-]{1,24}$/i.test(label)) {
    return label;
  }
  const queryHeight = url.searchParams.get("height") ?? url.searchParams.get("res");
  if (queryHeight && /^\d{3,4}$/.test(queryHeight)) {
    return `${queryHeight}p`;
  }
  const itag = url.searchParams.get("itag");
  return itag ? YOUTUBE_ITAG_QUALITY[itag] : undefined;
}

function containerFor(mime: string | undefined, extension: string): string | undefined {
  const subtype = mime?.split("/")[1];
  if (subtype) {
    const container = subtype.replace(/^x-/, "").replace("mpegurl", "hls").replace("vnd.apple.mpegurl", "hls");
    return container.slice(0, 24);
  }
  return extension || undefined;
}

function isSegmentUrl(url: URL): boolean {
  // Byte-range fMP4/TS fragments carry a start/end (often alongside a
  // `bytestart`/`byteend` or `range` query) and are one slice of an MSE
  // stream rather than a self-contained file.
  const hasByteRange =
    (url.searchParams.has("bytestart") && url.searchParams.has("byteend")) ||
    (url.searchParams.has("start") && url.searchParams.has("end"));
  return hasByteRange;
}

function trackKindFromFacebook(url: URL): MediaKind | null {
  // Facebook DASH URLs carry a JSON "efg" query parameter whose
  // "vencode_tag" indicates the transport track (e.g. "dash_ln_heaac_vbr3_audio"
  // for audio vs "dash_r2av1-...-ads" for separate-video segments).
  let efg = url.searchParams.get("efg");
  if (!efg) {
    return null;
  }
  try {
    efg = decodeURIComponent(efg);
  } catch {
    return null;
  }
  const videoId = /"video_id"/i.test(efg) || /_video/i.test(efg);
  const audio = /_audio/i.test(efg) || /heaac/i.test(efg) || /audio/i.test(efg);
  if (audio && !videoId) {
    return "audio";
  }
  if (videoId) {
    return "video";
  }
  return null;
}

function classifyKind(url: URL, mime: string | undefined, extension: string, elementKind: ClassificationInput["elementKind"]): MediaKind | null {
  if (SEGMENT_EXTENSIONS.has(extension) || /\/(?:license|widevine|playready)(?:\/|$)/i.test(url.pathname)) {
    return null;
  }
  const facebookTrack = trackKindFromFacebook(url);
  if (facebookTrack) {
    return facebookTrack;
  }
  if (extension === "m3u8" || mime === "application/vnd.apple.mpegurl" || mime === "application/x-mpegurl" || mime === "audio/mpegurl") {
    return "hls";
  }
  if (extension === "mpd" || mime === "application/dash+xml") {
    return "dash";
  }
  if (mime?.startsWith("video/")) {
    return "video";
  }
  if (mime?.startsWith("audio/")) {
    return "audio";
  }
  if (VIDEO_EXTENSIONS.has(extension)) {
    return "video";
  }
  if (AUDIO_EXTENSIONS.has(extension)) {
    return "audio";
  }
  if (url.hostname.endsWith(".googlevideo.com") && url.pathname.includes("videoplayback")) {
    return mime?.startsWith("audio/") ? "audio" : "video";
  }
  return elementKind ?? null;
}

export function classifyMedia(input: ClassificationInput): MediaClassification | null {
  const url = parseCaptureUrl(input.url);
  if (!url) {
    return null;
  }

  // Fragmented MP4 / byte-range media segments (Facebook DASH etc.) are not
  // complete videos. Never surface a lone segment as a downloadable track;
  // only the manifest (or a whole direct file) should be offered.
  if (isSegmentUrl(url)) {
    return null;
  }

  const extension = extensionOf(url.pathname);
  const mime = normalizedMime(input.contentType) ?? mimeFromGoogleVideo(url);
  const kind = classifyKind(url, mime, extension, input.elementKind);
  if (!kind) {
    return null;
  }

  const fallback = kind === "audio" ? "Audio" : kind === "hls" ? "HLS stream" : kind === "dash" ? "DASH stream" : "Video";
  const headerTitle = dispositionFilename(input.contentDisposition);
  const title = headerTitle ? safeTitle(headerTitle, fallback) : titleFromUrl(input.url, fallback);

  return {
    kind,
    title,
    mime,
    container: containerFor(mime, extension),
    codecs: codecsFromUrl(url),
    quality: qualityFrom(url, input.videoHeight),
    source: sourceFor(url, input.initiator)
  };
}
