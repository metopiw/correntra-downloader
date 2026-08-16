import assert from "node:assert/strict";
import test from "node:test";
import { classifyMedia } from "../src/shared/classify";

test("classifies direct video and audio responses", () => {
  assert.equal(classifyMedia({ url: "https://cdn.example/video", contentType: "video/mp4; charset=binary" })?.kind, "video");
  assert.equal(classifyMedia({ url: "https://cdn.example/music.mp3" })?.kind, "audio");
});

test("classifies clear HLS and DASH manifests but ignores media segments", () => {
  assert.equal(classifyMedia({ url: "https://media.example/master.m3u8?signature=secret" })?.kind, "hls");
  assert.equal(classifyMedia({ url: "https://media.example/manifest", contentType: "application/dash+xml" })?.kind, "dash");
  assert.equal(classifyMedia({ url: "https://media.example/chunk-14.m4s", contentType: "video/iso.segment" }), null);
  assert.equal(classifyMedia({ url: "https://media.example/segment.ts", contentType: "video/mp2t" }), null);
});

test("recognises YouTube googlevideo audio variants without storing adapter code", () => {
  const result = classifyMedia({
    url: "https://r1---sn.example.googlevideo.com/videoplayback?itag=251&mime=audio%2Fwebm%3B+codecs%3Dopus&sig=secret",
    initiator: "https://www.youtube.com"
  });
  assert.equal(result?.kind, "audio");
  assert.equal(result?.source, "youtube");
  assert.equal(result?.quality, "Opus");
  assert.equal(result?.container, "webm");
});

test("adds Instagram and X source hints", () => {
  assert.equal(
    classifyMedia({ url: "https://scontent.cdninstagram.com/clip.mp4", contentType: "video/mp4", initiator: "https://www.instagram.com" })?.source,
    "instagram"
  );
  assert.equal(
    classifyMedia({ url: "https://video.twimg.com/ext_tw_video/master.m3u8", initiator: "https://x.com" })?.source,
    "x"
  );
});

test("rejects non-http and likely DRM licence endpoints", () => {
  assert.equal(classifyMedia({ url: "blob:https://example.test/abc", elementKind: "video" }), null);
  assert.equal(classifyMedia({ url: "https://example.test/widevine/license", contentType: "video/mp4" }), null);
});
