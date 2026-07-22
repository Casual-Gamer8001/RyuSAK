import HttpService from "../services/HttpService";
import { app, shell } from "electron";
import fs from "fs-extra";
import path from "path";

const downloadCommunitySave = async (fileName: string) => {
  const buffer = await HttpService.downloadCommunitySave(fileName) as ArrayBuffer;
  const desktopPath = app.getPath("desktop");
  const fileDest = path.resolve(desktopPath, fileName);
  await fs.writeFile(fileDest, Buffer.from(buffer));
  shell.showItemInFolder(fileDest);
};

export default downloadCommunitySave;
