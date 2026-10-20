import { app, BrowserWindow, shell } from "electron";
import child_process from "child_process";
import fs from "fs-extra";
import path from "path";
import semver from "semver";
import type { RequestInit } from "node-fetch";
import HttpService, { getReleaseFeedUrl } from "../services/HttpService";
import { appendUpdateLog } from "./updateLog";
import { getPlatformAssetPattern, GithubReleaseJson, parseRelease, RELEASES_PAGE_URL, resolveExpectedSha256, selectReleaseAsset } from "./releaseFeed";
import { downloadVerified, DownloadResponse, sha256File } from "./verifiedDownload";
import { applyZipPackage } from "./packageApply";

// In-app updater, modelled on MDPro4's: every update gets its own staging folder (updates/<version>/) with a
// bootstrap log and an apply log, downloads are verified against the release's SHA-256 before anything runs,
// Windows hands off to a small helper that owns the install while RyuSAK is closed, and the outcome is
// recorded in updates/last-update.json so the next launch can report it.

export type UpdateStage = "checking" | "downloading" | "verified" | "handoff" | "installing";

export interface UpdateProgress {
  stage: UpdateStage;
  version?: string;
  received?: number;
  total?: number;
}

export interface UpdateResult {
  ok: boolean;
  error?: string;
  logPath?: string;
  openedReleasePage?: boolean;
}

export interface UpdateStatus {
  schema?: string;
  result: "success" | "rolled-back" | "aborted" | "failed";
  fromVersion?: string;
  targetVersion?: string;
  message?: string;
  logPath?: string;
  recoveryPath?: string;
  finishedAt?: string;
  acknowledged?: boolean;
}

const HELPER_FILE_NAME = "RyuSAK.UpdateHelper.exe";
const HELPER_RECEIPT_NAME = "build-receipt.json";
const STATUS_FILE_NAME = "last-update.json";
const LOCK_FILE_NAME = "update.lock";
const JOB_SCHEMA = "ryusak-update-job-v1";
const STATUS_SCHEMA = "ryusak-update-status-v1";
const LOCK_MAX_AGE_MS = 30 * 60 * 1000;
const KEPT_UPDATE_FOLDERS = 3;
const LINUX_EXECUTABLES = ["RyuSAK", "chrome_crashpad_handler", "chrome-sandbox"];

const errorMessage = (error: unknown) => (error instanceof Error ? error.message : String(error));

const isInside = (child: string, parent: string) => {
  const relative = path.relative(path.resolve(parent), path.resolve(child));
  return !!relative && relative.split(path.sep)[0] !== ".." && !path.isAbsolute(relative);
};

// Cleanup must never stop at the first file that is still in use (e.g. a helper that is just exiting).
const removeQuietly = (target: string) => {
  try {
    fs.removeSync(target);
  } catch (error) {
    console.error(`Could not remove ${target}`, error);
  }
};

export default class AppUpdater {
  private busy = false;

  constructor(private readonly isPortable: boolean, private readonly cacheDir: string) {}

  // Standard Windows installs keep downloads out of the roaming profile; portable builds keep them next to RyuSAK.
  public getUpdatesRoot() {
    if (process.platform === "win32" && !this.isPortable && process.env.LOCALAPPDATA) {
      return path.join(process.env.LOCALAPPDATA, "RyuSAK", "updates");
    }

    return path.join(this.cacheDir, "updates");
  }

  // One updater per install (MDPro4's fix after two overlapping updates broke an install): while the helper owns
  // the install, a new RyuSAK window would lock the files it is replacing, so RyuSAK waits for it instead.
  public isUpdateInProgress() {
    try {
      const lock = fs.readJsonSync(path.join(this.getUpdatesRoot(), LOCK_FILE_NAME));
      const startedAt = Date.parse(lock?.startedAt);
      if (typeof lock?.pid !== "number" || isNaN(startedAt) || Date.now() - startedAt > LOCK_MAX_AGE_MS) {
        return false;
      }

      process.kill(lock.pid, 0);
      return true;
    } catch (error) {
      return (error as NodeJS.ErrnoException)?.code === "EPERM";
    }
  }

  public readLastStatus(): UpdateStatus | null {
    const status = this.readStatusFile();
    return status && !status.acknowledged ? status : null;
  }

  public acknowledgeLastStatus() {
    const status = this.readStatusFile();
    if (status && !status.acknowledged) {
      this.writeStatusFile({ ...status, acknowledged: true });
    }
  }

  public async openLog(target: string) {
    const resolved = path.resolve(String(target || ""));
    if (!isInside(resolved, this.getUpdatesRoot()) || !(await fs.pathExists(resolved))) {
      return false;
    }

    return (await shell.openPath(resolved)) === "";
  }

  // Keeps the logs of the last few updates and drops leftovers (downloads of versions already installed,
  // half-finished downloads, extracted packages). Recovery files from a failed rollback are never removed.
  public cleanUpOldUpdates() {
    const root = this.getUpdatesRoot();
    if (!fs.existsSync(root)) {
      return;
    }

    try {
      const recoveryPath = this.readStatusFile()?.recoveryPath;
      const currentVersion = app.getVersion();
      const folders = fs.readdirSync(root, { withFileTypes: true })
        .filter((entry) => entry.isDirectory())
        .map((entry) => ({ folder: path.join(root, entry.name), modified: fs.statSync(path.join(root, entry.name)).mtimeMs }))
        .sort((left, right) => right.modified - left.modified);

      folders.forEach(({ folder }, index) => {
        if (recoveryPath && (isInside(recoveryPath, folder) || path.resolve(recoveryPath) === path.resolve(folder))) {
          return;
        }

        if (index >= KEPT_UPDATE_FOLDERS) {
          removeQuietly(folder);
          return;
        }

        const version = semver.valid(path.basename(folder));
        const alreadyInstalled = !!version && semver.lte(version, currentVersion);
        fs.readdirSync(folder).forEach((name) => {
          const leftover = /\.partial$/i.test(name)
            || name === "extract"
            || (alreadyInstalled && (/\.(exe|zip)$/i.test(name) || name === "helper"));
          if (leftover) {
            removeQuietly(path.join(folder, name));
          }
        });
      });
    } catch (error) {
      console.error("Could not clean up old RyuSAK updates.", error);
    }
  }

  public async installUpdate(mainWindow: BrowserWindow): Promise<UpdateResult> {
    if (this.busy) {
      return { ok: false, error: "An update is already being prepared." };
    }

    this.busy = true;
    let logPath: string = null;
    const sendProgress = (progress: UpdateProgress) => {
      if (mainWindow && !mainWindow.isDestroyed()) {
        mainWindow.webContents.send("update-progress", progress);
      }
    };

    try {
      const assetPattern = getPlatformAssetPattern(process.platform, process.arch);
      if (!assetPattern) {
        // No self-updating package for this platform (e.g. macOS): open the releases page instead.
        await shell.openExternal(RELEASES_PAGE_URL);
        return { ok: true, openedReleasePage: true };
      }

      if (!app.isPackaged) {
        throw new Error("Updates can only be installed by a packaged RyuSAK build.");
      }

      sendProgress({ stage: "checking" });
      const response = await HttpService.request(getReleaseFeedUrl());
      if (!response.ok) {
        throw new Error(`GitHub release check failed with ${response.status} ${response.statusText}`);
      }

      const release = parseRelease(await response.json() as GithubReleaseJson);
      const asset = selectReleaseAsset(release, assetPattern);
      const currentVersion = app.getVersion();
      const stagingDir = path.join(this.getUpdatesRoot(), release.version);
      await fs.ensureDir(stagingDir);

      const bootstrapLogPath = path.join(stagingDir, "update-bootstrap.log");
      logPath = bootstrapLogPath;
      const log = (message: string) => appendUpdateLog(bootstrapLogPath, message);
      log(`Preparing the update from ${currentVersion} to ${release.version} (${asset.name}).`);

      const expected = await resolveExpectedSha256(release, asset, async (url) => {
        const checksumResponse = await HttpService.request(url);
        if (!checksumResponse.ok) {
          throw new Error(`Checksum download failed with ${checksumResponse.status} ${checksumResponse.statusText}`);
        }
        return checksumResponse.text();
      });
      log(`Expecting SHA-256 ${expected.sha256} (${expected.source}).`);

      const packagePath = path.join(stagingDir, asset.name);
      let lastProgressAt = 0;
      const download = await downloadVerified({
        url: asset.downloadUrl,
        destination: packagePath,
        sha256: expected.sha256,
        size: asset.size,
        fetch: (url, init) => HttpService.request(url, { signal: init.signal as unknown as RequestInit["signal"] }) as unknown as Promise<DownloadResponse>,
        onProgress: (received, total) => {
          const now = Date.now();
          if (now - lastProgressAt >= 150 || received === total) {
            lastProgressAt = now;
            sendProgress({ stage: "downloading", version: release.version, received, total });
          }
        }
      });
      log(download.reused ? `Reusing the verified download ${packagePath}` : `Downloaded and verified ${packagePath}`);
      sendProgress({ stage: "verified", version: release.version });

      if (process.platform === "win32") {
        await this.handOffToWindowsHelper(stagingDir, packagePath, expected.sha256, asset.size, currentVersion, release.version, log);
        sendProgress({ stage: "handoff", version: release.version });
        setTimeout(() => app.quit(), 500);
        return { ok: true, logPath };
      }

      sendProgress({ stage: "installing", version: release.version });
      logPath = path.join(stagingDir, "apply-update.log");
      const applied = await this.applyLinuxPackage(stagingDir, packagePath, currentVersion, release.version, logPath);
      if (!applied.ok) {
        return { ok: false, error: applied.error, logPath };
      }

      // app.relaunch starts the new build only after this process has exited, so the single-instance lock is free.
      app.relaunch({ args: ["--no-sandbox"] });
      app.quit();
      return { ok: true, logPath };
    } catch (error) {
      const message = errorMessage(error);
      if (logPath) {
        appendUpdateLog(logPath, `Update failed: ${message}`);
      }
      console.error(error);
      return { ok: false, error: message, logPath };
    } finally {
      this.busy = false;
    }
  }

  private async handOffToWindowsHelper(stagingDir: string, installerPath: string, installerSha256: string, installerSize: number, currentVersion: string, targetVersion: string, log: (message: string) => void) {
    const bundledDir = path.join(process.resourcesPath, "updater");
    const bundledHelper = path.join(bundledDir, HELPER_FILE_NAME);
    if (!(await fs.pathExists(bundledHelper))) {
      throw new Error("This RyuSAK build doesn't include its update helper. Please download the update from GitHub instead.");
    }

    // Like MDPro4's build receipt: only run the helper that the build recorded.
    const receipt = await fs.readJson(path.join(bundledDir, HELPER_RECEIPT_NAME)).catch((): null => null);
    const helperSha256 = await sha256File(bundledHelper);
    if (!receipt || String(receipt.sha256).toLowerCase() !== helperSha256) {
      throw new Error("RyuSAK's update helper doesn't match its build receipt. Please reinstall RyuSAK from GitHub.");
    }

    // The helper runs from the staging folder so setup is free to replace the copy inside the install.
    const helperDir = path.join(stagingDir, "helper");
    await fs.emptyDir(helperDir);
    const helperPath = path.join(helperDir, HELPER_FILE_NAME);
    await fs.copy(bundledHelper, helperPath);
    if (await sha256File(helperPath) !== helperSha256) {
      throw new Error("RyuSAK couldn't copy its update helper.");
    }

    const job = {
      schema: JOB_SCHEMA,
      installerPath,
      installerSha256,
      installerSize,
      installDir: path.dirname(app.getPath("exe")),
      launchExe: app.getPath("exe"),
      waitPid: process.pid,
      currentVersion,
      targetVersion,
      portable: this.isPortable,
      logPath: path.join(stagingDir, "apply-update.log"),
      installerLogPath: path.join(stagingDir, "installer.log"),
      statusPath: path.join(this.getUpdatesRoot(), STATUS_FILE_NAME),
      lockPath: path.join(this.getUpdatesRoot(), LOCK_FILE_NAME),
      backupDir: path.join(stagingDir, "backup"),
      // Portable settings live in electron_cache inside the install; setup never touches them and neither does the helper.
      preserve: ["electron_cache"],
      handoffTimeoutSeconds: 120,
      installTimeoutSeconds: 900
    };
    const jobPath = path.join(helperDir, "update-job.json");
    await fs.writeJson(jobPath, job, { spaces: 2 });

    log(`Launching helper from ${helperPath} with job ${jobPath}`);
    const child = child_process.spawn(helperPath, ["--job", jobPath], {
      detached: true,
      stdio: "ignore",
      windowsHide: false
    });
    await new Promise<void>((resolve, reject) => {
      child.once("spawn", () => resolve());
      child.once("error", reject);
    });
    child.unref();
    log("Helper launch request accepted by Windows.");
  }

  private async applyLinuxPackage(stagingDir: string, packagePath: string, currentVersion: string, targetVersion: string, logPath: string) {
    const log = (message: string) => appendUpdateLog(logPath, message);
    log(`Starting the update from ${currentVersion} to ${targetVersion} using ${packagePath}`);

    const result = await applyZipPackage({
      zipPath: packagePath,
      installDir: path.dirname(app.getPath("exe")),
      workDir: stagingDir,
      targetVersion,
      executableName: "RyuSAK",
      versionMarker: "resources/app/package.json",
      manifestName: ".ryusak-files.json",
      // Settings live in electron_cache, and the portable marker stays exactly as the user has it.
      preserve: ["electron_cache", "portable"],
      executables: LINUX_EXECUTABLES,
      log
    });

    if (result.ok) {
      log("Update applied successfully.");
      await fs.remove(packagePath).then(
        () => log(`Removed downloaded package ${packagePath}`),
        (error) => log(`Could not remove ${packagePath}: ${errorMessage(error)}`)
      );
      this.writeStatus({ result: "success", fromVersion: currentVersion, targetVersion, message: `RyuSAK ${targetVersion} was installed.`, logPath });
      return result;
    }

    if (result.recoveryPath) {
      // RyuSAK keeps running, but record it so the problem is still reported after a restart.
      this.writeStatus({ result: "failed", fromVersion: currentVersion, targetVersion, message: result.error, logPath, recoveryPath: result.recoveryPath });
      return { ...result, error: `${result.error} Some files couldn't be restored; the originals were kept in ${result.recoveryPath}.` };
    }

    return result.changed
      ? { ...result, error: `${result.error} RyuSAK restored the previous version.` }
      : result;
  }

  private readStatusFile(): UpdateStatus | null {
    try {
      const status = fs.readJsonSync(path.join(this.getUpdatesRoot(), STATUS_FILE_NAME));
      return status && typeof status.result === "string" ? status : null;
    } catch {
      return null;
    }
  }

  private writeStatusFile(status: UpdateStatus) {
    try {
      fs.ensureDirSync(this.getUpdatesRoot());
      fs.writeJsonSync(path.join(this.getUpdatesRoot(), STATUS_FILE_NAME), status, { spaces: 2 });
    } catch (error) {
      console.error("Could not record the update result.", error);
    }
  }

  private writeStatus(status: UpdateStatus) {
    this.writeStatusFile({ schema: STATUS_SCHEMA, ...status, finishedAt: new Date().toISOString(), acknowledged: false });
  }
}
