import fs from "fs-extra";
import HttpService from "../services/HttpService";
import { gameIconSizeFile, proxyFile, steamGridDbApiKeyFile, SYS_SETTINGS } from "../../index";

export const setProxy = async (proxy: string) => {
  try {
    if (proxy) {
      await fs.writeFile(proxyFile, proxy);
    } else {
      await fs.unlink(proxyFile);
    }

    HttpService.updateHttpAgents(proxy);
    SYS_SETTINGS.proxy = proxy;

    return true;
  } catch (e) {
    console.error(e);
    return false;
  }
};

export const setSteamGridDbApiKey = async (steamGridDbApiKey: string) => {
  try {
    if (steamGridDbApiKey) {
      await fs.writeFile(steamGridDbApiKeyFile, steamGridDbApiKey);
    } else {
      await fs.remove(steamGridDbApiKeyFile);
    }

    SYS_SETTINGS.steamGridDbApiKey = steamGridDbApiKey;

    return true;
  } catch (e) {
    console.error(e);
    return false;
  }
};

export const setGameIconSize = async (gameIconSize: string) => {
  try {
    const allowedSizes = [90, 95, 105, 120, 135, 155, 180, 210, 245, 285, 330, 360];
    const numericValue = Number(gameIconSize);
    const value = Number.isFinite(numericValue)
      ? `${allowedSizes.reduce((closest, size) => Math.abs(size - numericValue) < Math.abs(closest - numericValue) ? size : closest, allowedSizes[0])}`
      : "180";
    await fs.writeFile(gameIconSizeFile, value);
    SYS_SETTINGS.gameIconSize = value;

    return true;
  } catch (e) {
    console.error(e);
    return false;
  }
};
