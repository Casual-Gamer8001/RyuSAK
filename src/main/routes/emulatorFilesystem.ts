import path from "path";
import fs from "fs-extra";
import { app } from "electron";
import { RyusakEmulatorGames } from "../../types";

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

export const scanGamesForConfig = async (dataPath: string): Promise<RyusakEmulatorGames> => {
  try {
    const directories = await fs.readdir(path.join(dataPath, "games"), { withFileTypes: true });
    return directories.filter(d => d.isDirectory()).map(d => d.name.toLowerCase());
  } catch(e) {
    return [];
  }
};

export type deleteGameProps = [string, string];

export const deleteGame = async (...args: deleteGameProps) => {
  const [titleId, dataPath] = args;

  await fs.remove(path.resolve(dataPath, "games", titleId.toLowerCase()));
};
