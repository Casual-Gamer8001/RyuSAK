import { ipcRenderer } from "electron";
import { IPCCalls } from "../main/routes";

export const invokeIpc = <T extends keyof IPCCalls> (arg: T, ...args: any[]): IPCCalls[T] => <IPCCalls[T]> ipcRenderer.invoke(arg as string, ...args);

// Replaces every {key} occurrence, e.g. fillPlaceholders("Open {emulator}", { emulator: "Eden" }).
export const fillPlaceholders = (text: string, values: Record<string, string>) =>
  Object.keys(values).reduce((result, key) => result.split(`{${key}}`).join(values[key]), text);
