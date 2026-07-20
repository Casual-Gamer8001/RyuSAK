import fs from "fs-extra";
import path from "path";
import { Mutex } from "async-mutex";
import { app } from "electron";
import { EShopTitles, EShopTitleMeta } from "../../types";
import HttpService from "../services/HttpService";
import custom_meta_json from "../../assets/custom_meta.json";
const customMetas = custom_meta_json as EShopTitles;

class EShopMetaService {
  private eShopDataPath: string;
  private titleOverridesPath: string;
  private eShopTitles: EShopTitles;
  private mutex = new Mutex();

  constructor() {
    const cacheDir = fs.existsSync(path.resolve(app.getPath("exe"), "..", "portable"))
      ? path.resolve(app.getPath("exe"), "..", "electron_cache")
      : path.join(app.getPath("userData"));

    this.eShopDataPath = path.join(cacheDir, "titles.US.en.json");
    this.titleOverridesPath = path.join(cacheDir, "title-overrides.json");
  }

  private async getTitleOverrides(): Promise<EShopTitles> {
    const defaults: EShopTitles = {
      "010049900F546001": {
        id: "010049900F546001",
        name: "Super Mario 3D All-Stars",
        iconUrl: ""
      },
      "010000000000003B": {
        id: "010000000000003B",
        name: "Unknown/System Cache",
        iconUrl: ""
      }
    };

    if (!await fs.pathExists(this.titleOverridesPath)) {
      await fs.writeJson(this.titleOverridesPath, defaults, { spaces: 2 });
      return defaults;
    }

    return {
      ...defaults,
      ...await fs.readJson(this.titleOverridesPath)
    };
  }

  async setTitleOverride(titleId: string, name: string): Promise<EShopTitleMeta> {
    titleId = titleId.toUpperCase();
    const titleOverrides = await this.getTitleOverrides();

    if (name) {
      titleOverrides[titleId] = {
        id: titleId,
        name,
        iconUrl: titleOverrides[titleId]?.iconUrl || ""
      };
    } else {
      delete titleOverrides[titleId];
    }

    await fs.writeJson(this.titleOverridesPath, titleOverrides, { spaces: 2 });
    return this.getEShopMeta(titleId);
  }

  private async getEShopTitles(): Promise<EShopTitles> {
    if (this.eShopTitles) {
      return this.eShopTitles;
    }

    const release = await this.mutex.acquire();
    try {
      if (!this.eShopTitles) {
        if (await fs.pathExists(this.eShopDataPath)) {
          this.eShopTitles = await fs.readJson(this.eShopDataPath);
        } else {
          await this.updateEShopData();
        }
      }

      return this.eShopTitles;
    }
    finally {
      release();
    }
  }

  private async getRyujinxMeta(titleId: string, dataPath?: string): Promise<Partial<EShopTitleMeta>> {
    if (!dataPath) return {};

    try {
      const metadataPath = path.resolve(dataPath, "games", titleId.toLowerCase(), "gui", "metadata.json");
      const metadata = await fs.readJson(metadataPath);

      return metadata.title ? { name: metadata.title } : {};
    } catch {
      return {};
    }
  }

  async getEShopMeta(titleId: string, dataPath?: string): Promise<EShopTitleMeta> {
    const eShopTitles = await this.getEShopTitles();
    titleId = titleId.toUpperCase();
    const titleOverrides = await this.getTitleOverrides();

    let titleMeta = titleOverrides[titleId] || customMetas[titleId] || (eShopTitles || {})[titleId] || { id: titleId, name: titleId, iconUrl: "" };
    const ryujinxMeta = await this.getRyujinxMeta(titleId, dataPath);

    if (!titleMeta.name || titleMeta.name === titleId) {
      titleMeta = { ...titleMeta, ...ryujinxMeta };
    }

    titleMeta.name ??= titleId;
    titleMeta.iconUrl ??= "";

    return titleMeta;
  }

  async updateEShopData(): Promise<boolean> {
    try {
      const eshopDataJson: string = await HttpService.downloadEshopData();

      await fs.writeFile(this.eShopDataPath, eshopDataJson, "utf-8");
      this.eShopTitles = JSON.parse(eshopDataJson);

      return true;
    } catch (e) {
      console.error(e);
      this.eShopTitles = {};
      return false;
    }
  }
}

export default new EShopMetaService();
