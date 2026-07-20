import { ipcRenderer } from "electron";
import Swal from "sweetalert2";
import useStore from "./state";
import useTranslation from "../i18n/I18nService";
import { SetState } from "zustand/vanilla";
import dance from "../resources/dance.gif";
import { invokeIpc } from "../utils";

export interface IShaders {
  needRefreshShaders: boolean,
  downloadShadersAction: (titleId: string, dataPath: string) => void,
  shareShaders: (titleId: string, dataPath: string, localShaderCount: number, ryusakCount: number) => void,
  refreshShaderIndexAction: () => Promise<void>,
}

const { t } = useTranslation();

const clearPreviousShareKeys = (titleId: string) => {
  const prefix = `ryu-share-${titleId}-`;
  Object.keys(localStorage)
    .filter(key => key.startsWith(prefix))
    .forEach(key => localStorage.removeItem(key));
};

const createShadersSlice = (set: SetState<IShaders>): IShaders => ({
  needRefreshShaders: false,
  refreshShaderIndexAction: async () => {
    const ryujinxShaders = await invokeIpc("load-shader-index");
    useStore.setState({ ryujinxShaders });
  },
  downloadShadersAction: async (titleId, dataPath) => {
    const state = useStore.getState();

    state.upsertFileAction({
      filename: titleId,
      downloadSpeed: Infinity,
      progress: 0
    });

    const onShaderDownloadProgress = (_: unknown, filename: string, percentage: number, downloadSpeed: number) => {
      useStore.getState().updateFileProgress(titleId, filename, percentage, downloadSpeed);
    };

    ipcRenderer.on("download-progress", onShaderDownloadProgress);
    const result = await invokeIpc("install-shaders", titleId, dataPath).catch(() => null);
    state.removeFileAction(titleId);
    ipcRenderer.removeListener("download-progress", onShaderDownloadProgress);

    if (!result) {
      Swal.fire({
        icon: "error",
        text: t("FETCH_FAILED")
      });
      return set({ needRefreshShaders: !state.needRefreshShaders });
    }

    Swal.fire({
      imageUrl: dance,
      text: "Success !"
    });
    await useStore.getState().refreshShaderIndexAction();
    return set({ needRefreshShaders: !state.needRefreshShaders });
  },
  shareShaders: async (titleId, dataPath, localShaderCount, ryusakCount) => {
    const key = `ryu-share-${titleId}-${localShaderCount}`;

    if (localStorage.getItem(key)) {
      Swal.fire({
        icon: "error",
        title: "error",
        text: "You already shared those shaders, thanks!"
      });
      return false;
    }
    const state = useStore.getState();
    const dlManagerFilename = t("shadersSharing");

    state.upsertFileAction({
      filename: dlManagerFilename,
      downloadSpeed: Infinity,
      progress: 0
    });

    const onShadersShareProgress = (_: any, filename: string, percentage: number) => {
      if (filename !== titleId) {
        return;
      }

      useStore.getState().upsertFileAction({
        filename: dlManagerFilename,
        downloadSpeed: Infinity,
        progress: percentage
      });
    };

    ipcRenderer.on("download-progress", onShadersShareProgress);

    const result = await invokeIpc("share-shaders", titleId, dataPath, localShaderCount, ryusakCount);
    state.removeFileAction(dlManagerFilename);
    ipcRenderer.removeListener("download-progress", onShadersShareProgress);

    if (result.error) {
      return Swal.fire({
        icon: "error",
        html: result.code == "SHADER_UPLOAD_FAIL"
          ? t("SHADER_UPLOAD_FAIL").replace("{status}", result.message)
          : result.code == "SHADER_POST_FAIL"
            ? t("SHADER_POST_FAIL").replace("{details}", result.message)
            : result.code == "SHADER_POST_DUPLICATE"
              ? t("SHADER_POST_DUPLICATE")
              : result.code
      });
    }

    clearPreviousShareKeys(titleId);
    localStorage.setItem(key, "true");
    await useStore.getState().refreshShaderIndexAction();

    return Swal.fire({
      imageUrl: dance,
      html: t("shadersShared"),
    });
  }
});

export default createShadersSlice;
