import { app, BrowserWindow } from "electron";
import fs from "fs-extra";
import path from "path";
import Zip from "adm-zip";
import HttpService from "../services/HttpService";
import { GameBananaFile } from "../../types";

export type getGameBananaModFilesProps = [number];
export type installGameBananaModProps = [string, number, number, string];

const getModPathForTitleId = (id: string, dataPath: string) => path.resolve(dataPath, "mods", "contents", id.toLocaleLowerCase());

const sanitizePathSegment = (value: string) => Array.from(value)
  .map(char => (char.charCodeAt(0) < 32 || /[<>:"/\\|?*]/.test(char)) ? "_" : char)
  .join("")
  .trim() || "GameBanana Mod";

const manualInstallGuidance = "This mod may still be compatible with the game, but RyuSAK could not auto-install it safely. Please download it from GameBanana and follow the mod author's manual install instructions.";

const walkFiles = async (dir: string): Promise<string[]> => {
  const entries = await fs.readdir(dir, { withFileTypes: true });
  const files: string[] = [];

  for (const entry of entries) {
    const fullPath = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      files.push(...await walkFiles(fullPath));
    } else {
      files.push(fullPath);
    }
  }

  return files;
};

const copyDirectoryContents = async (source: string, target: string) => {
  await fs.ensureDir(target);
  const entries = await fs.readdir(source);

  for (const entry of entries) {
    await fs.copy(path.join(source, entry), path.join(target, entry), { overwrite: true });
  }
};

const getRelativeParts = (file: string, root: string) => path.relative(root, file).split(path.sep).filter(Boolean);

type LayoutCandidate = {
  sourceRoot: string,
  sourceRootParts: string[],
  destinationName: string,
  fileCount: number,
  contentDirs: Set<string>,
  hasTitleId: boolean,
};

const extractZipSafely = async (zipPath: string, extractDir: string) => {
  const archive = new Zip(zipPath);
  const extractRoot = path.resolve(extractDir);

  for (const entry of archive.getEntries()) {
    if (entry.isDirectory) continue;

    const parts = entry.entryName.replace(/\\/g, "/").split("/").filter(Boolean);
    if (parts.length === 0 || parts.includes("..") || parts[0] === "__MACOSX") continue;

    const target = path.resolve(extractRoot, ...parts);
    if (!target.startsWith(`${extractRoot}${path.sep}`)) {
      throw new Error("Archive contains an unsafe file path.");
    }

    await fs.ensureDir(path.dirname(target));
    await fs.writeFile(target, entry.getData());
  }
};

const summarizeExtractedLayout = async (extractDir: string, files: string[]) => {
  const topLevelEntries = (await fs.readdir(extractDir).catch(() => []))
    .filter(name => name !== "__MACOSX")
    .slice(0, 8);
  const contentDirExamples = files
    .map(file => getRelativeParts(file, extractDir))
    .filter(parts => parts.some(part => ["romfs", "exefs"].includes(part.toLowerCase())))
    .map(parts => {
      const index = parts.findIndex(part => ["romfs", "exefs"].includes(part.toLowerCase()));
      return parts.slice(0, index + 1).join("/");
    })
    .filter((value, index, values) => values.indexOf(value) === index)
    .slice(0, 5);
  const patchCount = files.filter(file => file.toLowerCase().endsWith(".pchtxt")).length;

  return [
    topLevelEntries.length > 0 ? `Top-level entries: ${topLevelEntries.join(", ")}` : "The archive did not contain readable top-level entries.",
    contentDirExamples.length > 0 ? `Detected mod folders: ${contentDirExamples.join(", ")}` : "No romfs or exefs folder was found.",
    patchCount > 0 ? `Detected ${patchCount} pchtxt patch file(s).` : "No pchtxt patch files were found."
  ].join(" ");
};

const installPatchFiles = async (files: string[], targetDir: string, modFolderName: string) => {
  const patchFiles = files.filter(file => file.toLowerCase().endsWith(".pchtxt"));
  if (patchFiles.length === 0) return false;

  const exefsDir = path.join(targetDir, modFolderName, "exefs");
  await fs.ensureDir(exefsDir);

  for (const file of patchFiles) {
    await fs.copy(file, path.join(exefsDir, path.basename(file)), { overwrite: true });
  }

  return true;
};

const candidateScore = (candidate: LayoutCandidate) =>
  (candidate.hasTitleId ? 1000 : 0)
  + (candidate.contentDirs.size > 1 ? 100 : 0)
  + (candidate.sourceRootParts.length > 0 ? 20 : 0)
  + candidate.fileCount;

const findLayoutCandidates = (files: string[], extractDir: string, titleId: string, modFolderName: string) => {
  const titleIdLower = titleId.toLowerCase();
  const candidates = new Map<string, LayoutCandidate>();

  for (const file of files) {
    const sourceParts = getRelativeParts(file, extractDir);
    const lowerParts = sourceParts.map(part => part.toLowerCase());
    const modContentIndex = lowerParts.findIndex(part => part === "romfs" || part === "exefs");
    if (modContentIndex === -1) continue;

    const sourceRootParts = sourceParts.slice(0, modContentIndex);
    const sourceRoot = sourceRootParts.length > 0 ? path.join(extractDir, ...sourceRootParts) : extractDir;
    const sourceRootLowerParts = sourceRootParts.map(part => part.toLowerCase());
    const rootName = sourceRootParts[sourceRootParts.length - 1];
    const rootNameLower = rootName?.toLowerCase();
    const hasTitleId = sourceRootLowerParts.includes(titleIdLower);
    const shouldWrap = sourceRootParts.length === 0 || rootNameLower === titleIdLower;
    const destinationName = shouldWrap ? modFolderName : sanitizePathSegment(rootName);
    const key = sourceRoot.toLowerCase();
    const candidate = candidates.get(key) || {
      sourceRoot,
      sourceRootParts,
      destinationName,
      fileCount: 0,
      contentDirs: new Set<string>(),
      hasTitleId
    };

    candidate.fileCount += 1;
    candidate.contentDirs.add(lowerParts[modContentIndex]);
    candidate.hasTitleId = candidate.hasTitleId || hasTitleId;
    candidates.set(key, candidate);
  }

  return Array.from(candidates.values()).sort((a, b) => candidateScore(b) - candidateScore(a));
};

const installRecognizedModLayout = async (extractDir: string, titleId: string, targetDir: string, modFolderName: string) => {
  const files = await walkFiles(extractDir);
  const candidates = findLayoutCandidates(files, extractDir, titleId, modFolderName);
  const bestCandidate = candidates[0];

  if (bestCandidate) {
    const tiedCandidates = candidates.filter(candidate => candidateScore(candidate) === candidateScore(bestCandidate));
    if (tiedCandidates.length > 1) {
      const names = tiedCandidates.map(candidate => candidate.sourceRootParts.join("/") || "(archive root)").slice(0, 5).join(", ");
      throw new Error(`${manualInstallGuidance} RyuSAK found multiple possible mod folders and could not safely choose one: ${names}.`);
    }

    const destination = path.join(targetDir, bestCandidate.destinationName);
    await copyDirectoryContents(bestCandidate.sourceRoot, destination);
    return destination;
  }

  if (await installPatchFiles(files, targetDir, modFolderName)) {
    return path.join(targetDir, modFolderName);
  }

  const summary = await summarizeExtractedLayout(extractDir, files);
  throw new Error(`${manualInstallGuidance} RyuSAK auto-install currently supports ZIP files containing romfs, exefs, or pchtxt layouts. ${summary}`);
};

export const getGameBananaModFiles = async (...args: getGameBananaModFilesProps): Promise<GameBananaFile[]> => {
  const [modId] = args;
  const response = await HttpService.gameBananaModFiles(modId);
  const files = response?.[1] || {};

  return Object.values(files)
    .filter(file => !file._bIsArchived && file._sDownloadUrl)
    .map(file => ({
      id: file._idRow,
      name: file._sFile,
      size: file._nFilesize,
      downloadUrl: file._sDownloadUrl,
      description: file._sDescription
    }));
};

export const installGameBananaMod = async (mainWindow: BrowserWindow, ...args: installGameBananaModProps): Promise<string> => {
  const [titleId, modId, fileId, dataPath] = args;
  const files = await getGameBananaModFiles(modId);
  const file = files.find(item => item.id === fileId);

  if (!file) {
    throw new Error("Selected GameBanana file is no longer available.");
  }

  const targetDir = getModPathForTitleId(titleId, dataPath);
  const tempDir = path.join(app.getPath("temp"), "ryusak-gamebanana-mods", `${modId}-${fileId}-${Date.now()}`);
  const downloadPath = path.join(tempDir, sanitizePathSegment(file.name));
  const modFolderName = sanitizePathSegment(path.basename(file.name, path.extname(file.name)));
  const extension = path.extname(file.name).toLowerCase();

  await fs.ensureDir(tempDir);
  await fs.ensureDir(targetDir);

  try {
    const result = await HttpService.downloadGameBananaFile(file, downloadPath, mainWindow, `gamebanana-${fileId}`);
    if (!result) {
      throw new Error("Unable to download the selected GameBanana file.");
    }

    if (extension === ".pchtxt") {
      const exefsDir = path.join(targetDir, modFolderName, "exefs");
      await fs.ensureDir(exefsDir);
      await fs.copy(downloadPath, path.join(exefsDir, path.basename(file.name)), { overwrite: true });
      return path.join(targetDir, modFolderName);
    }

    if (extension !== ".zip") {
      const archiveNote = [".7z", ".rar"].includes(extension)
        ? " This looks like a compressed archive type that RyuSAK does not extract yet."
        : "";
      throw new Error(`${manualInstallGuidance} RyuSAK can currently auto-install GameBanana .zip archives and direct .pchtxt patch files only. Selected file: ${file.name}.${archiveNote}`);
    }

    const extractDir = path.join(tempDir, "extracted");
    await extractZipSafely(downloadPath, extractDir);

    const installPath = await installRecognizedModLayout(extractDir, titleId, targetDir, modFolderName);
    return installPath;
  } finally {
    await fs.remove(tempDir).catch(() => null);
  }
};
