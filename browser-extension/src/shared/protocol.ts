import { createOpaqueCandidateId } from "./candidate-index";

export const NATIVE_HOST_NAME = "com.correntra.downloader";
export const PROTOCOL_VERSION = 1;
export const MAX_NATIVE_MESSAGE_BYTES = 256 * 1_024;

export type NativeRequestKind = "host.ping" | "takeover.offer" | "media.start" | "media.resolve";

export interface NativeRequest<TPayload extends Record<string, unknown> = Record<string, unknown>> {
  protocolVersion: 1;
  kind: NativeRequestKind;
  requestId: string;
  timestampUtc: string;
  payload: TPayload;
}

export interface NativeMediaQuality {
  id: string;
  displayName: string;
  container: string;
  height?: number;
  bitrate?: number;
  mimeType?: string;
}

export interface NativeResponsePayload {
  accepted: boolean;
  reason?: string;
  hostVersion?: string;
  mediaQualities?: NativeMediaQuality[];
}

export interface NativeResponse {
  protocolVersion: 1;
  kind: "response";
  requestId: string;
  timestampUtc: string;
  payload: NativeResponsePayload;
}

export function createRequestId(): string {
  return `r_${createOpaqueCandidateId().slice(2)}`;
}

export function buildNativeRequest<TPayload extends Record<string, unknown>>(
  kind: NativeRequestKind,
  payload: TPayload,
  requestId = createRequestId(),
  now = new Date()
): NativeRequest<TPayload> {
  return {
    protocolVersion: PROTOCOL_VERSION,
    kind,
    requestId,
    timestampUtc: now.toISOString(),
    payload
  };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

export function parseNativeResponse(value: unknown, expectedRequestId: string): NativeResponse | null {
  if (!isRecord(value) || value.protocolVersion !== PROTOCOL_VERSION || value.kind !== "response") {
    return null;
  }
  if (value.requestId !== expectedRequestId || typeof value.timestampUtc !== "string" || !isRecord(value.payload)) {
    return null;
  }
  if (typeof value.payload.accepted !== "boolean") {
    return null;
  }

  const reason = typeof value.payload.reason === "string" ? value.payload.reason.slice(0, 160) : undefined;
  const hostVersion = typeof value.payload.hostVersion === "string" ? value.payload.hostVersion.slice(0, 40) : undefined;
  const mediaQualities = Array.isArray(value.payload.mediaQualities)
    ? value.payload.mediaQualities
        .filter((q: unknown): q is NativeMediaQuality =>
          typeof q === "object" && q !== null &&
          typeof (q as NativeMediaQuality).id === "string" &&
          typeof (q as NativeMediaQuality).displayName === "string")
        .map((q: NativeMediaQuality) => ({
          id: q.id,
          displayName: q.displayName,
          container: typeof q.container === "string" ? q.container : "mp4",
          ...(typeof q.height === "number" ? { height: q.height } : {}),
          ...(typeof q.bitrate === "number" ? { bitrate: q.bitrate } : {}),
          ...(typeof q.mimeType === "string" ? { mimeType: q.mimeType } : {})
        }))
    : undefined;
  return {
    protocolVersion: PROTOCOL_VERSION,
    kind: "response",
    requestId: expectedRequestId,
    timestampUtc: value.timestampUtc,
    payload: {
      accepted: value.payload.accepted,
      ...(reason ? { reason } : {}),
      ...(hostVersion ? { hostVersion } : {}),
      ...(mediaQualities && mediaQualities.length > 0 ? { mediaQualities } : {})
    }
  };
}

export function fitsNativeMessageLimit(value: unknown): boolean {
  try {
    return new TextEncoder().encode(JSON.stringify(value)).byteLength <= MAX_NATIVE_MESSAGE_BYTES;
  } catch {
    return false;
  }
}
