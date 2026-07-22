import { ipcMain, BrowserWindow } from "electron";
import loadComponentIpcHandler, { loadShaderIndexIpcHandler, loadShaderVariantsIpcHandler } from "./loadComponent.ipc";
import {
  getRyujinxPath,
  scanGamesForConfig,
  deleteGameProps,
  deleteGame
} from "./emulatorFilesystem";
import getDirectory from "./getDirectory.ipc";
import EShopMetaService from "../services/EShopMetaService";
import openFolderForGame, { openFolderIPCProps } from "./openFolderForGame";
import ryujinxCompatibility, { ryujinxCompatibilityProps } from "./ryujinxCompatibility";
import downloadCommunitySave from "./savesDownload";
import { countShaders, countShadersProps, getShaderCacheKey, installShaders, installShadersProps, shareShaders } from "./shaders";
import { searchGameBanana, searchProps } from "./gamebanana";
import {
  getGameBananaModFiles,
  getGameBananaModFilesProps,
  installGameBananaMod,
  installGameBananaModProps
} from "./gamebananaInstall";
import { setGameIconSize, setProxy, setSteamGridDbApiKey } from "./settings.ipc";
import searchSteamGridDbCovers, { steamGridDbCoverSearchProps } from "./steamgriddb";

export type IPCCalls = {
  "load-components": Promise<ReturnType<typeof loadComponentIpcHandler>>,
  "load-shader-index": Promise<ReturnType<typeof loadShaderIndexIpcHandler>>,
  "load-shader-variants": Promise<ReturnType<typeof loadShaderVariantsIpcHandler>>,
  "get-directory": Promise<ReturnType<typeof getDirectory>>,
  "scan-games": Promise<ReturnType<typeof scanGamesForConfig>>,
  "build-metadata-from-titleId": Promise<ReturnType<typeof EShopMetaService.getEShopMeta>>,
  "set-title-override": Promise<ReturnType<typeof EShopMetaService.setTitleOverride>>,
  "update-eshop-data": ReturnType<typeof EShopMetaService.updateEShopData>,
  "openFolderForGame": ReturnType<typeof openFolderForGame>,
  "getRyujinxCompatibility": ReturnType<typeof ryujinxCompatibility>,
  "download-community-save": ReturnType<typeof downloadCommunitySave>,
  "count-shaders": ReturnType<typeof countShaders>,
  "get-shader-cache-key": ReturnType<typeof getShaderCacheKey>,
  "install-shaders": ReturnType<typeof installShaders>,
  "share-shaders": ReturnType<typeof shareShaders>,
  "search-gamebanana": ReturnType<typeof searchGameBanana>,
  "get-gamebanana-mod-files": ReturnType<typeof getGameBananaModFiles>,
  "install-gamebanana-mod": ReturnType<typeof installGameBananaMod>,
  "search-steamgriddb-covers": ReturnType<typeof searchSteamGridDbCovers>,
  "delete-game": ReturnType<typeof deleteGame>,
  "set-proxy": ReturnType<typeof setProxy>,
  "set-steamgriddb-api-key": ReturnType<typeof setSteamGridDbApiKey>,
  "set-game-icon-size": ReturnType<typeof setGameIconSize>,
  "get-ryujinx-appdata-path": ReturnType<typeof getRyujinxPath>,
};

const makeIpcRoutes = (mainWindow: BrowserWindow) => {
  ipcMain.handle("load-components", async (_) => loadComponentIpcHandler());
  ipcMain.handle("load-shader-index", async (_) => loadShaderIndexIpcHandler());
  ipcMain.handle("load-shader-variants", async (_) => loadShaderVariantsIpcHandler());
  ipcMain.handle("get-directory", async (_) => getDirectory(mainWindow));
  ipcMain.handle("scan-games", async (_, dataPath: string) => scanGamesForConfig(dataPath));
  ipcMain.handle("build-metadata-from-titleId", async (_, titleId: string, dataPath?: string) => EShopMetaService.getEShopMeta(titleId, dataPath));
  ipcMain.handle("set-title-override", async (_, titleId: string, name: string) => EShopMetaService.setTitleOverride(titleId, name));
  ipcMain.handle("update-eshop-data", async () => EShopMetaService.updateEShopData());
  ipcMain.handle("openFolderForGame", async (_, ...args: openFolderIPCProps) => openFolderForGame(...args));
  ipcMain.handle("getRyujinxCompatibility", async (_, ...args: ryujinxCompatibilityProps) => ryujinxCompatibility(...args));
  ipcMain.handle("download-community-save", async (_, fileName: string) => downloadCommunitySave(fileName));
  ipcMain.handle("count-shaders", async (_, ...args: countShadersProps) => countShaders(...args));
  ipcMain.handle("get-shader-cache-key", async (_, titleId: string, dataPath: string) => getShaderCacheKey(titleId, dataPath));
  ipcMain.handle("install-shaders", async (_, ...args: installShadersProps) => installShaders(mainWindow, ...args));
  ipcMain.handle("share-shaders", async (_, ...args: shareShaders) => shareShaders(mainWindow, ...args));
  ipcMain.handle("search-gamebanana", async (_, ...args: searchProps) => searchGameBanana(...args));
  ipcMain.handle("get-gamebanana-mod-files", async (_, ...args: getGameBananaModFilesProps) => getGameBananaModFiles(...args));
  ipcMain.handle("install-gamebanana-mod", async (_, ...args: installGameBananaModProps) => installGameBananaMod(mainWindow, ...args));
  ipcMain.handle("search-steamgriddb-covers", async (_, ...args: steamGridDbCoverSearchProps) => searchSteamGridDbCovers(...args));
  ipcMain.handle("delete-game", (_, ...args: deleteGameProps) => deleteGame(...args));
  ipcMain.handle("set-proxy", async (_, proxy: string) => setProxy(proxy));
  ipcMain.handle("set-steamgriddb-api-key", async (_, steamGridDbApiKey: string) => setSteamGridDbApiKey(steamGridDbApiKey));
  ipcMain.handle("set-game-icon-size", async (_, gameIconSize: string) => setGameIconSize(gameIconSize));
  ipcMain.handle("get-ryujinx-appdata-path", (_) => getRyujinxPath());
};

export default makeIpcRoutes;
