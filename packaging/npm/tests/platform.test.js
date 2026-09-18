"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");

const {
  resolveTag,
  resolveVersion,
  alternateTag,
  releaseTagCandidates,
} = require("../scripts/platform.js");

function withEnv(vars, fn) {
  const prev = {};
  for (const key of Object.keys(vars)) {
    prev[key] = process.env[key];
    if (vars[key] === undefined) {
      delete process.env[key];
    } else {
      process.env[key] = vars[key];
    }
  }
  try {
    return fn();
  } finally {
    for (const key of Object.keys(vars)) {
      if (prev[key] === undefined) {
        delete process.env[key];
      } else {
        process.env[key] = prev[key];
      }
    }
  }
}

test("resolveTag uses an uppercase V to match published release assets", () => {
  const tag = withEnv(
    { STEMCODE_CLI_TAG: undefined, STEMCODE_CLI_VERSION: "1.1.10" },
    () => resolveTag()
  );
  assert.equal(tag, "V1.1.10");
  assert.ok(!tag.startsWith("v"), "default tag must not be lowercase v");
});

test("resolveTag honors an explicit STEMCODE_CLI_TAG override", () => {
  const tag = withEnv(
    { STEMCODE_CLI_TAG: "v9.9.9-rc1", STEMCODE_CLI_VERSION: undefined },
    () => resolveTag()
  );
  assert.equal(tag, "v9.9.9-rc1");
});

test("resolveVersion strips a leading v or V from the override", () => {
  const version = withEnv({ STEMCODE_CLI_VERSION: "V2.0.0" }, () =>
    resolveVersion()
  );
  assert.equal(version, "2.0.0");
});

test("alternateTag swaps an uppercase V to lowercase v", () => {
  assert.equal(alternateTag("V1.1.10"), "v1.1.10");
});

test("alternateTag swaps a lowercase v to uppercase V", () => {
  assert.equal(alternateTag("v1.1.10"), "V1.1.10");
});

test("alternateTag leaves tags without a v/V prefix unchanged", () => {
  assert.equal(alternateTag("1.1.10"), "1.1.10");
  assert.equal(alternateTag(""), "");
  assert.equal(alternateTag(undefined), undefined);
});

test("releaseTagCandidates includes v, V, and unprefixed release tags", () => {
  assert.deepEqual(releaseTagCandidates("V1.1.17"), ["V1.1.17", "v1.1.17", "1.1.17"]);
  assert.deepEqual(releaseTagCandidates("v1.1.17"), ["v1.1.17", "V1.1.17", "1.1.17"]);
  assert.deepEqual(releaseTagCandidates("1.1.17"), ["1.1.17"]);
});
