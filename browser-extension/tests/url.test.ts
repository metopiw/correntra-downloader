import assert from "node:assert/strict";
import test from "node:test";
import {
  baseNameFromPath,
  canonicalCandidateUrl,
  parseCaptureUrl,
  redactSensitiveText,
  redactUrlForDisplay,
  siteKeyFromUrl,
  storageSafeUrl
} from "../src/shared/url";

test("URL display and canonical forms remove query signatures and fragments", () => {
  const input = "https://CDN.Example.test/video/master.m3u8?token=secret&expires=123#fragment";
  assert.equal(redactUrlForDisplay(input), "https://cdn.example.test/video/master.m3u8");
  assert.equal(canonicalCandidateUrl(input), "https://cdn.example.test/video/master.m3u8");
  assert.equal(siteKeyFromUrl(input), "cdn.example.test");
});

test("credential-bearing and unsafe schemes are rejected", () => {
  assert.equal(parseCaptureUrl("https://user:password@example.test/file"), null);
  assert.equal(parseCaptureUrl("file:///C:/secret.txt"), null);
  assert.equal(parseCaptureUrl("javascript:alert(1)"), null);
});

test("only query-free URLs are eligible for session storage", () => {
  assert.equal(storageSafeUrl("https://example.test/file.mp4"), "https://example.test/file.mp4");
  assert.equal(storageSafeUrl("https://example.test/file.mp4?sig=secret"), undefined);
});

test("diagnostic text redacts common credential forms", () => {
  const redacted = redactSensitiveText("Authorization: Bearer abc.def token=x&signature=y Cookie=session-secret; next=value");
  assert.doesNotMatch(redacted, /abc\.def|session-secret|signature=y|token=x/);
  assert.match(redacted, /REDACTED/);
});

test("download paths expose only a safe base name", () => {
  assert.equal(baseNameFromPath("C:\\Users\\Name\\Downloads\\movie.mp4"), "movie.mp4");
  assert.equal(baseNameFromPath("../../bad:name.exe"), "bad name.exe");
});
