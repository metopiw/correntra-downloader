import assert from "node:assert/strict";
import test from "node:test";
import {
  buildNativeRequest,
  fitsNativeMessageLimit,
  MAX_NATIVE_MESSAGE_BYTES,
  NATIVE_HOST_NAME,
  parseNativeResponse,
  PROTOCOL_VERSION
} from "../src/shared/protocol";

test("builds versioned camelCase native envelopes", () => {
  const request = buildNativeRequest("takeover.offer", { browserDownloadId: 12 }, "r_AAAAAAAAAAAAAAAAAAAAAA", new Date("2026-08-13T12:00:00Z"));
  assert.equal(NATIVE_HOST_NAME, "com.correntra.downloader");
  assert.deepEqual(request, {
    protocolVersion: PROTOCOL_VERSION,
    kind: "takeover.offer",
    requestId: "r_AAAAAAAAAAAAAAAAAAAAAA",
    timestampUtc: "2026-08-13T12:00:00.000Z",
    payload: { browserDownloadId: 12 }
  });
});

test("accepts only a correlated response with a boolean decision", () => {
  const response = {
    protocolVersion: 1,
    kind: "response",
    requestId: "r_AAAAAAAAAAAAAAAAAAAAAA",
    timestampUtc: "2026-08-13T12:00:00.100Z",
    payload: { accepted: true, hostVersion: "0.1.0" }
  };
  assert.equal(parseNativeResponse(response, response.requestId)?.payload.accepted, true);
  assert.equal(parseNativeResponse(response, "r_wrong"), null);
  assert.equal(parseNativeResponse({ ...response, payload: { accepted: "yes" } }, response.requestId), null);
});

test("enforces the native message size cap", () => {
  assert.equal(fitsNativeMessageLimit({ ok: true }), true);
  assert.equal(fitsNativeMessageLimit({ data: "x".repeat(MAX_NATIVE_MESSAGE_BYTES) }), false);
});
