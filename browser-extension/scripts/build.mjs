import { cp, mkdir, rm } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { build } from "esbuild";

const extensionRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const outputDirectory = path.join(extensionRoot, "dist");

await rm(outputDirectory, { recursive: true, force: true });
await mkdir(outputDirectory, { recursive: true });

await build({
  absWorkingDir: extensionRoot,
  entryPoints: {
    background: "src/background.ts",
    content: "src/content.ts",
    popup: "src/popup.ts"
  },
  outdir: outputDirectory,
  bundle: true,
  format: "esm",
  platform: "browser",
  target: ["chrome102", "edge102"],
  sourcemap: false,
  minify: false,
  legalComments: "eof",
  charset: "utf8"
});

await cp(path.join(extensionRoot, "static"), outputDirectory, { recursive: true });
