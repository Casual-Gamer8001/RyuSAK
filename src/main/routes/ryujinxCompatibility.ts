import HttpService from "../services/HttpService";
import EShopMetaService from "../services/EShopMetaService";
import { GithubIssue, GithubLabel } from "../../types";

export type ryujinxCompatibilityProps = [string];

type RyubingCompatibilityEntry = {
  titleId: string;
  gameName: string;
  labels: string[];
  status: string;
  lastUpdated: string;
};

const statusDescriptions: { [key: string]: string } = {
  playable: "Boots and plays without crashes or GPU bugs, at a speed fast enough to reasonably enjoy.",
  ingame: "Boots and goes in-game, but has crashes, deadlocks, GPU bugs, bad audio, or speed problems.",
  menus: "Boots and goes past the title screen, but does not make it into main gameplay.",
  boots: "Boots but does not make it past the title screen.",
  nothing: "Does not boot or shows no signs of life."
};

const statusColors: { [key: string]: string } = {
  playable: "29cc97",
  ingame: "fbca04",
  menus: "d93f0b",
  boots: "d93f0b",
  nothing: "b60205"
};

const memoryDb: { [key: string]: GithubIssue } = {};
let compatibilityEntries: RyubingCompatibilityEntry[] = null;

const parseCsvLine = (line: string) => {
  const values: string[] = [];
  let current = "";
  let quoted = false;

  for (let index = 0; index < line.length; index++) {
    const char = line[index];

    if (char === "\"") {
      if (quoted && line[index + 1] === "\"") {
        current += "\"";
        index++;
      } else {
        quoted = !quoted;
      }
      continue;
    }

    if (char === "," && !quoted) {
      values.push(current);
      current = "";
      continue;
    }

    current += char;
  }

  values.push(current);
  return values;
};

const loadCompatibilityEntries = async (): Promise<RyubingCompatibilityEntry[]> => {
  if (compatibilityEntries) {
    return compatibilityEntries;
  }

  const csv = await HttpService.getRyubingCompatibilityCsv();
  compatibilityEntries = csv
    .split(/\r?\n/)
    .slice(1)
    .filter(Boolean)
    .map(parseCsvLine)
    .filter(row => row.length >= 5)
    .map(([titleId, gameName, labels, status, lastUpdated]) => ({
      titleId: titleId.toUpperCase(),
      gameName,
      labels: labels ? labels.split(";").filter(Boolean) : [],
      status,
      lastUpdated
    }));

  return compatibilityEntries;
};

const toLabel = (name: string, description = ""): GithubLabel => ({
  name,
  description,
  color: "0366d6"
});

const toCompatibilityIssue = (entry: RyubingCompatibilityEntry, mode: GithubIssue["mode"]): GithubIssue => {
  const normalizedStatus = entry.status.toLowerCase();
  return {
    mode,
    items: [{
      state: "open",
      labels: [
        {
          name: `status: ${entry.status}`,
          description: statusDescriptions[normalizedStatus] || `Last updated ${entry.lastUpdated}`,
          color: statusColors[normalizedStatus] || "0366d6"
        },
        ...entry.labels.map(label => toLabel(label, `Ryubing compatibility label. Last updated ${entry.lastUpdated}`))
      ]
    }]
  };
};

const ryujinxCompatibility = async (...args: ryujinxCompatibilityProps) => {
  const [titleId] = args;
  const normalizedTitleId = titleId.toUpperCase();

  if (memoryDb[normalizedTitleId]) {
    return memoryDb[normalizedTitleId];
  }

  const entries: RyubingCompatibilityEntry[] | null = await loadCompatibilityEntries().catch(() => null);
  if (!entries) {
    return null;
  }

  const entryByTitleId = entries.find(entry => entry.titleId === normalizedTitleId);
  if (entryByTitleId) {
    const compatData = toCompatibilityIssue(entryByTitleId, "id");
    memoryDb[normalizedTitleId] = compatData;
    return compatData;
  }

  const metadata = await EShopMetaService.getEShopMeta(titleId);
  const normalizedName = metadata.name.toLowerCase();
  const entryByName = entries.find(entry => entry.gameName.toLowerCase() === normalizedName)
    || entries.find(entry => entry.gameName.toLowerCase().includes(normalizedName) || normalizedName.includes(entry.gameName.toLowerCase()));

  if (entryByName) {
    const compatData = toCompatibilityIssue(entryByName, "name");
    memoryDb[normalizedTitleId] = compatData;
    return compatData;
  }

  const compatData: GithubIssue = { mode: "id", items: [] };
  memoryDb[normalizedTitleId] = compatData;
  return compatData;
};

export default ryujinxCompatibility;
