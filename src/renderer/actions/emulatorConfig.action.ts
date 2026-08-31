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
  renameEmulatorConfigAction: (path: string) => void,
  changeEmulatorConfigPathAction: (path: string) => void,
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
      text: i18n.t(process.platform === "linux" ? "pickRyuDataPathLinux" : "pickRyuDataPath")
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
  removeEmulatorConfigAction: async (path) => {
    const configs = get().ryujinxConfigs;
    const index = configs.findIndex(item => item.path === path);

    if (index === -1) {
      return null;
    }

    const config = configs[index];

    const { isConfirmed } = await Swal.fire({
      icon: "warning",
      title: i18n.t("deleteConfigConfirmTitle"),
      text: i18n.t("deleteConfigConfirmDescription").replace("{name}", config.name),
      showCancelButton: true,
      confirmButtonText: i18n.t("deleteConfiguration"),
      cancelButtonText: i18n.t("cancel"),
    });

    if (!isConfirmed) {
      return null;
    }

    configs.splice(index, 1);
    localStorage.setItem(LS_KEYS.CONFIG, JSON.stringify(configs));
    return set({ ryujinxConfigs: configs, selectedConfig: configs[0] });
  },
  renameEmulatorConfigAction: async (path) => {
    const configs = get().ryujinxConfigs;
    const config = configs.find(item => item.path === path);

    if (!config) {
      return null;
    }

    const { isConfirmed, value } = await Swal.fire({
      text: i18n.t("renameConfigTitle"),
      input: "text",
      inputValue: config.name,
      inputAttributes: {
        placeholder: i18n.t("addConfigEg")
      },
      showCancelButton: true,
    });

    if (!isConfirmed) {
      return null;
    }

    const name = `${value || ""}`.trim();
    if (!name || name === config.name) {
      return null;
    }

    const updatedConfigs = configs.map(item => item.path === path ? { ...item, name } : item);
    const selectedConfig = updatedConfigs.find(item => item.path === path);
    localStorage.setItem(LS_KEYS.CONFIG, JSON.stringify(updatedConfigs));
    return set({ ryujinxConfigs: updatedConfigs, selectedConfig });
  },
  changeEmulatorConfigPathAction: async (path) => {
    await Swal.fire({
      icon: "info",
      text: i18n.t("changeConfigPathDescription")
    });

    const response = await invokeIpc("get-directory");

    if (typeof response === "object") {
      get().openAlertAction("error", response.code);
      return null;
    }

    if (response === path) {
      return null;
    }

    const configs = get().ryujinxConfigs;
    if (configs.find(item => item.path === response)) {
      get().openAlertAction("error", "EMULATOR_PATH_ALREADY_EXISTS");
      return null;
    }

    const updatedConfigs = configs.map(item => item.path === path ? { ...item, path: response } : item);
    const selectedConfig = updatedConfigs.find(item => item.path === response);
    localStorage.setItem("ryu-selected", response);
    localStorage.setItem(LS_KEYS.CONFIG, JSON.stringify(updatedConfigs));
    return set({ ryujinxConfigs: updatedConfigs, selectedConfig });
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
