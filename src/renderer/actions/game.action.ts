import Swal from "sweetalert2";
import useTranslation from "../i18n/I18nService";
import { invokeIpc } from "../utils";
import { EmulatorKind } from "../../types";

const { t } = useTranslation();

export interface IGameAction {
  deleteGameAction: (titleId: string, dataPath: string, emulator?: EmulatorKind) => Promise<boolean>
}

const createGameSlice = (): IGameAction => ({
  deleteGameAction: async (titleId, dataPath, emulator) => {
    const { isConfirmed } = await Swal.fire({
      icon: "warning",
      title: t("areYouSure"),
      text: t("deleteGameNotice"),
      confirmButtonText: t("continue"),
      cancelButtonText: t("cancel"),
      showCancelButton: true,
    });

    if (!isConfirmed) {
      return false;
    }

    await invokeIpc("delete-game", titleId, dataPath, emulator);
    return true;
  }
});

export default createGameSlice;
