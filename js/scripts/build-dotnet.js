const { spawnSync } = require("child_process");
const fs = require("fs");
const path = require("path");

const packageRoot = path.resolve(__dirname, "..");
const repoRoot = path.resolve(packageRoot, "..");
const projectPath = path.join(repoRoot, "StemCode", "StemCode.csproj");
const publishDir = path.join(repoRoot, "StemCode", "bin", "Release", "net10.0", "publish");
const targetDir = path.join(packageRoot, "dotnet");

const publish = spawnSync(
  "dotnet",
  ["publish", projectPath, "-c", "Release", "-f", "net10.0"],
  {
    cwd: packageRoot,
    stdio: "inherit",
  },
);

if (publish.status !== 0) {
  process.exit(publish.status ?? 1);
}

fs.rmSync(targetDir, { recursive: true, force: true });
fs.mkdirSync(targetDir, { recursive: true });
fs.cpSync(publishDir, targetDir, { recursive: true });

console.log(`Copied StemCode .NET SDK publish output to ${targetDir}`);
