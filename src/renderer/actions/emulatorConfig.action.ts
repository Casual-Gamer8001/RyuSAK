import { GetState, SetState } from "zustand/vanilla";
import { IAlert } from "./alert.action";
import { EmulatorConfigMeta, EmulatorKind, RyusakEmulatorGame, LS_KEYS } from "../../types";
import Swal from "sweetalert2";
import { i18n } from "../app";
import { fillPlaceholders, invokeIpc } from "../utils";
import { EMULATOR_LABELS, EMULATOR_PROGRAM_NAMES, isEdenFamily, kindOf, normalizeKind } from "../../emulators";

export interface IEmulatorConfig {
  addNewEmulatorConfigAction: () => void;
  emulatorConfigs: EmulatorConfigMeta[];
  currentEmulator: EmulatorKind;
  selectedConfig: EmulatorConfigMeta;
  switchEmulatorAction: (emulator: EmulatorKind) => void,
  setSelectConfigAction: (selectedConfig: EmulatorConfigMeta) => void,
  removeEmulatorConfigAction: (path: string) => void,
  renameEmulatorConfigAction: (path: string) => void,
  changeEmulatorConfigPathAction: (path: string) => void,
  createDefaultConfig: () => void;
  renameLegacyDefaultConfigsAction: () => void;
  emulatorGames: RyusakEmulatorGame[];
}

// Ryujinx keeps its original key so existing installs keep their selected configuration.
export const selectedConfigKey = (emulator: EmulatorKind) => emulator === "ryujinx" ? "ryu-selected" : `${emulator}-selected`;

const DEFAULT_CONFIG_NAME_KEYS: Record<EmulatorKind, string> = {
  ryujinx: "ryuDefault",
  eden: "edenDefault",
  eglantine: "eglantineDefault"
};

// Every name RyuSAK ever gave the default configuration before it said Ryubing (all languages and past wordings).
const LEGACY_RYUJINX_DEFAULT_NAMES = new Set([
  "Ryujinx Global (default)",
  "Ryujinx Global (Default)",
  "Ryujinx Global (défaut)",
  "Ryujinx Global (padrão)",
  "Ryujinx Global (par défaut)",
  "Ryujinx Global (Por defecto)",
  "Ryujinx Global (standard)",
  "Ryujinx Global (Standard)",
  "Ryujinx Global (varsayılan)",
  "Ryujinx Global (по умолчанию)",
  "Ryujinx Global (デフォルト)",
  "Ryujinx Globale (Predefinita)",
  "Ryujinx 全局 (默认)"
]);

// Configurations saved before multi-emulator support have no emulator field and belong to Ryujinx.
const configuredEmulators: EmulatorConfigMeta[] = ((JSON.parse(localStorage.getItem(LS_KEYS.CONFIG)) || []) as EmulatorConfigMeta[])
  .map(config => ({ ...config, emulator: kindOf(config) }));

const saveConfigs = (configs: EmulatorConfigMeta[]) => localStorage.setItem(LS_KEYS.CONFIG, JSON.stringify(configs));

const isConfigFor = (config: EmulatorConfigMeta, emulator: EmulatorKind, path: string) => kindOf(config) === emulator && config.path === path;

const emulatorConfig = (set: SetState<IEmulatorConfig>, get: GetState<Partial<IAlert & IEmulatorConfig>>): IEmulatorConfig => ({
  emulatorConfigs: configuredEmulators,
  currentEmulator: normalizeKind(localStorage.getItem(LS_KEYS.TAB)),
  emulatorGames: [] as RyusakEmulatorGame[],
  selectedConfig: null as EmulatorConfigMeta,
  switchEmulatorAction: (emulator: EmulatorKind) => {
    if (emulator === get().currentEmulator) {
      return;
    }

    localStorage.setItem(LS_KEYS.TAB, emulator);
    return set({ currentEmulator: emulator, selectedConfig: null });
  },
  setSelectConfigAction: (selectedConfig: EmulatorConfigMeta = null) => {
    if (!selectedConfig) {
      return;
    }

    localStorage.setItem(selectedConfigKey(kindOf(selectedConfig)), selectedConfig.path);
    return set({ selectedConfig });
  },
  addNewEmulatorConfigAction: async () => {
    const emulator = get().currentEmulator;
    let configPath: string;

    if (isEdenFamily(emulator)) {
      await Swal.fire({
        icon: "info",
        text: fillPlaceholders(i18n.t("pickEdenFamilyProgram"), {
          emulator: EMULATOR_LABELS[emulator],
          program: EMULATOR_PROGRAM_NAMES[emulator]
        })
      });

      const response = await invokeIpc("pick-emulator-program", emulator);

      if ("error" in response) {
        get().openAlertAction("error", response.code);
        return null;
      }

      configPath = response.path;
    } else {
      await Swal.fire({
        icon: "info",
        text: i18n.t(process.platform === "linux" ? "pickRyuDataPathLinux" : "pickRyuDataPath")
      });

      const response = await invokeIpc("get-directory");

      if (typeof response === "object") {
        get().openAlertAction("error", response.code);
        return null;
      }

      configPath = response;
    }

    const emulatorConfigs = get().emulatorConfigs || [];

    if (emulatorConfigs.find(item => isConfigFor(item, emulator, configPath))) {
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
        const newConfig: EmulatorConfigMeta = {
          path: configPath,
          name: value,
          emulator
        };
        const updatedConfigs = [...emulatorConfigs, newConfig];
        localStorage.setItem(selectedConfigKey(emulator), newConfig.path);
        saveConfigs(updatedConfigs);
        return set({ emulatorConfigs: updatedConfigs, selectedConfig: newConfig });
      }
    }
  },
  removeEmulatorConfigAction: async (path) => {
    const emulator = get().currentEmulator;
    const configs = get().emulatorConfigs;
    const config = configs.find(item => isConfigFor(item, emulator, path));

    if (!config) {
      return null;
    }

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

    const updatedConfigs = configs.filter(item => item !== config);
    saveConfigs(updatedConfigs);
    return set({ emulatorConfigs: updatedConfigs, selectedConfig: updatedConfigs.find(item => kindOf(item) === emulator) || null });
  },
  renameEmulatorConfigAction: async (path) => {
    const emulator = get().currentEmulator;
    const configs = get().emulatorConfigs;
    const config = configs.find(item => isConfigFor(item, emulator, path));

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

    const updatedConfigs = configs.map(item => item === config ? { ...item, name } : item);
    const selectedConfig = updatedConfigs.find(item => isConfigFor(item, emulator, path));
    saveConfigs(updatedConfigs);
    return set({ emulatorConfigs: updatedConfigs, selectedConfig });
  },
  changeEmulatorConfigPathAction: async (path) => {
    const emulator = get().currentEmulator;

    await Swal.fire({
      icon: "info",
      text: isEdenFamily(emulator)
        ? fillPlaceholders(i18n.t("changeConfigPathDescriptionEdenFamily"), { emulator: EMULATOR_LABELS[emulator] })
        : i18n.t("changeConfigPathDescription")
    });

    const response = await invokeIpc("get-directory");

    if (typeof response === "object") {
      get().openAlertAction("error", response.code);
      return null;
    }

    if (response === path) {
      return null;
    }

    const configs = get().emulatorConfigs;
    if (configs.find(item => isConfigFor(item, emulator, response))) {
      get().openAlertAction("error", "EMULATOR_PATH_ALREADY_EXISTS");
      return null;
    }

    const updatedConfigs = configs.map(item => isConfigFor(item, emulator, path) ? { ...item, path: response } : item);
    const selectedConfig = updatedConfigs.find(item => isConfigFor(item, emulator, response));
    localStorage.setItem(selectedConfigKey(emulator), response);
    saveConfigs(updatedConfigs);
    return set({ emulatorConfigs: updatedConfigs, selectedConfig });
  },
  createDefaultConfig: async () => {
    const emulator = get().currentEmulator;
    const hasDefault = (configs: EmulatorConfigMeta[]) => configs.some(c => c.isDefault && kindOf(c) === emulator);

    if (hasDefault(get().emulatorConfigs)) {
      return;
    }

    const defaultPath = await invokeIpc("get-default-emulator-path", emulator);

    // The user may have switched emulators, or another call may have added the default, while waiting.
    const configs = get().emulatorConfigs;
    if (hasDefault(configs)) {
      return;
    }

    const config: EmulatorConfigMeta = {
      name: i18n.t(DEFAULT_CONFIG_NAME_KEYS[emulator]),
      path: defaultPath,
      emulator,
      isDefault: true
    };

    const updatedConfigs = [...configs, config];
    saveConfigs(updatedConfigs);
    return get().currentEmulator === emulator
      ? set({ emulatorConfigs: updatedConfigs, selectedConfig: config })
      : set({ emulatorConfigs: updatedConfigs });
  },
  // A default configuration still named "Ryujinx Global (default)" (in any language) gets the Ryubing name.
  // Names the user changed are kept. Runs from the UI because translations aren't loaded when the store is created.
  renameLegacyDefaultConfigsAction: () => {
    const newName = i18n.t(DEFAULT_CONFIG_NAME_KEYS.ryujinx);
    const configs = get().emulatorConfigs;
    const isLegacyDefault = (config: EmulatorConfigMeta) =>
      config.isDefault && kindOf(config) === "ryujinx" && LEGACY_RYUJINX_DEFAULT_NAMES.has(config.name);

    if (!newName || LEGACY_RYUJINX_DEFAULT_NAMES.has(newName) || !configs.some(isLegacyDefault)) {
      return;
    }

    const updatedConfigs = configs.map(config => isLegacyDefault(config) ? { ...config, name: newName } : config);
    saveConfigs(updatedConfigs);
    return set({ emulatorConfigs: updatedConfigs });
  },
});

export default emulatorConfig;
