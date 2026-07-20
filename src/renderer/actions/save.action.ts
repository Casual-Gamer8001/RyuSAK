import { SetState } from "zustand/vanilla";
import { IDownloadManager } from "./downloadManager.action";
import useStore from "./state";
import Swal from "sweetalert2";
import { invokeIpc } from "../utils";

export interface ISaveAction {
  selectedTitleId?: string,
  setCurrentSaveDownloadAction: (id: string) => void,
  clearSelectedTitleIdAction: () => void,
  downloadSaveAction: (fileName: string) => void,
}

const createDownloadSaveSlice = (set: SetState<ISaveAction & IDownloadManager>): ISaveAction => ({
  selectedTitleId: null,
  setCurrentSaveDownloadAction: (selectedTitleId) => {
    set({ selectedTitleId });
  },
  clearSelectedTitleIdAction: () => set({ selectedTitleId: null }),
  downloadSaveAction: async (fileName) => {
    const state = useStore.getState();
    state.upsertFileAction({
      filename: state.selectedTitleId,
      downloadSpeed: Infinity,
      progress: Infinity
    });
    await invokeIpc("downloadSave", fileName);
    state.removeFileAction(state.selectedTitleId);
    Swal.fire({
      icon: "success",
      text: `${fileName} has been placed on your desktop`
    });
  }
});

export default createDownloadSaveSlice;
