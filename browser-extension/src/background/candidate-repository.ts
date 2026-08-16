import {
  DEFAULT_CANDIDATE_POLICY,
  makeDedupKey,
  OPAQUE_ID_PATTERN,
  pruneCandidates,
  upsertCandidate
} from "../shared/candidate-index";
import type {
  CandidateDraft,
  CandidateSecret,
  MediaClassification,
  PublicCandidate,
  StoredCandidate
} from "../shared/types";
import {
  canonicalCandidateUrl,
  parseCaptureUrl,
  redactUrlForDisplay,
  safeTitle,
  storageSafeUrl
} from "../shared/url";

const STORAGE_KEY = "mediaCandidatesV1";

export interface AddCandidateInput {
  tabId: number;
  pageHost: string;
  url: string;
  referrer?: string;
  classification: MediaClassification;
  approxBytes?: number;
}

function publicCandidate(candidate: StoredCandidate): PublicCandidate {
  const { dedupKey: _dedupKey, storageSafeUrl: _storageSafeUrl, ...safe } = candidate;
  return safe;
}

function isStoredCandidate(value: unknown): value is StoredCandidate {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    return false;
  }
  const item = value as Partial<StoredCandidate>;
  return (
    typeof item.id === "string" &&
    OPAQUE_ID_PATTERN.test(item.id) &&
    typeof item.dedupKey === "string" &&
    /^d_[a-z0-9]+$/.test(item.dedupKey) &&
    typeof item.tabId === "number" &&
    Number.isInteger(item.tabId) &&
    typeof item.pageHost === "string" &&
    typeof item.kind === "string" &&
    typeof item.title === "string" &&
    typeof item.displayUrl === "string" &&
    typeof item.source === "string" &&
    typeof item.firstSeenAt === "number" &&
    typeof item.lastSeenAt === "number" &&
    typeof item.expiresAt === "number"
  );
}

export class CandidateRepository {
  readonly #vault = new Map<string, CandidateSecret>();
  #mutation: Promise<void> = Promise.resolve();

  async initialize(): Promise<void> {
    if (chrome.storage.session.setAccessLevel) {
      await chrome.storage.session.setAccessLevel({ accessLevel: "TRUSTED_CONTEXTS" });
    }
    await this.#prunePersisted();
  }

  async add(input: AddCandidateInput): Promise<PublicCandidate | null> {
    const parsedUrl = parseCaptureUrl(input.url);
    const canonicalUrl = canonicalCandidateUrl(input.url);
    if (!parsedUrl || !canonicalUrl || !Number.isInteger(input.tabId) || input.tabId < 0) {
      return null;
    }

    const now = Date.now();
    const classification = input.classification;
    const draft: CandidateDraft = {
      dedupKey: makeDedupKey([
        input.tabId,
        canonicalUrl,
        classification.kind,
        classification.quality,
        classification.container,
        classification.codecs
      ]),
      tabId: input.tabId,
      pageHost: input.pageHost.slice(0, 253).toLowerCase(),
      kind: classification.kind,
      title: safeTitle(classification.title, classification.kind === "audio" ? "Audio" : "Video"),
      displayUrl: redactUrlForDisplay(input.url),
      ...(classification.mime ? { mime: classification.mime.slice(0, 100) } : {}),
      ...(classification.container ? { container: classification.container.slice(0, 24) } : {}),
      ...(classification.codecs ? { codecs: classification.codecs.slice(0, 80) } : {}),
      ...(classification.quality ? { quality: classification.quality.slice(0, 24) } : {}),
      ...(input.approxBytes && input.approxBytes > 0 ? { approxBytes: input.approxBytes } : {}),
      source: classification.source,
      ...(storageSafeUrl(input.url) ? { storageSafeUrl: storageSafeUrl(input.url) } : {})
    };

    let result: PublicCandidate | null = null;
    await this.#serialize(async () => {
      const candidates = await this.#read();
      const upserted = upsertCandidate(candidates, draft, now);
      await chrome.storage.session.set({ [STORAGE_KEY]: upserted.candidates });
      this.#vault.set(upserted.candidate.id, {
        fullUrl: input.url,
        ...(parseCaptureUrl(input.referrer) ? { referrer: input.referrer } : {}),
        detectedAt: now
      });
      this.#pruneVault(now, new Set(upserted.candidates.map((candidate) => candidate.id)));
      result = publicCandidate(upserted.candidate);
    });
    return result;
  }

  async listForTab(tabId: number): Promise<PublicCandidate[]> {
    const now = Date.now();
    const candidates = pruneCandidates(await this.#read(), now);
    return candidates
      .filter((candidate) => candidate.tabId === tabId)
      .sort((left, right) => right.lastSeenAt - left.lastSeenAt)
      .map(publicCandidate);
  }

  async resolve(id: string): Promise<{ candidate: StoredCandidate; secret: CandidateSecret } | null> {
    if (!OPAQUE_ID_PATTERN.test(id)) {
      return null;
    }
    const now = Date.now();
    const candidate = pruneCandidates(await this.#read(), now).find((item) => item.id === id);
    if (!candidate) {
      this.#vault.delete(id);
      return null;
    }

    const secret = this.#vault.get(id);
    if (secret && secret.detectedAt + DEFAULT_CANDIDATE_POLICY.ttlMs > now) {
      return { candidate, secret };
    }
    if (candidate.storageSafeUrl) {
      return {
        candidate,
        secret: { fullUrl: candidate.storageSafeUrl, detectedAt: candidate.lastSeenAt }
      };
    }
    return null;
  }

  async removeTab(tabId: number): Promise<void> {
    await this.#serialize(async () => {
      const candidates = (await this.#read()).filter((candidate) => candidate.tabId !== tabId);
      await chrome.storage.session.set({ [STORAGE_KEY]: candidates });
      for (const [id] of this.#vault) {
        if (!candidates.some((candidate) => candidate.id === id)) {
          this.#vault.delete(id);
        }
      }
    });
  }

  async #read(): Promise<StoredCandidate[]> {
    const result = await chrome.storage.session.get(STORAGE_KEY);
    const raw = result[STORAGE_KEY];
    return Array.isArray(raw) ? raw.filter(isStoredCandidate) : [];
  }

  async #prunePersisted(): Promise<void> {
    await this.#serialize(async () => {
      const candidates = pruneCandidates(await this.#read(), Date.now());
      await chrome.storage.session.set({ [STORAGE_KEY]: candidates });
    });
  }

  #pruneVault(now: number, activeIds: ReadonlySet<string>): void {
    for (const [id, secret] of this.#vault) {
      if (!activeIds.has(id) || secret.detectedAt + DEFAULT_CANDIDATE_POLICY.ttlMs <= now) {
        this.#vault.delete(id);
      }
    }
  }

  async #serialize(operation: () => Promise<void>): Promise<void> {
    const next = this.#mutation.then(operation, operation);
    this.#mutation = next.catch(() => undefined);
    await next;
  }
}
