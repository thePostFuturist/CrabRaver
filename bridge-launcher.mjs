#!/usr/bin/env node
// Cross-platform launcher for Bridge MCP server.
// Uses only Node.js built-in modules — works everywhere Claude Code runs.
//
// Binary search order:
//   1. Project-local:  {scriptDir}/bin/publish/{rid}/
//   2. User-global:    ~/.digitraver/mcp/bridge/{rid}/
//   3. Auto-download from GitHub Releases → overwrites cache in-place
//   4. Fallback: dotnet run (dev only, requires .NET SDK)
//
// Environment variables:
//   BRIDGE_DEBUG=1    — inject --verbose flag for debug logging
//   BRIDGE_VERSION    — override version (default: read from VERSION file)
//   BRIDGE_REPO       — override GitHub repo for downloads

import { spawn, execFileSync } from "node:child_process";
import { existsSync, readFileSync, mkdirSync, chmodSync, createWriteStream, unlinkSync, writeFileSync, statSync } from "node:fs";
import { join, dirname } from "node:path";
import { platform, arch, homedir } from "node:os";
import { fileURLToPath } from "node:url";
import { get as httpsGet } from "node:https";

// ── Configuration ────────────────────────────────────────────────────
const SCRIPT_DIR = dirname(fileURLToPath(import.meta.url));
const BINARY_NAME = "DigitRaverHelperMCP";
const REPO = process.env.BRIDGE_REPO || "thePostFuturist/CrabRaver";

function readVersion() {
  const versionFile = join(SCRIPT_DIR, "VERSION");
  if (existsSync(versionFile)) {
    return readFileSync(versionFile, "utf-8").trim();
  }
  return "1.0.0";
}

const VERSION = process.env.BRIDGE_VERSION || readVersion();

function diag(msg) {
  process.stderr.write(`[bridge-mcp] ${msg}\n`);
}

// ── Detect platform → RID ────────────────────────────────────────────
function detectRid() {
  const p = platform();
  const a = arch();
  switch (p) {
    case "win32":
      return "win-x64";
    case "darwin":
      return a === "arm64" ? "osx-arm64" : "osx-x64";
    case "linux":
      return a === "arm64" ? "linux-arm64" : "linux-x64";
    default:
      diag(`Unsupported platform: ${p}-${a}`);
      process.exit(1);
  }
}

const RID = detectRid();
const EXE = RID.startsWith("win-") ? `${BINARY_NAME}.exe` : BINARY_NAME;

// ── Release asset naming ─────────────────────────────────────────────
function releaseAssetName() {
  switch (RID) {
    case "win-x64":     return `${BINARY_NAME}.exe`;
    case "osx-arm64":   return `${BINARY_NAME}-osx-arm64`;
    case "osx-x64":     return `${BINARY_NAME}-osx-x64`;
    case "linux-x64":   return `${BINARY_NAME}-linux-x64`;
    case "linux-arm64":  return `${BINARY_NAME}-linux-arm64`;
    default:            return `${BINARY_NAME}-${RID}`;
  }
}

// ── Search for binary ────────────────────────────────────────────────
const LOCAL_DIR = join(SCRIPT_DIR, "bin", "publish", RID);
const CACHE_DIR = join(homedir(), ".digitraver", "mcp", "bridge", RID);
const CACHE_VERSION_FILE = join(CACHE_DIR, ".version");

let binary = "";
let needsUpdate = false;

if (existsSync(join(LOCAL_DIR, EXE))) {
  binary = join(LOCAL_DIR, EXE);
  diag(`Using local build: ${binary}`);
} else if (existsSync(join(CACHE_DIR, EXE))) {
  // Check if cached version matches current VERSION
  const cachedVersion = existsSync(CACHE_VERSION_FILE)
    ? readFileSync(CACHE_VERSION_FILE, "utf-8").trim()
    : "";
  if (cachedVersion !== VERSION) {
    diag(`Cached binary is v${cachedVersion || "unknown"}, need v${VERSION}. Updating...`);
    needsUpdate = true;
  } else {
    binary = join(CACHE_DIR, EXE);
    diag(`Using cached binary: ${binary} (v${VERSION})`);
  }
}

// ── Download helper (follows one redirect) ───────────────────────────
function download(url, dest) {
  return new Promise((resolve, reject) => {
    diag(`Downloading: ${url}`);
    httpsGet(url, (res) => {
      // GitHub releases redirect to objects.githubusercontent.com
      if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
        diag(`Following redirect...`);
        httpsGet(res.headers.location, (res2) => {
          if (res2.statusCode !== 200) {
            reject(new Error(`HTTP ${res2.statusCode} from redirect target`));
            return;
          }
          const file = createWriteStream(dest);
          res2.pipe(file);
          file.on("finish", () => file.close(resolve));
          file.on("error", reject);
        }).on("error", reject);
        return;
      }
      if (res.statusCode !== 200) {
        reject(new Error(`HTTP ${res.statusCode}: ${url}`));
        return;
      }
      const file = createWriteStream(dest);
      res.pipe(file);
      file.on("finish", () => file.close(resolve));
      file.on("error", reject);
    }).on("error", reject);
  });
}

// ── Download if not found or outdated ────────────────────────────────
if (!binary || needsUpdate) {
  const asset = releaseAssetName();
  const url = `https://github.com/${REPO}/releases/download/v${VERSION}/${asset}`;

  diag(`Binary not found locally. Downloading v${VERSION} for ${RID}...`);
  mkdirSync(CACHE_DIR, { recursive: true });

  const dest = join(CACHE_DIR, EXE);
  try {
    await download(url, dest);
    if (!RID.startsWith("win-")) {
      chmodSync(dest, 0o755);
    }
    writeFileSync(CACHE_VERSION_FILE, VERSION);
    binary = dest;
    diag(`Installed v${VERSION} to: ${dest}`);
  } catch (err) {
    diag(`Download failed: ${err.message}`);
    try { unlinkSync(dest); } catch {}
  }
}

// ── Fallback: dotnet run ─────────────────────────────────────────────
if (!binary) {
  let hasDotnet = false;
  try {
    execFileSync("dotnet", ["--version"], { stdio: "ignore" });
    hasDotnet = true;
  } catch {}

  if (hasDotnet) {
    diag("Falling back to 'dotnet run' (requires .NET 8 SDK)");
    const args = ["run", "--project", SCRIPT_DIR, "--"];
    if (process.env.BRIDGE_DEBUG === "1") args.push("--verbose");
    args.push(...process.argv.slice(2));

    const child = spawn("dotnet", args, { stdio: "inherit" });
    child.on("exit", (code, signal) => {
      process.exit(signal ? 1 : (code ?? 1));
    });
  } else {
    diag("ERROR: No binary found and .NET SDK not available.");
    diag(`Install from: https://github.com/${REPO}/releases/tag/v${VERSION}`);
    diag("Or install .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0");
    process.exit(1);
  }
}

// ── Spawn binary ─────────────────────────────────────────────────────
if (binary) {
  const args = [];
  if (process.env.BRIDGE_DEBUG === "1") args.push("--verbose");
  args.push(...process.argv.slice(2));

  const child = spawn(binary, args, { stdio: "inherit" });

  // Forward signals to child
  for (const sig of ["SIGINT", "SIGTERM", "SIGHUP"]) {
    process.on(sig, () => child.kill(sig));
  }

  child.on("exit", (code, signal) => {
    process.exit(signal ? 1 : (code ?? 1));
  });
}
