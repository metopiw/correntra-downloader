import assert from "node:assert/strict";
import test from "node:test";
import {
  makeDedupKey,
  OPAQUE_ID_PATTERN,
  pruneCandidates,
  upsertCandidate,
  type CandidatePolicy
} from "../src/shared/candidate-index";
import type { CandidateDraft, StoredCandidate } from "../src/shared/types";

const policy: CandidatePolicy = { ttlMs: 1_000, maxPerTab: 2, maxTotal: 3 };
const validIds = [
  "c_AAAAAAAAAAAAAAAAAAAAAA",
  "c_BBBBBBBBBBBBBBBBBBBBBB",
  "c_CCCCCCCCCCCCCCCCCCCCCC",
  "c_DDDDDDDDDDDDDDDDDDDDDD"
];

function draft(tabId: number, path: string): CandidateDraft {
  return {
    dedupKey: makeDedupKey([tabId, `https://example.test/${path}`, "video", "720p"]),
    tabId,
    pageHost: "example.test",
    kind: "video",
    title: path,
    displayUrl: `https://example.test/${path}`,
    quality: "720p",
    source: "network"
  };
}

test("opaque candidate IDs contain no URL material", () => {
  assert.match(validIds[0]!, OPAQUE_ID_PATTERN);
  assert.doesNotMatch(validIds[0]!, /example|https/i);
});

test("upsert deduplicates variants and refreshes TTL", () => {
  const first = upsertCandidate([], draft(1, "movie.mp4"), 100, () => validIds[0]!, policy);
  const second = upsertCandidate(first.candidates, { ...draft(1, "movie.mp4"), title: "Updated" }, 500, () => validIds[1]!, policy);
  assert.equal(second.isNew, false);
  assert.equal(second.candidate.id, validIds[0]);
  assert.equal(second.candidate.title, "Updated");
  assert.equal(second.candidate.expiresAt, 1_500);
});

test("pruning enforces TTL, per-tab cap and global cap", () => {
  let records: StoredCandidate[] = [];
  let idIndex = 0;
  for (const [tab, path, time] of [[1, "one", 100], [1, "two", 200], [1, "three", 300], [2, "four", 400]] as const) {
    records = upsertCandidate(records, draft(tab, path), time, () => validIds[idIndex++]!, policy).candidates;
  }
  assert.equal(records.length, 3);
  assert.equal(records.filter((item) => item.tabId === 1).length, 2);
  assert.deepEqual(records.map((item) => item.title), ["four", "three", "two"]);
  assert.equal(pruneCandidates(records, 1_401, policy).length, 0);
});

test("dedup key is stable and does not expose its input", () => {
  const one = makeDedupKey([4, "https://example.test/video?token=secret", "video"]);
  const two = makeDedupKey([4, "https://example.test/video?token=secret", "video"]);
  assert.equal(one, two);
  assert.doesNotMatch(one, /secret|example/);
});
