import { URL } from "url";
import fetch, { RequestInit, HeadersInit, BodyInit } from "node-fetch";
import pRetry from "p-retry";
import { app, BrowserWindow, ipcMain } from "electron";
import https from "https";
import httpsProxyAgent from "https-proxy-agent";
import fs from "fs-extra";
import path from "path";
import { MirrorDirMeta, PostShadersBody, RyusakShaders, GameBananaSearchGameResult, GameBananaSearchModResult } from "../../types";

const USER_AGENT = `RyuSAK/${app.getVersion()}`;
const CDN_URL = "https://mirror.lewd.wtf";
const SHADER_REPOSITORY_URL = "https://ryusak-shader-backend.azurewebsites.net/";

export enum HTTP_PATHS {
  MODS_TITLE_LIST   = "/json/archive/nintendo/switch/mods/",
  MODS_VERSION_LIST = "/json/archive/nintendo/switch/mods/{title_id}/",
  MODS_LIST         = "/json/archive/nintendo/switch/mods/{title_id}/{version}/",
  MOD_DOWNLOAD      = "/json/archive/nintendo/switch/mods/{title_id}/{version}/{name}/",
  SAVES_LIST        = "/json/archive/nintendo/switch/savegames/",
  SAVES_DOWNLOAD    = "/archive/nintendo/switch/savegames/{file_name}",
  SHADERS_LIST_PC   = "metadata/shader_count_spirv.json",
  SHADERS_LIST_MAC  = "metadata/shader_count_macos.json",
  SHADERS_MIN_VER   = "metadata/shader_min_version.txt",
  SHADERS_ZIP_PC    = "shaders/spirv/{title_id}.zip",
  SHADERS_ZIP_MAC   = "shaders/macos/{title_id}.zip",
  THRESHOLD         = "metadata/threshold.txt",
}

export enum OTHER_URLS {
  RELEASE_INFO            = "https://api.github.com/repos/Ecks1337/RyuSAK/releases/latest",
  COMPAT_CSV              = "https://git.ryujinx.app/projects/Ryubing/raw/branch/master/docs/compatibility.csv",
  ESHOP_DATA              = "https://ryusak-shader-backend.azurewebsites.net/metadata/titles.US.en.json",
  GAME_BANANA_SEARCH_GAME = "https://api.gamebanana.com/Core/List/Like?itemtype=Game&field=name&match={match}",
  GAME_BANANA_SEARCH_MODS = "https://gamebanana.com/apiv10/Mod/Index?_nPage=1&_nPerpage=50&_sSort=Generic_MostDownloaded&_aFilters[Generic_Game]={id}",
}

class HttpService {
  private httpsAgent: https.Agent;
  private useMacUrl: boolean;
  private shaderRepositoryUrl: string;

  constructor() {
    this.useMacUrl = process.platform === "darwin";

    const cacheDir = fs.existsSync(path.resolve(app.getPath("exe"), "..", "portable"))
      ? path.resolve(app.getPath("exe"), "..", "electron_cache")
      : path.join(app.getPath("userData"));

    const proxyFile = path.resolve(cacheDir, "proxy");
    const proxy: string = fs.existsSync(proxyFile)
      ? fs.readFileSync(proxyFile, "utf-8")
      : null;
    this.updateHttpAgents(proxy);
    this.shaderRepositoryUrl = SHADER_REPOSITORY_URL;
  }

  public updateHttpAgents(proxy: string) {
    const oldhttpsAgent = this.httpsAgent;

    if (proxy) {
      this.httpsAgent = httpsProxyAgent(proxy) as any;
    } else {
      this.httpsAgent = new https.Agent();
    }

    oldhttpsAgent?.destroy();
  }

  protected fetch(url: string, req: RequestInit = { }) {
    req.agent ??= this.httpsAgent;
    req.headers = {
      "User-Agent": USER_AGENT,
      ...req.headers
    };

    return fetch(url, req);
  }

  // Trigger HTTP request using an exponential backoff strategy
  protected get(path: string, contentType: "JSON" | "TXT" | "BUFFER" = "JSON", retries = 3, baseUrl = CDN_URL) {
    const url = new URL(path, baseUrl);
    return pRetry(
      async () => {
        const response = await this.fetch(url.href);

        if (response.status >= 400) {
          throw new pRetry.AbortError(response.statusText);
        }

        if (contentType === "JSON") {
          return response.json();
        }

        if (contentType === "BUFFER") {
          return response.arrayBuffer();
        }

        return response.text();
      },
      { retries }
    ) as Promise<any>;
  }

  protected getFromShaderRepository(path: string, contentType: "JSON" | "TXT" | "BUFFER" = "JSON", retries = 3) {
    return this.get(path, contentType, retries, this.shaderRepositoryUrl);
  }

  public async post(path: string, body: BodyInit, headers: HeadersInit = null, baseUrl = CDN_URL) {
    const url = new URL(path, baseUrl);
    return this.fetch(url.href, {
      method: "POST",
      body,
      headers
    });
  }

  public async postJSON(path: string, obj: any, baseUrl = CDN_URL) {
    return this.post(
      path,
      JSON.stringify(obj),
      { "Content-Type": "application/json" },
      baseUrl
    );
  }

  public async postShaderUpload(body: BodyInit, headers: HeadersInit = null) {
    return this.post("upload/", body, headers, this.shaderRepositoryUrl);
  }

  public async postShaders(body: PostShadersBody) {
    if (this.useMacUrl) body.shaderType = "mac";
    return this.postJSON("/", body, this.shaderRepositoryUrl);
  }

  public async getWithProgress(path: string, destPath: string, mainWindow: BrowserWindow, eventName: string, baseUrl = CDN_URL) {
    const url = new URL(path, baseUrl);
    const fileStream = fs.createWriteStream(destPath);
    const controller = new AbortController();

    const response = await this.fetch(url.href, {
      signal: controller.signal
    });

    let chunkLength = 0;
    let lastEmittedEventTimestamp = 0;
    const contentLength = +(response.headers.get("content-length"));
    const startTime = Date.now();

    ipcMain.on("cancel-download", async (_, abortKey: string) => {
      if (abortKey !== eventName) return;
      fileStream.close();
      await fs.unlink(destPath).catch(() => null);
      controller.abort();
    });

    return new Promise((resolve, reject) => {
      response.body.pipe(fileStream);
      response.body.on("error", reject);
      response.body.on("data", (chunk) => {
        chunkLength += chunk.length;
        const percentage = chunkLength / contentLength * 100;
        const currentTimestamp = +new Date();
        const timeRange = currentTimestamp - startTime;
        const downloadSpeed = chunkLength / timeRange / 1024;

        // Throttle event to 1 time every 100ms
        if (currentTimestamp - lastEmittedEventTimestamp >= 200) {
          mainWindow.webContents.send("download-progress", eventName, percentage.toFixed(2), +downloadSpeed.toFixed(2));
          lastEmittedEventTimestamp = currentTimestamp;
        }
      });
      fileStream.on("finish", () => resolve(destPath));
    }).catch(() => null);
  }

  public async getShadersZipWithProgress(titleId: string, destPath: string, mainWindow: BrowserWindow) {
    const shadersZipUrl = this.useMacUrl ? HTTP_PATHS.SHADERS_ZIP_MAC : HTTP_PATHS.SHADERS_ZIP_PC;
    return this.getWithProgress(shadersZipUrl.replace("{title_id}", titleId), destPath, mainWindow, titleId, this.shaderRepositoryUrl);
  }

  public async downloadRyujinxShaderList() {
    return this.getFromShaderRepository(this.useMacUrl
      ? HTTP_PATHS.SHADERS_LIST_MAC
      : HTTP_PATHS.SHADERS_LIST_PC
    ) as Promise<RyusakShaders>;
  }

  public async downloadSaveList() {
    return this.get(HTTP_PATHS.SAVES_LIST) as Promise<MirrorDirMeta>;
  }

  public async downloadModsTitleList() {
    return this.get(HTTP_PATHS.MODS_TITLE_LIST) as Promise<MirrorDirMeta>;
  }

  public async getThreshold() {
    return this.getFromShaderRepository(HTTP_PATHS.THRESHOLD, "TXT")
      .then(threshold => Number(threshold))
      .catch(() => -1) as Promise<number>;
  }

  public async getShadersMinVersion() {
    return this.getFromShaderRepository(HTTP_PATHS.SHADERS_MIN_VER, "TXT")
      .then(ver => Number(ver)) as Promise<number>;
  }

  public async getLatestApplicationVersion() {
    // Do not use this.get because we do not want exponential backoff strategy since GitHub api is limited to 10 requests per minute for unauthenticated requests
    try {
      const response = await this.fetch(OTHER_URLS.RELEASE_INFO);

      if (response.status == 200) {
        const responseJson = await response.json() as any;
        const tagName = responseJson.tag_name as string;

        return tagName.replace("v", "");
      }
    }
    catch (e) {
      console.error(e);
    }

    return app.getVersion();
  }

  public async downloadEshopData() {
    return this.get(OTHER_URLS.ESHOP_DATA, "TXT") as Promise<string>;
  }

  public async getRyubingCompatibilityCsv() {
    const compatibilityCachePath = path.resolve(app.getPath("userData"), "compatibility.csv");

    try {
      const response = await this.fetch(OTHER_URLS.COMPAT_CSV);
      if (!response.ok) {
        throw new Error(`Ryubing compatibility CSV request failed with status ${response.status}`);
      }

      const csv = await response.text();
      await fs.ensureDir(path.dirname(compatibilityCachePath));
      await fs.writeFile(compatibilityCachePath, csv);

      return csv;
    } catch (error) {
      if (await fs.pathExists(compatibilityCachePath)) {
        return fs.readFile(compatibilityCachePath, "utf-8");
      }

      throw error;
    }
  }

  public async getModVersions(titleId: string) {
    return this.get(HTTP_PATHS.MODS_VERSION_LIST.replace("{title_id}", titleId)) as Promise<MirrorDirMeta>;
  }

  public async getModsForVersion(titleId: string, version: string) {
    return this.get(HTTP_PATHS.MODS_LIST.replace("{title_id}", titleId).replace("{version}", version));
  }

  public async getModName(titleId: string, version: string, name: string): Promise<{ modName: string, url: string }> {
    const path = HTTP_PATHS.MOD_DOWNLOAD
                           .replace("{title_id}", titleId)
                           .replace("{version}", encodeURIComponent(version))
                           .replace("{name}", encodeURIComponent(name));

    const mod = (await this.get(path)) as MirrorDirMeta;

    if (!mod[0]) {
      return;
    }

    const url = new URL(`${path}${encodeURIComponent(mod[0].name)}`, CDN_URL);

    return {
      modName: mod[0].name,
      url: url.href
    };
  }

  public async downloadSave(fileName: string) {
    return this.get(HTTP_PATHS.SAVES_DOWNLOAD.replace("{file_name}", fileName), "BUFFER") as Promise<ArrayBuffer>;
  }

  public async gameBananaSearchGame(match: string) {
    return this.get(OTHER_URLS.GAME_BANANA_SEARCH_GAME.replace("{match}", match)) as Promise<Array<GameBananaSearchGameResult>>;
  }

  public async gameBananaSearchMods(gameId: number) {
    return this.get(OTHER_URLS.GAME_BANANA_SEARCH_MODS.replace("{id}", gameId.toString())) as Promise<GameBananaSearchModResult>;
  }
}

export default new HttpService();
