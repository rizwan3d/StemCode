"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("fs");
const os = require("os");
const path = require("path");
const crypto = require("crypto");

const download = require("../scripts/download");
const platform = require("../scripts/platform");

function sha256(buffer) {
  return crypto.createHash("sha256").update(buffer).digest("hex").toLowerCase();
}

function makeZip(entries) {
  const AdmZip = require("adm-zip");
  const zip = new AdmZip();
  for (const [name, contents] of Object.entries(entries)) {
    zip.addFile(name, Buffer.from(contents));
  }
  return zip.toBuffer();
}

function fakeResponse(body, status = 200) {
  const buffer = Buffer.isBuffer(body) ? body : Buffer.from(body);
  return {
    ok: status >= 200 && status < 400,
    status,
    statusText: status === 404 ? "Not Found" : "OK",
    async arrayBuffer() {
      return buffer.buffer.slice(buffer.byteOffset, buffer.byteOffset + buffer.byteLength);
    },
  };
}

// Serves release assets for each tag and emits checksums for every prepared
// archive so the CLI and Voice runtime are verified from the same release.
function makeFetch({ missingTags, assetsByTag }) {
  return async (url) => {
    const match = url.match(/\/releases\/download\/([^/]+)\/(.+)$/);
    assert.ok(match, `unexpected request url: ${url}`);
    const [, tag, file] = match;

    if (missingTags.has(tag)) {
      return fakeResponse("", 404);
    }

    const assets = assetsByTag.get(tag);
    assert.ok(assets, `no prepared assets for tag ${tag}`);

    if (file === platform.CHECKSUMS_NAME) {
      const checksums = [...assets.entries()]
        .map(([name, buffer]) => `${sha256(buffer)}  ${name}`)
        .join("\n");
      return fakeResponse(`${checksums}\n`);
    }

    const archive = assets.get(file);
    return archive ? fakeResponse(archive) : fakeResponse("", 404);
  };
}

async function withDownloadOverrides(overrides, fn) {
  const originals = {
    fetch: global.fetch,
    installedBinaryPath: platform.installedBinaryPath,
    installedVoiceBinaryPath: platform.installedVoiceBinaryPath,
    vendorDir: platform.vendorDir,
    voiceDir: platform.voiceDir,
    cliVersion: process.env.STEMCODE_CLI_VERSION,
  };
  try {
    global.fetch = overrides.fetch;
    platform.installedBinaryPath = overrides.installedBinaryPath;
    platform.installedVoiceBinaryPath = overrides.installedVoiceBinaryPath;
    platform.vendorDir = overrides.vendorDir;
    platform.voiceDir = overrides.voiceDir;
    if (overrides.cliVersion === undefined) {
      delete process.env.STEMCODE_CLI_VERSION;
    } else {
      process.env.STEMCODE_CLI_VERSION = overrides.cliVersion;
    }
    return await fn();
  } finally {
    global.fetch = originals.fetch;
    platform.installedBinaryPath = originals.installedBinaryPath;
    platform.installedVoiceBinaryPath = originals.installedVoiceBinaryPath;
    platform.vendorDir = originals.vendorDir;
    platform.voiceDir = originals.voiceDir;
    if (originals.cliVersion === undefined) {
      delete process.env.STEMCODE_CLI_VERSION;
    } else {
      process.env.STEMCODE_CLI_VERSION = originals.cliVersion;
    }
  }
}

function createReleaseAssets(tag, cliContents, voiceContents) {
  const rid = platform.resolveRid();
  const cliAsset = platform.assetName(rid);
  const voiceAsset = platform.voiceAssetName(rid);
  const cliZip = makeZip({
    [platform.executableFileName()]: cliContents,
    "onnxruntime-native-file.txt": "native-runtime-content",
  });
  const voiceZip = makeZip({
    [platform.voiceExecutableFileName()]: voiceContents,
    "voice-native-file.txt": "native-runtime-content",
  });

  return new Map([
    [tag, new Map([
      [cliAsset, cliZip],
      [voiceAsset, voiceZip],
    ])],
  ]);
}

test("ensureBinary falls back to the alternate-case tag when the primary 404s", async () => {
  const executable = platform.executableFileName();
  const voiceExecutable = platform.voiceExecutableFileName();
  const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "stemcode-dl-"));
  const voiceDir = path.join(tempDir, "voice");

  try {
    const fetch = makeFetch({
      missingTags: new Set(["V1.1.10"]),
      assetsByTag: createReleaseAssets("v1.1.10", "fake-stemcode-binary", "fake-voice-binary"),
    });

    const result = await withDownloadOverrides(
      {
        fetch,
        installedBinaryPath: () => path.join(tempDir, executable),
        installedVoiceBinaryPath: () => path.join(voiceDir, voiceExecutable),
        vendorDir: () => tempDir,
        voiceDir: () => voiceDir,
        cliVersion: "1.1.10", // resolveTag() => V1.1.10 (primary)
      },
      () => download.ensureBinary({ force: true, log: () => {} })
    );

    assert.equal(result, path.join(tempDir, executable));
    assert.ok(fs.existsSync(result), "CLI binary should be extracted to disk");
    assert.ok(fs.existsSync(path.join(tempDir, "onnxruntime-native-file.txt")), "CLI native files should be retained");
    assert.ok(fs.existsSync(path.join(voiceDir, voiceExecutable)), "Voice runtime should be extracted");
    assert.ok(fs.existsSync(path.join(voiceDir, "voice-native-file.txt")), "Voice native files should be retained");
  } finally {
    fs.rmSync(tempDir, { recursive: true, force: true });
  }
});

test("ensureBinary uses the primary tag when it resolves successfully", async () => {
  const executable = platform.executableFileName();
  const voiceExecutable = platform.voiceExecutableFileName();
  const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "stemcode-dl-"));
  const voiceDir = path.join(tempDir, "voice");

  try {
    const fetch = makeFetch({
      missingTags: new Set(),
      assetsByTag: createReleaseAssets("V1.1.10", "fake-stemcode-binary-primary", "fake-voice-binary-primary"),
    });

    const result = await withDownloadOverrides(
      {
        fetch,
        installedBinaryPath: () => path.join(tempDir, executable),
        installedVoiceBinaryPath: () => path.join(voiceDir, voiceExecutable),
        vendorDir: () => tempDir,
        voiceDir: () => voiceDir,
        cliVersion: "1.1.10", // resolveTag() => V1.1.10 (primary)
      },
      () => download.ensureBinary({ force: true, log: () => {} })
    );

    assert.equal(result, path.join(tempDir, executable));
    assert.ok(fs.existsSync(result), "CLI binary should be extracted to disk");
    assert.ok(fs.existsSync(path.join(tempDir, "onnxruntime-native-file.txt")), "CLI native files should be retained");
    assert.ok(fs.existsSync(path.join(voiceDir, voiceExecutable)), "Voice runtime should be extracted");
    assert.ok(fs.existsSync(path.join(voiceDir, "voice-native-file.txt")), "Voice native files should be retained");
  } finally {
    fs.rmSync(tempDir, { recursive: true, force: true });
  }
});
