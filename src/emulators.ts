import { EmulatorConfigMeta, EmulatorKind } from "./types";

export const EMULATOR_KINDS: EmulatorKind[] = ["ryujinx", "eden", "eglantine"];

export const EMULATOR_LABELS: Record<EmulatorKind, string> = {
  ryujinx: "Ryujinx",
  eden: "Eden",
  eglantine: "Eglantine"
};

// Program names produced by each emulator's build (OUTPUT_NAME in src/yuzu/CMakeLists.txt).
export const EMULATOR_PROGRAM_NAMES: Record<EmulatorKind, string> = {
  ryujinx: "Ryujinx",
  eden: "eden",
  eglantine: "eglantine"
};

export const isEmulatorKind = (value: unknown): value is EmulatorKind =>
  typeof value === "string" && (EMULATOR_KINDS as string[]).includes(value);

export const kindOf = (config?: Pick<EmulatorConfigMeta, "emulator"> | null): EmulatorKind =>
  isEmulatorKind(config?.emulator) ? config.emulator : "ryujinx";

export const normalizeKind = (value?: string | null): EmulatorKind =>
  isEmulatorKind(value) ? value : "ryujinx";

// Eden and Eglantine share the yuzu folder layout (load/, nand/, cache/shader/, cache/game_list/).
export const isEdenFamily = (kind: EmulatorKind) => kind === "eden" || kind === "eglantine";

// Ryujinx-only features: community shader cache sharing and Ryubing compatibility labels.
export const supportsShaderSharing = (kind: EmulatorKind) => kind === "ryujinx";
export const supportsCompatibilityData = (kind: EmulatorKind) => kind === "ryujinx";
