import { app, BrowserWindow, ipcMain } from "electron";
import { ChildProcess, spawn } from "child_process";
import fs from "fs-extra";
import path from "path";
import os from "os";

// Runs the bundled shader builder (shaderbuilder/RyuSAK.ShaderBuilder), which adds shaders from the game's own files
// to Ryubing's shader cache. It prints one JSON object per line: progress events, then a result or an error.

export type ShaderBuilderCommand = "status" | "inspect" | "build" | "undo";

export type ShaderBuilderOutcome = {
  ok: boolean,
  code?: string,
  message?: string,
  result?: Record<string, any>,
};

export type runShaderBuilderProps = [ShaderBuilderCommand, string, string, string?];

const BUILDER_FILE_NAME = process.platform === "win32" ? "RyuSAK.ShaderBuilder.exe" : "RyuSAK.ShaderBuilder";
const RIDS: Record<string, string> = {
  "win32-x64": "win-x64",
  "win32-arm64": "win-arm64",
  "linux-x64": "linux-x64",
  "linux-arm64": "linux-arm64",
  "darwin-x64": "osx-x64",
  "darwin-arm64": "osx-arm64"
};

const runningJobs = new Map<string, { child: ChildProcess, writing: boolean }>();

const findShaderBuilder = async (): Promise<string | null> => {
  const rid = RIDS[`${process.platform}-${process.arch}`];
  const candidates = [
    path.join(process.resourcesPath, "shaderbuilder", BUILDER_FILE_NAME),
    // Development (electron-forge start): the helper published by scripts/build-shader-builder.js.
    path.resolve(app.getAppPath(), ".shader-builder", rid || "", BUILDER_FILE_NAME),
    path.resolve(app.getAppPath(), "..", "..", ".shader-builder", rid || "", BUILDER_FILE_NAME)
  ];

  for (const candidate of candidates) {
    if (await fs.pathExists(candidate)) {
      return candidate;
    }
  }

  return null;
};

const parseLine = (line: string): Record<string, any> | null => {
  try {
    return JSON.parse(line);
  } catch {
    return null;
  }
};

ipcMain.on("cancel-download", (_, abortKey: string) => {
  const job = runningJobs.get(abortKey);

  // Once the builder starts writing, it finishes: it swaps the new files in only at the very end.
  if (job && !job.writing) {
    job.child.kill();
  }
});

export const runShaderBuilder = async (mainWindow: BrowserWindow, ...args: runShaderBuilderProps): Promise<ShaderBuilderOutcome> => {
  const [command, titleId, dataPath, jobKey] = args;
  const builder = await findShaderBuilder();

  if (!builder) {
    return { ok: false, code: "BUILDER_MISSING", message: "The shader builder is missing from this copy of RyuSAK." };
  }

  if (process.platform !== "win32") {
    await fs.chmod(builder, 0o755).catch(() => null);
  }

  const key = jobKey || `${command}-${titleId}`;
  if (runningJobs.has(key)) {
    return { ok: false, code: "BUSY", message: "RyuSAK is already working on this game's shaders." };
  }

  return new Promise<ShaderBuilderOutcome>((resolve) => {
    // The index remembers which game file holds which title, so later builds don't open every file again.
    const indexPath = path.join(app.getPath("userData"), "shader-builder-index.json");
    const child = spawn(builder, [command, "--data", dataPath, "--title", titleId.toLowerCase(), "--index", indexPath, "--json"], {
      cwd: os.tmpdir(),
      windowsHide: true
    });
    const job = { child, writing: false };
    let outcome: ShaderBuilderOutcome = null;
    let pending = "";
    let errorOutput = "";

    runningJobs.set(key, job);

    const handleLine = (line: string) => {
      const event = parseLine(line.trim());
      if (!event) {
        return;
      }

      if (event.event === "progress") {
        if (event.phase === "write") {
          job.writing = true;
        }

        mainWindow.webContents.send("shader-builder-progress", key, event.phase, +event.done || 0, +event.total || 0, event.message || "");
      } else if (event.event === "result") {
        outcome = { ok: true, result: event };
      } else if (event.event === "error") {
        outcome = { ok: false, code: event.code, message: event.message };
      }
    };

    child.stdout.setEncoding("utf8");
    child.stdout.on("data", (chunk: string) => {
      pending += chunk;
      const lines = pending.split(/\r?\n/);
      pending = lines.pop();
      lines.forEach(handleLine);
    });

    child.stderr.setEncoding("utf8");
    child.stderr.on("data", (chunk: string) => {
      errorOutput = (errorOutput + chunk).slice(-4000);
    });

    child.on("error", (error) => {
      outcome = outcome || { ok: false, code: "BUILDER_FAILED", message: error.message };
    });

    child.on("close", (exitCode, signal) => {
      runningJobs.delete(key);
      if (pending) {
        handleLine(pending);
      }

      if (!outcome && signal) {
        outcome = { ok: false, code: "CANCELLED", message: "Cancelled." };
      }

      resolve(outcome || {
        ok: false,
        code: "BUILDER_FAILED",
        message: `The shader builder stopped unexpectedly (exit code ${exitCode}). ${errorOutput.trim()}`.trim()
      });
    });
  });
};

// Copies the game's shader cache, minus the shaders RyuSAK built, into a temporary folder for sharing.
export const exportShadersForSharing = async (titleId: string, dataPath: string): Promise<string | null> => {
  const builder = await findShaderBuilder();
  if (!builder) {
    return null;
  }

  const outputDir = path.join(app.getPath("temp"), `ryusak-share-${titleId}-${Date.now()}`);

  return new Promise<string | null>((resolve) => {
    const child = spawn(builder, ["export", "--data", dataPath, "--title", titleId.toLowerCase(), "--out", outputDir, "--json"], {
      cwd: os.tmpdir(),
      windowsHide: true
    });
    let output = "";

    child.stdout.setEncoding("utf8");
    child.stdout.on("data", (chunk: string) => output += chunk);
    child.on("error", () => resolve(null));
    child.on("close", (exitCode) => {
      const succeeded = exitCode === 0 && output.split(/\r?\n/).map(parseLine).some(event => event?.event === "result");
      resolve(succeeded ? outputDir : null);
    });
  });
};
