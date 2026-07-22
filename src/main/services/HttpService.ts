import { URL } from "url";
import fetch, { RequestInit, HeadersInit, BodyInit } from "node-fetch";
import pRetry from "p-retry";
import { app, BrowserWindow, ipcMain } from "electron";
import https from "https";
import httpsProxyAgent from "https-proxy-agent";
import fs from "fs-extra";
import path from "path";
import { MirrorDirMeta, PostShadersBody, RyusakShaders, RyusakShaderVariants, GameBananaSearchGameResult, GameBananaSearchModResult, GameBananaFile } from "../../types";

const USER_AGENT = `RyuSAK/${app.getVersion()}`;
const SHADER_REPOSITORY_URL = "https://ryusak-shader-backend.azurewebsites.net/";

export enum HTTP_PATHS {
  COMMUNITY_SAVES_LIST = "metadata/savegames.json",
  COMMUNITY_SAVES_DOWNLOAD = "savegames/{file_name}",
  SHADERS_LIST_PC   = "metadata/shader_count_spirv.json",
  SHADERS_LIST_MAC  = "metadata/shader_count_macos.json",
  SHADERS_VARIANTS_PC = "metadata/shader_variants_spirv.json",
  SHADERS_VARIANTS_MAC = "metadata/shader_variants_macos.json",
  SHADERS_MIN_VER   = "metadata/shader_min_version.txt",
  SHADERS_ZIP_PC    = "shaders/spirv/{title_id}.zip",
  SHADERS_ZIP_MAC   = "shaders/macos/{title_id}.zip",
  THRESHOLD         = "metadata/threshold.txt",
}

export enum OTHER_URLS {
  RELEASE_INFO            = "https://api.github.com/repos/Casual-Gamer8001/RyuSAK/releases/latest",
  COMPAT_CSV              = "https://git.ryujinx.app/projects/Ryubing/raw/branch/master/docs/compatibility.csv",
  ESHOP_DATA              = "https://ryusak-shader-backend.azurewebsites.net/metadata/titles.US.en.json",
  GAME_BANANA_SEARCH_GAME = "https://api.gamebanana.com/Core/List/Like?itemtype=Game&field=name&match={match}",
  GAME_BANANA_SEARCH_MODS = "https://gamebanana.com/apiv10/Mod/Index?_nPage=1&_nPerpage=50&_sSort=Generic_MostDownloaded&_aFilters[Generic_Game]={id}",
  GAME_BANANA_MOD_FILES   = "https://api.gamebanana.com/Core/Item/Data?itemtype=Mod&itemid={id}&fields=name,Files().aFiles()",
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
  protected get(path: string, contentType: "JSON" | "TXT" | "BUFFER" = "JSON", retries = 3, baseUrl = SHADER_REPOSITORY_URL) {
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

  public async post(path: string, body: BodyInit, headers: HeadersInit = null, baseUrl = SHADER_REPOSITORY_URL) {
    const url = new URL(path, baseUrl);
    return this.fetch(url.href, {
      method: "POST",
      body,
      headers
    });
  }

  public async postJSON(path: string, obj: any, baseUrl = SHADER_REPOSITORY_URL) {
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

  public async getWithProgress(path: string, destPath: string, mainWindow: BrowserWindow, eventName: string, baseUrl = SHADER_REPOSITORY_URL) {
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

  public async getShadersZipWithProgress(titleId: string, destPath: string, mainWindow: BrowserWindow, shaderPath?: string) {
    const shadersZipUrl = this.useMacUrl ? HTTP_PATHS.SHADERS_ZIP_MAC : HTTP_PATHS.SHADERS_ZIP_PC;
    return this.getWithProgress(shaderPath || shadersZipUrl.replace("{title_id}", titleId), destPath, mainWindow, titleId, this.shaderRepositoryUrl);
  }

  public async downloadRyujinxShaderList() {
    return this.getFromShaderRepository(this.useMacUrl
      ? HTTP_PATHS.SHADERS_LIST_MAC
      : HTTP_PATHS.SHADERS_LIST_PC
    ) as Promise<RyusakShaders>;
  }

  public async downloadRyujinxShaderVariants() {
    return this.getFromShaderRepository(this.useMacUrl
      ? HTTP_PATHS.SHADERS_VARIANTS_MAC
      : HTTP_PATHS.SHADERS_VARIANTS_PC
    ).catch(() => ({})) as Promise<RyusakShaderVariants>;
  }

  public async downloadCommunitySaveList() {
    return this.getFromShaderRepository(HTTP_PATHS.COMMUNITY_SAVES_LIST).catch(() => []) as Promise<MirrorDirMeta>;
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

  public async downloadCommunitySave(fileName: string) {
    return this.getFromShaderRepository(HTTP_PATHS.COMMUNITY_SAVES_DOWNLOAD.replace("{file_name}", encodeURIComponent(fileName)), "BUFFER") as Promise<ArrayBuffer>;
  }

  public async gameBananaSearchGame(match: string) {
    return this.get(OTHER_URLS.GAME_BANANA_SEARCH_GAME.replace("{match}", match)) as Promise<Array<GameBananaSearchGameResult>>;
  }

  public async gameBananaSearchMods(gameId: number) {
    return this.get(OTHER_URLS.GAME_BANANA_SEARCH_MODS.replace("{id}", gameId.toString())) as Promise<GameBananaSearchModResult>;
  }

  public async gameBananaModFiles(modId: number) {
    return this.get(OTHER_URLS.GAME_BANANA_MOD_FILES.replace("{id}", modId.toString())) as Promise<[string, { [id: string]: {
      _idRow: number,
      _sFile: string,
      _nFilesize: number,
      _sDownloadUrl: string,
      _sDescription?: string,
      _bIsArchived?: boolean,
    } }]>;
  }

  public async downloadGameBananaFile(file: GameBananaFile, destPath: string, mainWindow: BrowserWindow, eventName: string) {
    return this.getWithProgress(file.downloadUrl, destPath, mainWindow, eventName);
  }
}

export default new HttpService();
