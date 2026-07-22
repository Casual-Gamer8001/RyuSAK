import HttpService from "../services/HttpService";
import { GameBananaMod } from "../../types";

const stripNonAscii = (value: string) => Array.from(value).filter(char => char.charCodeAt(0) <= 0x7F).join("");

export type searchProps = [string];

export const searchGameBanana = async (...args: searchProps): Promise<Array<GameBananaMod>> => {
  const [name] = args;

  const results = await HttpService.gameBananaSearchGame(stripNonAscii(name));
  if (!results || results.length === 0) {
    return;
  }

  const mods = (await HttpService.gameBananaSearchMods(results[0].id))._aRecords;
  if (!mods || mods.length === 0) {
    return [];
  }

  return mods.map(mod => ({
    id: mod._idRow,
    name: mod._sName,
    url: mod._sProfileUrl,
    cover: mod._aPreviewMedia?._aImages?.[0]
      ? `${mod._aPreviewMedia._aImages[0]._sBaseUrl}/${mod._aPreviewMedia._aImages[0]._sFile220 || mod._aPreviewMedia._aImages[0]._sFile}`
      : "",
    hasFiles: mod._bHasFiles !== false
  }));
};
