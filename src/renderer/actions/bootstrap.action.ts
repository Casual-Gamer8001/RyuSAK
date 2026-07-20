import { GetState, SetState } from "zustand/vanilla";
import { MirrorDirMeta, RyusakShaders, LS_KEYS, Settings } from "../../types";
import { IDownloadManager } from "./downloadManager.action";
import useTranslation from "../i18n/I18nService";
import { invokeIpc } from "../utils";

const { t } = useTranslation();

interface IBootstrap {
  isAppInitialized: boolean;
  bootstrapError?: string;
  saves: MirrorDirMeta;
  mods: MirrorDirMeta;
  ryujinxShaders: RyusakShaders;
  bootstrapAppAction: () => Promise<void>;
  latestVersion?: string;
  currentVersion?: string;
  threshold?: number;
  shadersMinVersion?: number;
  settings: Settings;
}

const lastEshopUpdate = localStorage.getItem(LS_KEYS.ESHOP_UPDATE) ? +localStorage.getItem(LS_KEYS.ESHOP_UPDATE) : null;

const createBootstrapSlice = (set: SetState<IBootstrap>, get: GetState<IDownloadManager>): IBootstrap => ({
  isAppInitialized: false,
  bootstrapError: null,
  saves: [],
  mods: [],
  ryujinxShaders: {},
  latestVersion: null,
  currentVersion: null,
  threshold: -1,
  shadersMinVersion: null,
  settings: {},
  bootstrapAppAction: async () => {
    set({ bootstrapError: null });

    try {
      const [
        settings,
        ryujinxShaders,
        saves,
        mods,
        latestVersion,
        currentVersion,
        threshold,
        shadersMinVersion
      ] = await invokeIpc("load-components");

      const dayInMilliseconds = 1000 * 60 * 60 * 24;
      const tomorrow = new Date();
      tomorrow.setDate(tomorrow.getDate() + 1);
      tomorrow.setHours(0, 0, 0, 0);

      const lastEshopUpdatePlus1Day = new Date(lastEshopUpdate + dayInMilliseconds).setHours(0, 0, 0, 0);

      if (!lastEshopUpdate || (tomorrow.getTime() > lastEshopUpdatePlus1Day)) {
        get().upsertFileAction({
          filename: t("updatingEshop"),
          progress: Infinity,
          downloadSpeed: Infinity
        });

        invokeIpc("update-eshop-data").then((res) => {

          if (res === true) {
            localStorage.setItem(LS_KEYS.ESHOP_UPDATE, `${new Date().getTime()}`);
          }

          get().removeFileAction(t("updatingEshop"));
        });
      }

      return set({
        settings,
        bootstrapError: null,
        isAppInitialized: true,
        saves,
        mods,
        ryujinxShaders,
        latestVersion,
        currentVersion,
        threshold,
        shadersMinVersion
      });
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      console.error("Failed to initialize RyuSAK.", error);
      return set({
        isAppInitialized: false,
        bootstrapError: message
      });
    }
  }
});

export default createBootstrapSlice;
