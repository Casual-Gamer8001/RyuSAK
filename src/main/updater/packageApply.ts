import AdmZip from "adm-zip";
import fs from "fs-extra";
import path from "path";

// Applies a full app package (the Linux/Steam Deck zip) to the running install the way MDPro4 applies its patches:
// validate everything first, journal every change (originals are moved aside, never overwritten), write the
// version marker last, and put every original back if any step fails.

export interface ApplyZipPackageOptions {
  zipPath: string;
  installDir: string;
  workDir: string;
  targetVersion: string;
  executableName: string;
  versionMarker: string;
  manifestName: string;
  preserve: string[];
  executables: string[];
  log: (message: string) => void;
  retryDelayMs?: number;
}

export interface ApplyZipPackageResult {
  ok: boolean;
  // false when the update stopped before any install file was touched
  changed: boolean;
  error?: string;
  // set when the rollback couldn't restore everything; the originals are kept here
  recoveryPath?: string;
  written?: number;
  deleted?: number;
}

interface JournalEntry {
  target: string;
  label: string;
  backup: string | null;
  placed: boolean;
}

const MANIFEST_SCHEMA = "ryusak-installed-files-v1";

const errorMessage = (error: unknown) => (error instanceof Error ? error.message : String(error));

const delay = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

const lstatOrNull = (target: string) => fs.lstat(target).catch((): null => null);

const isPreserved = (key: string, preserve: string[]) =>
  preserve.some((entry) => key === entry || key.indexOf(`${entry}/`) === 0);

const listFiles = async (root: string, prefix = ""): Promise<string[]> => {
  const files: string[] = [];
  const entries = await fs.readdir(prefix ? path.join(root, prefix) : root, { withFileTypes: true });

  for (let index = 0; index < entries.length; index++) {
    const entry = entries[index];
    const key = prefix ? `${prefix}/${entry.name}` : entry.name;

    if (entry.isDirectory()) {
      const children = await listFiles(root, key);
      for (let child = 0; child < children.length; child++) {
        files.push(children[child]);
      }
    } else {
      files.push(key);
    }
  }

  return files;
};

// Refuses absolute paths, ".." escapes and anything that would be written through a symlinked folder.
const resolveInside = async (root: string, key: string) => {
  const parts = key.split("/");

  if (!key || path.isAbsolute(key) || key.indexOf(":") !== -1 || key.indexOf("\\") !== -1 || parts.some((part) => part === "" || part === "." || part === "..")) {
    throw new Error(`Invalid update path: ${key}`);
  }

  const target = path.join(root, ...parts);
  if (target.indexOf(root + path.sep) !== 0) {
    throw new Error(`Refusing to touch a path outside the install directory: ${key}`);
  }

  let current = root;
  for (let index = 0; index < parts.length - 1; index++) {
    current = path.join(current, parts[index]);
    const stat = await lstatOrNull(current);
    if (!stat) {
      break;
    }
    if (stat.isSymbolicLink()) {
      throw new Error(`Update path crosses a symbolic link: ${key}`);
    }
  }

  return target;
};

// rename() keeps running binaries valid on Linux. Across file systems it falls back to copy + rename.
const moveFile = async (source: string, destination: string) => {
  try {
    await fs.rename(source, destination);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== "EXDEV") {
      throw error;
    }

    const temporary = `${destination}.ryusak-partial`;
    try {
      await fs.copy(source, temporary, { overwrite: true, preserveTimestamps: true });
      await fs.rename(temporary, destination);
    } catch (copyError) {
      await fs.remove(temporary).catch((): void => undefined);
      throw copyError;
    }
    await fs.remove(source);
  }
};

const withRetry = async (action: () => Promise<unknown>, description: string, log: (message: string) => void, delayMs: number, attempts = 5) => {
  for (let attempt = 1; ; attempt++) {
    try {
      await action();
      return;
    } catch (error) {
      if (attempt >= attempts) {
        throw error;
      }
      log(`Retry ${attempt} failed for ${description}: ${errorMessage(error)}`);
      await delay(delayMs);
    }
  }
};

const ensureParentDirs = async (root: string, directory: string, created: string[]) => {
  const missing: string[] = [];
  let current = directory;

  while (current.length > root.length && !(await lstatOrNull(current))) {
    missing.unshift(current);
    current = path.dirname(current);
  }

  for (let index = 0; index < missing.length; index++) {
    await fs.mkdir(missing[index]);
    created.push(missing[index]);
  }
};

const findPackageRoot = async (extractDir: string, executableName: string) => {
  if (await fs.pathExists(path.join(extractDir, executableName))) {
    return extractDir;
  }

  const entries = await fs.readdir(extractDir, { withFileTypes: true });
  const match = entries.find((entry) => entry.isDirectory() && fs.existsSync(path.join(extractDir, entry.name, executableName)));
  if (!match) {
    throw new Error(`The update package doesn't contain ${executableName}.`);
  }

  return path.join(extractDir, match.name);
};

const readPackageVersion = async (markerPath: string) => {
  try {
    return String((await fs.readJson(markerPath)).version || "");
  } catch {
    return "";
  }
};

const readManifest = async (manifestPath: string): Promise<string[]> => {
  try {
    const manifest = await fs.readJson(manifestPath);
    return Array.isArray(manifest?.files) ? manifest.files.filter((file: unknown) => typeof file === "string") : [];
  } catch {
    return [];
  }
};

export const applyZipPackage = async (options: ApplyZipPackageOptions): Promise<ApplyZipPackageResult> => {
  const log = options.log;
  const retryDelayMs = options.retryDelayMs ?? 300;
  const installRoot = path.resolve(options.installDir);
  const extractDir = path.join(options.workDir, "extract");
  const backupDir = path.join(options.workDir, "backup");
  const markerKey = options.versionMarker;
  const journal: JournalEntry[] = [];
  const createdDirs: string[] = [];
  let changesStarted = false;

  try {
    await fs.remove(extractDir);
    await fs.ensureDir(extractDir);
    if (await fs.pathExists(backupDir)) {
      // Keep an earlier attempt's recovery files instead of overwriting them.
      await fs.move(backupDir, `${backupDir}-${Date.now()}`);
    }

    // keepOriginalPermission keeps the Unix permission bits stored in the archive.
    const archive = new AdmZip(options.zipPath);
    (archive.extractAllTo as (targetPath: string, overwrite?: boolean, keepOriginalPermission?: boolean) => void)(extractDir, true, true);

    const packageRoot = await findPackageRoot(extractDir, options.executableName);
    const packagedVersion = await readPackageVersion(path.join(packageRoot, ...markerKey.split("/")));
    if (packagedVersion !== options.targetVersion) {
      throw new Error(`The update package contains version ${packagedVersion || "unknown"} instead of ${options.targetVersion}.`);
    }

    const packageKeys = (await listFiles(packageRoot)).filter((key) => !isPreserved(key, options.preserve));
    // The version marker goes last, so an interrupted update still reports the old version (MDPro4 does the same).
    packageKeys.sort((left, right) => {
      const markerOrder = (left === markerKey ? 1 : 0) - (right === markerKey ? 1 : 0);
      return markerOrder || (left < right ? -1 : left > right ? 1 : 0);
    });

    const packageKeySet = new Set(packageKeys);
    const previousKeys = await readManifest(path.join(installRoot, options.manifestName));
    const deletions: Array<{ key: string; target: string }> = [];
    for (let index = 0; index < previousKeys.length; index++) {
      const key = previousKeys[index];
      if (packageKeySet.has(key) || isPreserved(key, options.preserve)) {
        continue;
      }
      const target = await resolveInside(installRoot, key);
      const stat = await lstatOrNull(target);
      if (stat && !stat.isDirectory()) {
        deletions.push({ key, target });
      }
    }

    const writes: Array<{ key: string; source: string; target: string }> = [];
    for (let index = 0; index < packageKeys.length; index++) {
      const key = packageKeys[index];
      const target = await resolveInside(installRoot, key);
      const stat = await lstatOrNull(target);
      if (stat && stat.isDirectory()) {
        throw new Error(`A folder is in the way of ${key}.`);
      }
      writes.push({ key, source: path.join(packageRoot, ...key.split("/")), target });
    }

    // The file list of this update decides what the next update may delete. It is journaled like any other file.
    const manifestSource = path.join(options.workDir, `${options.manifestName}.new`);
    await fs.writeJson(manifestSource, { schema: MANIFEST_SCHEMA, version: options.targetVersion, files: packageKeys }, { spaces: 2 });
    writes.push({ key: options.manifestName, source: manifestSource, target: await resolveInside(installRoot, options.manifestName) });

    log(`Validated the v${options.targetVersion} package: ${packageKeys.length} files to install, ${deletions.length} old files to remove.`);

    await fs.ensureDir(backupDir);
    changesStarted = true;
    let backupIndex = 0;
    const nextBackupPath = () => path.join(backupDir, String(backupIndex++));

    for (let index = 0; index < deletions.length; index++) {
      const deletion = deletions[index];
      const entry: JournalEntry = { target: deletion.target, label: deletion.key, backup: null, placed: false };
      const backup = nextBackupPath();
      journal.push(entry);
      await withRetry(() => moveFile(deletion.target, backup), `delete ${deletion.key}`, log, retryDelayMs);
      entry.backup = backup;
    }

    for (let index = 0; index < writes.length; index++) {
      const write = writes[index];
      await ensureParentDirs(installRoot, path.dirname(write.target), createdDirs);

      const entry: JournalEntry = { target: write.target, label: write.key, backup: null, placed: false };
      journal.push(entry);
      if (await lstatOrNull(write.target)) {
        const backup = nextBackupPath();
        await withRetry(() => moveFile(write.target, backup), `back up ${write.key}`, log, retryDelayMs);
        entry.backup = backup;
      }

      await withRetry(() => moveFile(write.source, write.target), `install ${write.key}`, log, retryDelayMs);
      entry.placed = true;
    }

    if (process.platform !== "win32") {
      for (let index = 0; index < options.executables.length; index++) {
        const target = path.join(installRoot, options.executables[index]);
        if (await fs.pathExists(target)) {
          await fs.chmod(target, 0o755);
        }
      }
    }

    await fs.remove(backupDir).catch((error) => log(`Could not remove ${backupDir}: ${errorMessage(error)}`));
    await fs.remove(extractDir).catch((error) => log(`Could not remove ${extractDir}: ${errorMessage(error)}`));
    log(`Installed ${packageKeys.length} files and removed ${deletions.length} old files.`);
    return { ok: true, changed: true, written: packageKeys.length, deleted: deletions.length };
  } catch (error) {
    const message = errorMessage(error);
    log(`Update failed: ${message}`);

    if (!changesStarted) {
      await fs.remove(extractDir).catch((): void => undefined);
      return { ok: false, changed: false, error: `${message} No files were changed.` };
    }

    log("Restoring the previous version...");
    let recoveryFailed = false;
    for (let index = journal.length - 1; index >= 0; index--) {
      const entry = journal[index];
      try {
        if (entry.placed) {
          await withRetry(() => fs.remove(entry.target), `remove ${entry.label}`, log, retryDelayMs);
        }
        if (entry.backup) {
          await withRetry(() => moveFile(entry.backup, entry.target), `restore ${entry.label}`, log, retryDelayMs);
        }
      } catch (rollbackError) {
        recoveryFailed = true;
        log(`Rollback failed for ${entry.target}: ${errorMessage(rollbackError)}`);
      }
    }

    for (let index = createdDirs.length - 1; index >= 0; index--) {
      await fs.rmdir(createdDirs[index]).catch((rmdirError) => log(`Rollback could not remove folder ${createdDirs[index]}: ${errorMessage(rmdirError)}`));
    }

    if (recoveryFailed) {
      log(`Recovery files retained at ${backupDir}`);
      return { ok: false, changed: true, error: message, recoveryPath: backupDir };
    }

    log("Rolled back the unsuccessful update.");
    await fs.remove(backupDir).catch((): void => undefined);
    await fs.remove(extractDir).catch((): void => undefined);
    return { ok: false, changed: true, error: message };
  }
};
