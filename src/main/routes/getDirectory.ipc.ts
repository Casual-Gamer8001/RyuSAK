import { BrowserWindow, dialog } from "electron";

const getDirectory = async (mainWindow: BrowserWindow) => {
  const { canceled, filePaths } = await dialog.showOpenDialog(mainWindow, { properties: ["openDirectory"] });

  if (canceled) {
    return { error: true, code: "OPERATION_CANCELED" };
  }

  return filePaths[0];
};

export default getDirectory;
