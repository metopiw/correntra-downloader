import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { access, readFile } from "node:fs/promises";
import test from "node:test";

const expectedExtensionId = "fbngehclfngjenhlchnkojooliaifggj";

test("public manifest key produces the registered stable extension ID", async () => {
  const manifest = JSON.parse(await readFile("static/manifest.json", "utf8")) as {
    key: string;
    icons: Record<string, string>;
  };

  const digest = createHash("sha256")
    .update(Buffer.from(manifest.key, "base64"))
    .digest()
    .subarray(0, 16);
  const extensionId = [...digest]
    .flatMap((byte) => [byte >>> 4, byte & 0x0f])
    .map((nibble) => String.fromCharCode("a".charCodeAt(0) + nibble))
    .join("");

  assert.equal(extensionId, expectedExtensionId);
  for (const iconPath of Object.values(manifest.icons)) {
    await access(`static/${iconPath}`);
  }
});

