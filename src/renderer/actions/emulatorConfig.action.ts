import { GetState, SetState } from "zustand/vanilla";
import { IAlert } from "./alert.action";
import { RyujinxConfigMeta, RyusakEmulatorGame, LS_KEYS } from "../../types";
import Swal from "sweetalert2";
import { i18n } from "../app";
import { invokeIpc } from "../utils";

export interface IEmulatorConfig {
  addNewEmulatorConfigAction: () => void;
  ryujinxConfigs: RyujinxConfigMeta[];
  selectedConfig: RyujinxConfigMeta;
  setSelectConfigAction: (selectedConfig: RyujinxConfigMeta) => void,
  removeEmulatorConfigAction: (path: string) => void,
  createDefaultConfig: () => void;
  emulatorGames: RyusakEmulatorGame[];
}

const configuredEmulators: IEmulatorConfig["ryujinxConfigs"] = JSON.parse(localStorage.getItem(LS_KEYS.CONFIG)) || [];

const emulatorConfig = (set: SetState<IEmulatorConfig>, get: GetState<Partial<IAlert & IEmulatorConfig>>): IEmulatorConfig => ({
  ryujinxConfigs: configuredEmulators,
  emulatorGames: [] as RyusakEmulatorGame[],
  selectedConfig: null as RyujinxConfigMeta,
  setSelectConfigAction: (selectedConfig: RyujinxConfigMeta = null) => {
    if (!selectedConfig) {
      return;
    }

    localStorage.setItem("ryu-selected", selectedConfig.path);
    return set({ selectedConfig });
  },
  addNewEmulatorConfigAction: async () => {
    await Swal.fire({
      icon: "info",
      text: i18n.t("pickRyuDataPath")
    });

    const response = await invokeIpc("get-directory");

    if (typeof response === "object") {
      get().openAlertAction("error", response.code);
      return null;
    }

    const ryujinxConfigs = get().ryujinxConfigs || [];

    if (ryujinxConfigs.find(item => item.path === response)) {
      get().openAlertAction("error", "EMULATOR_PATH_ALREADY_EXISTS");
      return null;
    }

    let promptUserForConfiguration = true;
    while (promptUserForConfiguration) {
      const { isConfirmed, value } = await Swal.fire({
        text: i18n.t("addConfigTitle"),
        input: "text",
        inputAttributes: {
          placeholder: i18n.t("addConfigEg")
        },
        showCancelButton: true,
      });

      if (!isConfirmed) {
        promptUserForConfiguration = false;
      }

      if (value && value.length > 0) {
        const newConfig: RyujinxConfigMeta = {
          path: response,
          name: value
        };
        ryujinxConfigs.push(newConfig);
        localStorage.setItem("ryu-selected", newConfig.path);
        localStorage.setItem(LS_KEYS.CONFIG, JSON.stringify(ryujinxConfigs));
        return set({ ryujinxConfigs, selectedConfig: newConfig });
      }
    }
  },
  removeEmulatorConfigAction: (path) => {
    const configs = get().ryujinxConfigs;
    const index = configs.findIndex(item => item.path === path);
    configs.splice(index, 1);
    localStorage.setItem(LS_KEYS.CONFIG, JSON.stringify(configs));
    return set({ ryujinxConfigs: configs, selectedConfig: configs[0] });
  },
  createDefaultConfig: async () => {
    const configs = get().ryujinxConfigs;

    if (configs.find(c => c.isDefault)) {
      return;
    }

    const config: RyujinxConfigMeta = {
      name: i18n.t("ryuDefault"),
      path: await invokeIpc("get-ryujinx-appdata-path"),
      isDefault: true
    };

    configs.push(config);
    localStorage.setItem(LS_KEYS.CONFIG, JSON.stringify(configs));
    return set({ ryujinxConfigs: configs, selectedConfig: config });
  },
});

export default emulatorConfig;
