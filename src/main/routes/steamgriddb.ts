import fetch from "node-fetch";
import { SYS_SETTINGS } from "../../index";

type SteamGridDbGame = {
  id: number;
  name: string;
};

type SteamGridDbGrid = {
  id: number;
  url: string;
  thumb?: string;
};

export type steamGridDbCoverSearchProps = [string];

type SteamGridDbCoverSearchResult = {
  error?: "MISSING_API_KEY" | "API_KEY_INVALID" | "REQUEST_FAILED";
  covers: Array<{
    id: number;
    url: string;
    thumb: string;
    steamGridDbName: string;
    steamGridDbGameId: number;
    matchScore: number;
  }>;
};

type SteamGridDbFetchResult = {
  error: "MISSING_API_KEY" | "API_KEY_INVALID" | "REQUEST_FAILED" | null;
  body: any;
};

const normalizeCoverName = (value: string) => value
  .toLowerCase()
  .normalize("NFKD")
  .replace(/[^a-z0-9\s]/g, "")
  .replace(/\s+/g, " ")
  .trim();

const scoreCoverName = (title: string, candidateName: string) => {
  const wanted = normalizeCoverName(title);
  const candidate = normalizeCoverName(candidateName);

  if (wanted === candidate) return 100;
  if (candidate.includes(wanted) || wanted.includes(candidate)) return 80;

  const wantedWords = new Set(wanted.split(" ").filter(Boolean));
  const candidateWords = new Set(candidate.split(" ").filter(Boolean));
  const overlap = Array.from(wantedWords).filter(word => candidateWords.has(word)).length;

  return overlap / Math.max(wantedWords.size, 1) * 60;
};

const steamGridDbFetch = async (url: string): Promise<SteamGridDbFetchResult> => {
  if (!SYS_SETTINGS.steamGridDbApiKey) {
    return { error: "MISSING_API_KEY" as const, body: null };
  }

  const response = await fetch(url, {
    headers: {
      Authorization: `Bearer ${SYS_SETTINGS.steamGridDbApiKey}`,
    },
  });

  if (!response.ok) {
    return {
      error: response.status === 401 || response.status === 403
        ? "API_KEY_INVALID" as const
        : "REQUEST_FAILED" as const,
      body: null
    };
  }

  return { error: null, body: await response.json() as any };
};

const searchSteamGridDbCovers = async (...args: steamGridDbCoverSearchProps): Promise<SteamGridDbCoverSearchResult> => {
  const [name] = args;

  if (!name || !SYS_SETTINGS.steamGridDbApiKey) {
    return { error: !SYS_SETTINGS.steamGridDbApiKey ? "MISSING_API_KEY" : undefined, covers: [] };
  }

  const searchResult = await steamGridDbFetch(`https://www.steamgriddb.com/api/v2/search/autocomplete/${encodeURIComponent(name)}`);
  if (searchResult.error) return { error: searchResult.error, covers: [] };

  const games = (searchResult.body?.data || []) as SteamGridDbGame[];
  const bestGame = games
    .map(game => ({ game, score: scoreCoverName(name, game.name) }))
    .sort((left, right) => right.score - left.score)[0];

  if (!bestGame || bestGame.score < 40) {
    return { covers: [] };
  }

  const gridsResult = await steamGridDbFetch(`https://www.steamgriddb.com/api/v2/grids/game/${bestGame.game.id}?dimensions=600x900,342x482,660x930&types=static`);
  if (gridsResult.error) return { error: gridsResult.error, covers: [] };

  const grids = (gridsResult.body?.data || []) as SteamGridDbGrid[];

  const covers = grids
    .filter(grid => grid.url)
    .slice(0, 12)
    .map(grid => ({
      id: grid.id,
      url: grid.url,
      thumb: grid.thumb || grid.url,
      steamGridDbName: bestGame.game.name,
      steamGridDbGameId: bestGame.game.id,
      matchScore: Math.round(bestGame.score),
    }));

  return { covers };
};

export default searchSteamGridDbCovers;
