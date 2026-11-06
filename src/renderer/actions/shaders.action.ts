import { ipcRenderer } from "electron";
import Swal from "sweetalert2";
import useStore from "./state";
import useTranslation, { I18nKeys } from "../i18n/I18nService";
import { SetState } from "zustand/vanilla";
import dance from "../resources/dance.gif";
import { fillPlaceholders, invokeIpc } from "../utils";

export interface IShaders {
  needRefreshShaders: boolean,
  downloadShadersAction: (titleId: string, dataPath: string, shaderPath?: string) => void,
  shareShaders: (titleId: string, dataPath: string, localShaderCount: number, ryusakCount: number) => void,
  refreshShaderIndexAction: () => Promise<void>,
  buildShadersAction: (titleId: string, dataPath: string, gameName: string) => Promise<void>,
  removeBuiltShadersAction: (titleId: string, dataPath: string, builtCount: number) => Promise<void>,
}

const { t } = useTranslation();

// Share of the progress bar for each step of the shader builder.
const BUILD_PHASES: Record<string, [number, number]> = {
  scan: [2, 58],
  probe: [60, 30],
  write: [90, 10]
};

const BUILDER_ERRORS: Record<string, I18nKeys> = {
  LAUNCH_FIRST: "SHADER_BUILDER_LAUNCH_FIRST",
  RYUBING_RUNNING: "SHADER_BUILDER_RYUBING_RUNNING",
  CACHE_IN_USE: "SHADER_BUILDER_RYUBING_RUNNING",
  NO_KEYS: "SHADER_BUILDER_NO_KEYS",
  NO_GAME: "SHADER_BUILDER_NO_GAME",
  NO_GAME_DIRS: "SHADER_BUILDER_NO_GAME",
  GAME_UNREADABLE: "SHADER_BUILDER_NO_KEYS"
};

const showBuilderError = (code: string, message: string) => {
  if (code === "CANCELLED") {
    return;
  }

  Swal.fire({
    icon: "error",
    html: BUILDER_ERRORS[code] ? t(BUILDER_ERRORS[code]) : fillPlaceholders(t("SHADER_BUILDER_FAILED"), { message: message || code })
  });
};

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
    const ryujinxShaderVariants = await invokeIpc("load-shader-variants");
    useStore.setState({ ryujinxShaders, ryujinxShaderVariants });
  },
  downloadShadersAction: async (titleId, dataPath, shaderPath) => {
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
    const result = await invokeIpc("install-shaders", titleId, dataPath, shaderPath).catch(() => null);
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
              : result.code == "SHADER_EXPORT_FAIL"
                ? t("SHADER_EXPORT_FAIL")
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
  },
  buildShadersAction: async (titleId, dataPath, gameName) => {
    const confirmation = await Swal.fire({
      icon: "info",
      title: t("buildShaders"),
      html: t("buildShadersConfirm"),
      showCancelButton: true,
      confirmButtonText: t("buildShadersButton"),
      cancelButtonText: t("cancel")
    });

    if (!confirmation.isConfirmed) {
      return;
    }

    const state = useStore.getState();
    const jobKey = `${t("buildingShaders")}: ${gameName}`;
    state.upsertFileAction({ filename: jobKey, downloadSpeed: Infinity, progress: Infinity });

    const onProgress = (_: unknown, key: string, phase: string, done: number, total: number) => {
      const weights = BUILD_PHASES[phase];
      if (key !== jobKey || !weights || total <= 0) {
        return;
      }

      const progress = weights[0] + weights[1] * Math.min(1, done / total);
      useStore.getState().upsertFileAction({ filename: jobKey, downloadSpeed: Infinity, progress: +progress.toFixed(1) });
    };

    ipcRenderer.on("shader-builder-progress", onProgress);
    const outcome = await invokeIpc("run-shader-builder", "build", titleId, dataPath, jobKey)
      .catch((error: Error) => ({ ok: false, code: "BUILDER_FAILED", message: error.message, result: undefined }));
    ipcRenderer.removeListener("shader-builder-progress", onProgress);
    useStore.getState().removeFileAction(jobKey);
    set({ needRefreshShaders: !useStore.getState().needRefreshShaders });

    if (!outcome.ok) {
      return showBuilderError(outcome.code, outcome.message);
    }

    const added = +outcome.result.added || 0;
    await Swal.fire({
      icon: added > 0 ? "success" : "info",
      html: added > 0
        ? fillPlaceholders(t("buildShadersDone"), { added: `${added}` })
        : t("buildShadersNothing")
    });
  },
  removeBuiltShadersAction: async (titleId, dataPath, builtCount) => {
    const confirmation = await Swal.fire({
      icon: "warning",
      title: t("removeBuiltShaders"),
      html: fillPlaceholders(t("removeBuiltShadersConfirm"), { count: `${builtCount}` }),
      showCancelButton: true,
      confirmButtonText: t("removeBuiltShaders"),
      cancelButtonText: t("cancel")
    });

    if (!confirmation.isConfirmed) {
      return;
    }

    const outcome = await invokeIpc("run-shader-builder", "undo", titleId, dataPath)
      .catch((error: Error) => ({ ok: false, code: "BUILDER_FAILED", message: error.message, result: undefined }));
    set({ needRefreshShaders: !useStore.getState().needRefreshShaders });

    if (!outcome.ok) {
      return showBuilderError(outcome.code, outcome.message);
    }

    const text = fillPlaceholders(t("removeBuiltShadersDone"), {
      removed: `${+outcome.result.removed || 0}`,
      kept: `${+outcome.result.kept || 0}`
    });
    await Swal.fire({
      icon: "success",
      html: outcome.result.hostCacheReset ? `${text}<br /><br />${t("removeBuiltShadersRecompile")}` : text
    });
  }
});

export default createShadersSlice;
