import { EmulatorConfigMeta, EmulatorKind } from "./types";

export const EMULATOR_KINDS: EmulatorKind[] = ["ryujinx", "eden"];

// The "ryujinx" kind covers Ryubing and older Ryujinx builds, which use the same Ryujinx data folder.
export const EMULATOR_LABELS: Record<EmulatorKind, string> = {
  ryujinx: "Ryubing",
  eden: "Eden"
};

// Program names produced by each emulator's build (OUTPUT_NAME in src/yuzu/CMakeLists.txt).
export const EMULATOR_PROGRAM_NAMES: Record<EmulatorKind, string> = {
  ryujinx: "Ryujinx",
  eden: "eden"
};

export const isEmulatorKind = (value: unknown): value is EmulatorKind =>
  typeof value === "string" && (EMULATOR_KINDS as string[]).includes(value);

export const kindOf = (config?: Pick<EmulatorConfigMeta, "emulator"> | null): EmulatorKind =>
  isEmulatorKind(config?.emulator) ? config.emulator : "ryujinx";

export const normalizeKind = (value?: string | null): EmulatorKind =>
  isEmulatorKind(value) ? value : "ryujinx";

// Emulators that use the yuzu folder layout (load/, nand/, cache/shader/, cache/game_list/).
export const isEdenFamily = (kind: EmulatorKind) => kind === "eden";

// Ryujinx-only features: community shader cache sharing and Ryubing compatibility labels.
export const supportsShaderSharing = (kind: EmulatorKind) => kind === "ryujinx";
export const supportsCompatibilityData = (kind: EmulatorKind) => kind === "ryujinx";
