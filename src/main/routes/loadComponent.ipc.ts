import HttpService from "../services/HttpService";
import electron from "electron";
import { SYS_SETTINGS } from "../../index";
import { MirrorDirMeta, RyusakShaders } from "../../types";

const loadStartupResource = async <T>(name: string, promise: Promise<T>, fallback: T): Promise<T> => {
  try {
    return await promise;
  } catch (error) {
    console.error(`Unable to load startup resource "${name}".`, error);
    return fallback;
  }
};

const loadComponentIpcHandler = async () => Promise.all([
  SYS_SETTINGS,
  loadStartupResource<RyusakShaders>("ryujinx shader list", HttpService.downloadRyujinxShaderList(), {}),
  loadStartupResource<MirrorDirMeta>("save list", HttpService.downloadSaveList(), []),
  loadStartupResource<MirrorDirMeta>("mods title list", HttpService.downloadModsTitleList(), []),
  HttpService.getLatestApplicationVersion(),
  electron.app.getVersion(),
  loadStartupResource<number>("shader threshold", HttpService.getThreshold(), -1),
  loadStartupResource<number>("minimum shader version", HttpService.getShadersMinVersion(), 0)
]);

export default loadComponentIpcHandler;

export const loadShaderIndexIpcHandler = async () => loadStartupResource<RyusakShaders>("ryujinx shader list", HttpService.downloadRyujinxShaderList(), {});
