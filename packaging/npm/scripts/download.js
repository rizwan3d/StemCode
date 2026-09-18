"use strict";

// Downloads, verifies, and extracts the StemCode CLI binary and matching Voice
// runtime from the GitHub release. Shared by the postinstall step and runtime
// launcher so installations self-heal when lifecycle scripts were skipped.

const fs = require("fs");
const path = require("path");
const crypto = require("crypto");
const AdmZip = require("adm-zip");

const platform = require("./platform");

async function fetchBuffer(url, { allowNotFound = false } = {}) {
  if (typeof fetch !== "function") {
    throw new Error(
      "Global fetch is unavailable. StemCode's npm package requires Node.js 18 or newer."
    );
  }

  const response = await fetch(url, {
    redirect: "follow",
    headers: { "User-Agent": `${platform.APP_NAME}-npm-installer` },
  });

  if (response.status === 404 && allowNotFound) {
    return null;
  }

  if (!response.ok) {
    throw new Error(`Request to ${url} failed with HTTP ${response.status} ${response.statusText}.`);
  }

  return Buffer.from(await response.arrayBuffer());
}

function parseExpectedChecksum(checksumsText, assetName) {
  for (const rawLine of checksumsText.split(/\r?\n/)) {
    const line = rawLine.trim();
    if (!line) continue;

    const match = line.match(/^([0-9a-fA-F]{64})[\s*]+(.+)$/);
    if (!match) continue;

    let file = match[2].trim();
    file = file.replace(/^\*/, "").replace(/^\.\//, "");

    if (file === assetName) {
      return match[1].toLowerCase();
    }
  }
  return null;
}

function sha256(buffer) {
  return crypto.createHash("sha256").update(buffer).digest("hex").toLowerCase();
}

function extractCliRuntime(zipBuffer, destinationDir) {
  const zip = new AdmZip(zipBuffer);
  const wanted = platform.executableFileName();

  const entry = zip.getEntries().find((candidate) => {
    if (candidate.isDirectory) return false;
    const base = candidate.entryName.split("/").pop();
    return base === wanted;
  });

  if (!entry) {
    throw new Error(`Release archive did not contain the expected executable '${wanted}'.`);
  }

  fs.mkdirSync(destinationDir, { recursive: true });
  zip.extractAllTo(destinationDir, true);

  const destinationPath = path.join(destinationDir, wanted);
  if (!fs.existsSync(destinationPath)) {
    throw new Error(`StemCode CLI executable '${wanted}' was not installed from the release archive.`);
  }

  if (process.platform !== "win32") {
    fs.chmodSync(destinationPath, 0o755);
  }
}

function extractVoiceRuntime(zipBuffer, destinationDir) {
  fs.mkdirSync(destinationDir, { recursive: true });
  const zip = new AdmZip(zipBuffer);
  zip.extractAllTo(destinationDir, true);

  const voiceBinary = path.join(destinationDir, platform.voiceExecutableFileName());
  if (!fs.existsSync(voiceBinary)) {
    throw new Error(
      `Voice release archive did not contain the expected executable '${platform.voiceExecutableFileName()}'.`
    );
  }

  if (process.platform !== "win32") {
    fs.chmodSync(voiceBinary, 0o755);
  }
}

// Fetches and SHA256-verifies the release archive for a single tag.
// Returns the archive Buffer, or null when the asset is missing (HTTP 404).
async function downloadForTag(candidateTag, asset, log) {
  const base = platform.baseDownloadUrl(candidateTag);
  const assetUrl = `${base}/${asset}`;
  const checksumsUrl = `${base}/${platform.CHECKSUMS_NAME}`;

  log(`Downloading ${asset} (${candidateTag})...`);
  const archiveBuffer = await fetchBuffer(assetUrl, { allowNotFound: true });
  if (!archiveBuffer) {
    return null;
  }

  log(`Verifying ${platform.CHECKSUMS_NAME} for ${asset}...`);
  const checksumsText = (await fetchBuffer(checksumsUrl)).toString("utf8");
  const expected = parseExpectedChecksum(checksumsText, asset);
  if (!expected) {
    throw new Error(`${platform.CHECKSUMS_NAME} does not contain a checksum for ${asset}.`);
  }

  const actual = sha256(archiveBuffer);
  if (actual !== expected) {
    throw new Error(
      `SHA256 verification failed for ${asset}. Expected ${expected}, got ${actual}.`
    );
  }

  return archiveBuffer;
}

// Ensures the platform CLI and Voice runtime are present in vendor/. Returns the
// absolute CLI path. `onDownloaded` is awaited only for a fresh installation.
async function ensureBinary(options = {}) {
  const { force = false, log = () => {}, tag, onDownloaded } = options;

  const binaryPath = platform.installedBinaryPath();
  const voiceBinaryPath = platform.installedVoiceBinaryPath();
  if (!force && fs.existsSync(binaryPath) && fs.existsSync(voiceBinaryPath)) {
    return binaryPath;
  }

  const rid = platform.resolveRid();
  const asset = platform.assetName(rid);
  const voiceAsset = platform.voiceAssetName(rid);
  const resolvedTag = tag && tag.trim()
    ? tag.trim()
    : platform.resolveTag();

  // Release tags have shipped with uppercase V, lowercase v, and no prefix.
  // Use one tag for both archives so the CLI and Voice runtime match.
  const candidateTags = platform.releaseTagCandidates(resolvedTag);

  let archiveBuffer = null;
  let voiceArchiveBuffer = null;
  let usedTag = null;
  let lastError = null;
  for (const candidateTag of candidateTags) {
    try {
      const cliBuffer = await downloadForTag(candidateTag, asset, log);
      if (!cliBuffer) {
        continue;
      }

      const voiceBuffer = await downloadForTag(candidateTag, voiceAsset, log);
      if (!voiceBuffer) {
        continue;
      }

      archiveBuffer = cliBuffer;
      voiceArchiveBuffer = voiceBuffer;
      usedTag = candidateTag;
      break;
    } catch (err) {
      lastError = err;
    }
  }

  if (!archiveBuffer || !voiceArchiveBuffer) {
    if (lastError) {
      throw lastError;
    }
    throw new Error(
      `Could not find release assets ${asset} and ${voiceAsset} for tag ${candidateTags.join(" or ")}.`
    );
  }

  fs.mkdirSync(platform.vendorDir(), { recursive: true });
  const tempCliDir = path.join(platform.vendorDir(), `.cli.${process.pid}.tmp`);
  const tempVoiceDir = path.join(platform.vendorDir(), `.voice.${process.pid}.tmp`);

  try {
    log("Extracting StemCode CLI...");
    fs.rmSync(tempCliDir, { recursive: true, force: true });
    extractCliRuntime(archiveBuffer, tempCliDir);

    log("Extracting StemCode Voice runtime...");
    fs.rmSync(tempVoiceDir, { recursive: true, force: true });
    extractVoiceRuntime(voiceArchiveBuffer, tempVoiceDir);

    fs.cpSync(tempCliDir, platform.vendorDir(), { recursive: true, force: true });
    fs.rmSync(platform.voiceDir(), { recursive: true, force: true });
    fs.renameSync(tempVoiceDir, platform.voiceDir());
  } finally {
    fs.rmSync(tempCliDir, { recursive: true, force: true });
    fs.rmSync(tempVoiceDir, { recursive: true, force: true });
  }

  log(`Installed StemCode CLI to ${binaryPath} (${usedTag}).`);
  log(`Installed StemCode Voice runtime to ${platform.voiceDir()} (${usedTag}).`);

  // Fire once per genuine install. Updates pass force=true and are intentionally
  // excluded so they are not counted as new installs.
  if (!force && typeof onDownloaded === "function") {
    try {
      await onDownloaded();
    } catch {
      // Telemetry must never affect installation.
    }
  }

  return binaryPath;
}

module.exports = { ensureBinary, parseExpectedChecksum };

// Allow `node scripts/download.js` for manual/forced reinstall.
if (require.main === module) {
  ensureBinary({ force: true, log: (m) => console.error(`[stemcode] ${m}`) }).catch((err) => {
    console.error(`[stemcode] ${err.message}`);
    process.exit(1);
  });
}
