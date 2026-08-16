import type { CandidateDraft, StoredCandidate } from "./types";

export interface CandidatePolicy {
  ttlMs: number;
  maxPerTab: number;
  maxTotal: number;
}

export const DEFAULT_CANDIDATE_POLICY: CandidatePolicy = {
  ttlMs: 20 * 60 * 1_000,
  maxPerTab: 36,
  maxTotal: 160
};

export const OPAQUE_ID_PATTERN = /^c_[A-Za-z0-9_-]{22}$/;

function fnv1a64(value: string): string {
  let hash = 0xcbf29ce484222325n;
  const bytes = new TextEncoder().encode(value);
  for (const byte of bytes) {
    hash ^= BigInt(byte);
    hash = BigInt.asUintN(64, hash * 0x100000001b3n);
  }
  return hash.toString(36).padStart(13, "0");
}

export function makeDedupKey(parts: ReadonlyArray<string | number | undefined>): string {
  return `d_${fnv1a64(parts.map((part) => String(part ?? "").toLowerCase()).join("\u001f"))}`;
}

export function createOpaqueCandidateId(): string {
  const bytes = new Uint8Array(16);
  globalThis.crypto.getRandomValues(bytes);
  let binary = "";
  for (const byte of bytes) {
    binary += String.fromCharCode(byte);
  }
  return `c_${btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "")}`;
}

export function pruneCandidates(
  candidates: readonly StoredCandidate[],
  now: number,
  policy: CandidatePolicy = DEFAULT_CANDIDATE_POLICY
): StoredCandidate[] {
  const newest = candidates
    .filter((candidate) => candidate.expiresAt > now && OPAQUE_ID_PATTERN.test(candidate.id))
    .sort((left, right) => right.lastSeenAt - left.lastSeenAt);

  const perTab = new Map<number, number>();
  const kept: StoredCandidate[] = [];
  for (const candidate of newest) {
    const count = perTab.get(candidate.tabId) ?? 0;
    if (count >= policy.maxPerTab || kept.length >= policy.maxTotal) {
      continue;
    }
    perTab.set(candidate.tabId, count + 1);
    kept.push(candidate);
  }
  return kept;
}

export function upsertCandidate(
  candidates: readonly StoredCandidate[],
  draft: CandidateDraft,
  now: number,
  idFactory: () => string = createOpaqueCandidateId,
  policy: CandidatePolicy = DEFAULT_CANDIDATE_POLICY
): { candidates: StoredCandidate[]; candidate: StoredCandidate; isNew: boolean } {
  const active = pruneCandidates(candidates, now, policy);
  const existing = active.find((candidate) => candidate.dedupKey === draft.dedupKey);
  const candidate: StoredCandidate = existing
    ? {
        ...existing,
        ...draft,
        id: existing.id,
        firstSeenAt: existing.firstSeenAt,
        lastSeenAt: now,
        expiresAt: now + policy.ttlMs
      }
    : {
        ...draft,
        id: idFactory(),
        firstSeenAt: now,
        lastSeenAt: now,
        expiresAt: now + policy.ttlMs
      };

  if (!OPAQUE_ID_PATTERN.test(candidate.id)) {
    throw new Error("Candidate ID factory returned an invalid opaque ID.");
  }

  const withoutOld = active.filter((item) => item.id !== candidate.id);
  return {
    candidates: pruneCandidates([candidate, ...withoutOld], now, policy),
    candidate,
    isNew: !existing
  };
}
