import { shell } from "electron";
import fs from "fs-extra";
import { EmulatorKind } from "../../types";
import { normalizeKind } from "../../emulators";
import { getModsDir, getShaderDir } from "../emulatorPaths";

export type openFolderIPCProps = [string, "shaders" | "mods", string, EmulatorKind?];

const openFolderForGame = async (...args: openFolderIPCProps) => {
  const [titleId, dir, dataPath, emulator] = args;
  const kind = normalizeKind(emulator);
  const folder = dir === "mods" ? getModsDir(kind, dataPath, titleId) : getShaderDir(kind, dataPath, titleId);

  await fs.ensureDir(folder); // The folder may not exist yet if the emulator has never created it, so create it
  return shell.openPath(folder);
};

export default openFolderForGame;
