export type MediaKind = "video" | "audio" | "hls" | "dash";

export type CandidateSource =
  | "network"
  | "page"
  | "youtube"
  | "instagram"
  | "x";

export interface CandidateDraft {
  dedupKey: string;
  tabId: number;
  pageHost: string;
  kind: MediaKind;
  title: string;
  displayUrl: string;
  mime?: string;
  container?: string;
  codecs?: string;
  quality?: string;
  approxBytes?: number;
  source: CandidateSource;
  storageSafeUrl?: string;
}

export interface StoredCandidate extends CandidateDraft {
  id: string;
  firstSeenAt: number;
  lastSeenAt: number;
  expiresAt: number;
}

export type PublicCandidate = Omit<StoredCandidate, "dedupKey" | "storageSafeUrl">;

export interface MediaClassification {
  kind: MediaKind;
  title: string;
  mime?: string;
  container?: string;
  codecs?: string;
  quality?: string;
  source: CandidateSource;
}

export interface CandidateSecret {
  fullUrl: string;
  referrer?: string;
  detectedAt: number;
}

export interface ExtensionSettings {
  masterEnabled: boolean;
  disabledHosts: string[];
  sessionEnabled: boolean;
}

export interface RuntimeRequest {
  type: string;
  [key: string]: unknown;
}

export interface RuntimeResponse {
  ok: boolean;
  error?: string;
  [key: string]: unknown;
}
