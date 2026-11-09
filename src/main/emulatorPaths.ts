import fs from "fs-extra";
import os from "os";
import path from "path";
import { EmulatorKind } from "../types";
import { isEdenFamily } from "../emulators";

// Eden uses these folder names (src/common/fs/fs_paths.h: EDEN_DIR, PORTABLE_DIR).
const EDEN_FAMILY_DIR = "eden";
const EDEN_FAMILY_PORTABLE_DIR = "user";
const TITLE_ID_PATTERN = /^[0-9a-f]{16}$/i;
const HOMEBREW_TITLE_ID = "0000000000000000";
const GAME_LIST_FILE_PATTERN = /^([0-9a-f]{16})\.(pv\.txt|appname\.txt|jpeg)$/i;
const MAX_ICON_BYTES = 2 * 1024 * 1024;

export type EmulatorTitleMeta = {
  name?: string,
  iconUrl?: string
};

export type EmulatorProgramLocation = {
  path: string,
  mode: "portable" | "global"
};

// Title IDs come from folder and file names, so make sure one can never escape the data folder.
export const assertSafeTitleId = (titleId: string): string => {
  if (typeof titleId !== "string" || titleId.length === 0 || titleId === "." || titleId === ".."
    || /[\\/]/.test(titleId) || path.basename(titleId) !== titleId) {
    throw new Error(`Invalid title ID: ${titleId}`);
  }

  return titleId;
};

const xdgDir = (envName: "XDG_DATA_HOME" | "XDG_CACHE_HOME", fallback: string) =>
  process.env[envName] || path.join(os.homedir(), fallback);

const samePath = (left: string, right: string) => {
  const a = path.resolve(left);
  const b = path.resolve(right);
  return process.platform === "linux" ? a === b : a.toLowerCase() === b.toLowerCase();
};

// Mirrors Reinitialize() in src/common/fs/path_util.cpp (non-portable branches).
export const getEdenFamilyGlobalDataDir = () => process.platform === "win32"
  ? path.join(process.env.APPDATA || path.join(os.homedir(), "AppData", "Roaming"), EDEN_FAMILY_DIR)
  : path.join(xdgDir("XDG_DATA_HOME", path.join(".local", "share")), EDEN_FAMILY_DIR);

// Windows and portable installs keep the cache inside the data folder. Global Linux and macOS installs
// split it out to XDG_CACHE_HOME, which is where the shader cache and game list cache live.
export const getEdenFamilyCacheDir = (dataRoot: string) => {
  if (process.platform !== "win32" && samePath(dataRoot, getEdenFamilyGlobalDataDir())) {
    return path.join(xdgDir("XDG_CACHE_HOME", ".cache"), EDEN_FAMILY_DIR);
  }

  return path.join(dataRoot, "cache");
};

// Eden 0.2.1 and older keep shader caches in <data>/shader. Newer Eden builds (June 2026, #4093)
// use <cache>/shader instead, and nothing moves the old folder, so either place can hold a game's cache.
const getEdenFamilyShaderRoots = (dataRoot: string) => ({
  current: path.join(getEdenFamilyCacheDir(dataRoot), "shader"),
  legacy: path.join(dataRoot, "shader")
});

const isDirectory = (dir: string) => {
  try {
    return fs.statSync(dir).isDirectory();
  } catch {
    return false;
  }
};

export const getShaderDir = (kind: EmulatorKind, dataRoot: string, titleId: string) => {
  const id = assertSafeTitleId(titleId).toLowerCase();

  if (!isEdenFamily(kind)) {
    return path.resolve(dataRoot, "games", id, "cache", "shader");
  }

  // Prefer the folder that already holds this game's cache, then the layout the installed version created.
  const { current, legacy } = getEdenFamilyShaderRoots(dataRoot);
  if (isDirectory(path.join(current, id))) return path.resolve(current, id);
  if (isDirectory(path.join(legacy, id))) return path.resolve(legacy, id);
  return path.resolve(isDirectory(legacy) && !isDirectory(current) ? legacy : current, id);
};

export const getModsDir = (kind: EmulatorKind, dataRoot: string, titleId: string) => {
  const id = assertSafeTitleId(titleId);

  return isEdenFamily(kind)
    ? path.resolve(dataRoot, "load", id.toUpperCase())
    : path.resolve(dataRoot, "mods", "contents", id.toLowerCase());
};

const readEntryNames = async (dir: string, keep: (entry: fs.Dirent) => boolean) => {
  try {
    const entries = await fs.readdir(dir, { withFileTypes: true });
    return entries.filter(keep).map(entry => entry.name);
  } catch {
    return [];
  }
};

// Eden lists every game in the user's game folders in cache/game_list (on by default), and only
// creates shader/<tid> and load/<TID> for games it has seen, so the union covers all known games.
const scanEdenFamilyGames = async (dataRoot: string) => {
  const cacheDir = getEdenFamilyCacheDir(dataRoot);
  const titleIds = new Set<string>();
  const add = (titleId: string) => {
    if (TITLE_ID_PATTERN.test(titleId) && titleId !== HOMEBREW_TITLE_ID) {
      titleIds.add(titleId.toLowerCase());
    }
  };

  for (const name of await readEntryNames(path.join(cacheDir, "game_list"), entry => entry.isFile())) {
    const match = GAME_LIST_FILE_PATTERN.exec(name);
    if (match) add(match[1]);
  }

  const { current, legacy } = getEdenFamilyShaderRoots(dataRoot);
  for (const shaderRoot of [current, legacy]) {
    for (const name of await readEntryNames(shaderRoot, entry => entry.isDirectory())) {
      add(name);
    }
  }

  for (const name of await readEntryNames(path.join(dataRoot, "load"), entry => entry.isDirectory())) {
    add(name);
  }

  return Array.from(titleIds).sort();
};

export const scanGames = async (kind: EmulatorKind, dataRoot: string): Promise<string[]> => {
  if (isEdenFamily(kind)) {
    return scanEdenFamilyGames(dataRoot);
  }

  const directories = await readEntryNames(path.join(dataRoot, "games"), entry => entry.isDirectory());
  return directories.map(name => name.toLowerCase());
};

const imageMimeType = (data: Buffer) => data.length >= 4 && data[0] === 0x89 && data[1] === 0x50 && data[2] === 0x4e && data[3] === 0x47
  ? "image/png"
  : "image/jpeg";

export const getEmulatorTitleMeta = async (kind: EmulatorKind, dataRoot: string | undefined, titleId: string): Promise<EmulatorTitleMeta> => {
  if (!dataRoot) return {};

  try {
    assertSafeTitleId(titleId);
  } catch {
    return {};
  }

  if (!isEdenFamily(kind)) {
    try {
      const metadata = await fs.readJson(path.resolve(dataRoot, "games", titleId.toLowerCase(), "gui", "metadata.json"));
      return metadata.title ? { name: metadata.title } : {};
    } catch {
      return {};
    }
  }

  const gameListDir = path.join(getEdenFamilyCacheDir(dataRoot), "game_list");
  const fileStem = titleId.toUpperCase();
  const meta: EmulatorTitleMeta = {};

  try {
    const name = (await fs.readFile(path.join(gameListDir, `${fileStem}.appname.txt`), "utf-8")).replace(/\0/g, "").trim();
    if (name) meta.name = name;
  } catch {
    // No cached name for this title.
  }

  try {
    const icon = await fs.readFile(path.join(gameListDir, `${fileStem}.jpeg`));
    if (icon.length > 0 && icon.length <= MAX_ICON_BYTES) {
      meta.iconUrl = `data:${imageMimeType(icon)};base64,${icon.toString("base64")}`;
    }
  } catch {
    // No cached icon for this title.
  }

  return meta;
};

export const deleteGameData = async (kind: EmulatorKind, dataRoot: string, titleId: string) => {
  assertSafeTitleId(titleId);

  if (!isEdenFamily(kind)) {
    await fs.remove(path.resolve(dataRoot, "games", titleId.toLowerCase()));

    // Ryujinx loads a mod folder whatever the letter case of its name, and Linux keeps both cases apart.
    const modsRoot = path.resolve(dataRoot, "mods", "contents");
    for (const name of await readEntryNames(modsRoot, entry => entry.name.toLowerCase() === titleId.toLowerCase())) {
      await fs.remove(path.join(modsRoot, name));
    }
    return;
  }

  const gameListDir = path.join(getEdenFamilyCacheDir(dataRoot), "game_list");
  const fileStem = titleId.toUpperCase();
  const { current, legacy } = getEdenFamilyShaderRoots(dataRoot);
  const targets = [
    path.resolve(current, titleId.toLowerCase()),
    path.resolve(legacy, titleId.toLowerCase()),
    getModsDir(kind, dataRoot, titleId),
    ...["pv.txt", "appname.txt", "jpeg"].map(extension => path.join(gameListDir, `${fileStem}.${extension}`))
  ];

  for (const target of targets) {
    await fs.remove(target).catch((): null => null);
  }
};

export const programMatchesKind = (kind: EmulatorKind, programPath: string) => {
  const name = path.basename(programPath).toLowerCase();

  if (kind === "eden") return name.includes("eden");
  return name.includes("ryujinx") || name.includes("ryubing");
};

// A "user" folder next to the program makes Eden portable (path_util.cpp Reinitialize).
export const resolveEdenFamilyDataRoot = async (programPath: string): Promise<EmulatorProgramLocation> => {
  const portableDir = path.join(path.dirname(programPath), EDEN_FAMILY_PORTABLE_DIR);

  try {
    if ((await fs.stat(portableDir)).isDirectory()) {
      return { path: portableDir, mode: "portable" };
    }
  } catch {
    // No portable folder next to the program.
  }

  return { path: getEdenFamilyGlobalDataDir(), mode: "global" };
};
