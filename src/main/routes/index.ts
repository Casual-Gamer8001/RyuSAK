import { ipcMain, BrowserWindow } from "electron";
import loadComponentIpcHandler, { loadShaderIndexIpcHandler, loadShaderVariantsIpcHandler } from "./loadComponent.ipc";
import {
  getDefaultEmulatorDataPath,
  scanGamesForConfig,
  deleteGameProps,
  deleteGame,
  pickEmulatorProgram
} from "./emulatorFilesystem";
import getDirectory from "./getDirectory.ipc";
import EShopMetaService from "../services/EShopMetaService";
import openFolderForGame, { openFolderIPCProps } from "./openFolderForGame";
import ryujinxCompatibility, { ryujinxCompatibilityProps } from "./ryujinxCompatibility";
import downloadCommunitySave from "./savesDownload";
import { countShaders, countShadersProps, getShaderCacheKey, installShaders, installShadersProps, shareShaders } from "./shaders";
import { runShaderBuilder, runShaderBuilderProps } from "./shaderBuilder";
import { searchGameBanana, searchProps } from "./gamebanana";
import {
  getGameBananaModFiles,
  getGameBananaModFilesProps,
  getModsPath,
  getModsPathProps,
  installGameBananaMod,
  installGameBananaModProps
} from "./gamebananaInstall";
import { setGameIconSize, setProxy, setSteamGridDbApiKey } from "./settings.ipc";
import searchSteamGridDbCovers, { steamGridDbCoverSearchProps } from "./steamgriddb";
import { EmulatorKind } from "../../types";

export type IPCCalls = {
  "load-components": Promise<ReturnType<typeof loadComponentIpcHandler>>,
  "load-shader-index": Promise<ReturnType<typeof loadShaderIndexIpcHandler>>,
  "load-shader-variants": Promise<ReturnType<typeof loadShaderVariantsIpcHandler>>,
  "get-directory": Promise<ReturnType<typeof getDirectory>>,
  "pick-emulator-program": ReturnType<typeof pickEmulatorProgram>,
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
  "run-shader-builder": ReturnType<typeof runShaderBuilder>,
  "search-gamebanana": ReturnType<typeof searchGameBanana>,
  "get-gamebanana-mod-files": ReturnType<typeof getGameBananaModFiles>,
  "install-gamebanana-mod": ReturnType<typeof installGameBananaMod>,
  "get-mods-path": Promise<ReturnType<typeof getModsPath>>,
  "search-steamgriddb-covers": ReturnType<typeof searchSteamGridDbCovers>,
  "delete-game": ReturnType<typeof deleteGame>,
  "set-proxy": ReturnType<typeof setProxy>,
  "set-steamgriddb-api-key": ReturnType<typeof setSteamGridDbApiKey>,
  "set-game-icon-size": ReturnType<typeof setGameIconSize>,
  "get-default-emulator-path": Promise<ReturnType<typeof getDefaultEmulatorDataPath>>,
};

const makeIpcRoutes = (mainWindow: BrowserWindow) => {
  ipcMain.handle("load-components", async (_) => loadComponentIpcHandler());
  ipcMain.handle("load-shader-index", async (_) => loadShaderIndexIpcHandler());
  ipcMain.handle("load-shader-variants", async (_) => loadShaderVariantsIpcHandler());
  ipcMain.handle("get-directory", async (_) => getDirectory(mainWindow));
  ipcMain.handle("pick-emulator-program", async (_, emulator: EmulatorKind) => pickEmulatorProgram(mainWindow, emulator));
  ipcMain.handle("scan-games", async (_, dataPath: string, emulator?: EmulatorKind) => scanGamesForConfig(dataPath, emulator));
  ipcMain.handle("build-metadata-from-titleId", async (_, titleId: string, dataPath?: string, emulator?: EmulatorKind) => EShopMetaService.getEShopMeta(titleId, dataPath, emulator));
  ipcMain.handle("set-title-override", async (_, titleId: string, name: string, dataPath?: string, emulator?: EmulatorKind) => EShopMetaService.setTitleOverride(titleId, name, dataPath, emulator));
  ipcMain.handle("update-eshop-data", async () => EShopMetaService.updateEShopData());
  ipcMain.handle("openFolderForGame", async (_, ...args: openFolderIPCProps) => openFolderForGame(...args));
  ipcMain.handle("getRyujinxCompatibility", async (_, ...args: ryujinxCompatibilityProps) => ryujinxCompatibility(...args));
  ipcMain.handle("download-community-save", async (_, fileName: string) => downloadCommunitySave(fileName));
  ipcMain.handle("count-shaders", async (_, ...args: countShadersProps) => countShaders(...args));
  ipcMain.handle("get-shader-cache-key", async (_, titleId: string, dataPath: string) => getShaderCacheKey(titleId, dataPath));
  ipcMain.handle("install-shaders", async (_, ...args: installShadersProps) => installShaders(mainWindow, ...args));
  ipcMain.handle("share-shaders", async (_, ...args: shareShaders) => shareShaders(mainWindow, ...args));
  ipcMain.handle("run-shader-builder", async (_, ...args: runShaderBuilderProps) => runShaderBuilder(mainWindow, ...args));
  ipcMain.handle("search-gamebanana", async (_, ...args: searchProps) => searchGameBanana(...args));
  ipcMain.handle("get-gamebanana-mod-files", async (_, ...args: getGameBananaModFilesProps) => getGameBananaModFiles(...args));
  ipcMain.handle("install-gamebanana-mod", async (_, ...args: installGameBananaModProps) => installGameBananaMod(mainWindow, ...args));
  ipcMain.handle("get-mods-path", async (_, ...args: getModsPathProps) => getModsPath(...args));
  ipcMain.handle("search-steamgriddb-covers", async (_, ...args: steamGridDbCoverSearchProps) => searchSteamGridDbCovers(...args));
  ipcMain.handle("delete-game", (_, ...args: deleteGameProps) => deleteGame(...args));
  ipcMain.handle("set-proxy", async (_, proxy: string) => setProxy(proxy));
  ipcMain.handle("set-steamgriddb-api-key", async (_, steamGridDbApiKey: string) => setSteamGridDbApiKey(steamGridDbApiKey));
  ipcMain.handle("set-game-icon-size", async (_, gameIconSize: string) => setGameIconSize(gameIconSize));
  ipcMain.handle("get-default-emulator-path", (_, emulator?: EmulatorKind) => getDefaultEmulatorDataPath(emulator));
};

export default makeIpcRoutes;
