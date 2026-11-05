import path from "path";
import fs from "fs-extra";
import { app, BrowserWindow, dialog } from "electron";
import { EmulatorKind, RyusakEmulatorGames } from "../../types";
import { EMULATOR_PROGRAM_NAMES, isEdenFamily, normalizeKind } from "../../emulators";
import {
  deleteGameData,
  EmulatorProgramLocation,
  getEdenFamilyGlobalDataDir,
  programMatchesKind,
  resolveEdenFamilyDataRoot,
  scanGames
} from "../emulatorPaths";

export const getRyujinxPath = (): string => {
  if (process.platform === "win32") {
    return path.resolve(app.getPath("appData"), "Ryujinx");
  }

  const home = app.getPath("home");
  const linuxCandidates = [
    path.resolve(home, ".var", "app", "org.ryujinx.Ryujinx", "config", "Ryujinx"),
    path.resolve(home, ".var", "app", "org.ryubing.Ryubing", "config", "Ryujinx"),
    path.resolve(home, ".var", "app", "org.ryubing.Ryubing", "config", "Ryubing"),
    path.resolve(home, ".config", "Ryujinx"),
    path.resolve(home, ".config", "Ryubing")
  ];

  return linuxCandidates.find(candidate => fs.existsSync(candidate)) || linuxCandidates[0];
};

export const getDefaultEmulatorDataPath = (emulator?: EmulatorKind): string =>
  isEdenFamily(normalizeKind(emulator)) ? getEdenFamilyGlobalDataDir() : getRyujinxPath();

export const scanGamesForConfig = async (dataPath: string, emulator?: EmulatorKind): Promise<RyusakEmulatorGames> => {
  try {
    return await scanGames(normalizeKind(emulator), dataPath);
  } catch(e) {
    return [];
  }
};

export type deleteGameProps = [string, string, EmulatorKind?];

export const deleteGame = async (...args: deleteGameProps) => {
  const [titleId, dataPath, emulator] = args;

  await deleteGameData(normalizeKind(emulator), dataPath, titleId);
};

export type PickEmulatorProgramResult = EmulatorProgramLocation | { error: true, code: string };

export const pickEmulatorProgram = async (mainWindow: BrowserWindow, emulator: EmulatorKind): Promise<PickEmulatorProgramResult> => {
  const kind = normalizeKind(emulator);
  const { canceled, filePaths } = await dialog.showOpenDialog(mainWindow, { properties: ["openFile"] });

  if (canceled || filePaths.length === 0) {
    return { error: true, code: "OPERATION_CANCELED" };
  }

  if (!isEdenFamily(kind) || !programMatchesKind(kind, filePaths[0])) {
    return { error: true, code: `INVALID_${EMULATOR_PROGRAM_NAMES[kind].toUpperCase()}_PROGRAM` };
  }

  return resolveEdenFamilyDataRoot(filePaths[0]);
};
