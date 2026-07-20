export type MirrorFileMeta = {
  name: string;
  type: string;
  mtime: string;
  size: number;
};

export type MirrorDirMeta = Array<MirrorFileMeta>;

export type MirrorUploadResponse = {
  fileSize: number,
  fileName: string,
  fileId: string,
  contentType: string,
  deletionTime: string,
  deletionToken: string
};

export type PostShadersBody = {
  name: string,
  titleId: string,
  fileId: string,
  deletionToken: string,
  shaderCount: number,
  shaderType?: string
};

export type RyusakShaders = {
  [key: string]: number;
};

export type RyujinxConfigMeta = {
  path: string,
  name: string,
  isDefault?: boolean,
  selected?: boolean
};

export type EShopTitleMeta = {
  id: string;
  name: string;
  iconUrl: string;
  normalizedName?: string;
};

export type EShopTitles = {
  [key: string]: EShopTitleMeta;
};

export enum LS_KEYS {
  CONFIG = "v2-emulators-bin",
  COVER_CACHE = "ryu-cover-cache",
  COVER_OVERRIDES = "ryu-cover-overrides",
  HIDDEN_GAMES = "ryu-hidden-games",
  TOS = "v2-tos",
  TAB = "v2-tab",
  ESHOP_UPDATE = "ryusak-eshop-update-date-2",
  LOCALE = "ryusak-locale"
}

export type RyusakEmulatorGames = string[];

export type RyusakEmulatorGame = {
  title: string,
  img: string
};

export type RyusakDownload = {
  filename: string,
  progress: number,
  downloadSpeed: number,
};

export type GithubLabel = {
  color: string;
  description: string;
  name: string;
};

export type GithubIssue = {
  items: {
    state: string;
    labels: Array<GithubLabel>;
  }[];
  mode?: "id" | "name";
};

export type GameBananaSearchGameResult = {
  id: number,
  name: string
};

export type GameBananaSearchModResult = {
  _aRecords: Array<{
    _sName: string,
    _sProfileUrl: string,
    _aPreviewMedia: {
      _aImages: Array<{
        _sBaseUrl: string,
        _sFile: string,
        _sFile220: string
      }>
    }
  }>
};

export type GameBananaMod = {
  name: string,
  url: string,
  cover: string,
};

export type Settings = {
  proxy?: string,
  steamGridDbApiKey?: string,
  gameIconSize?: string,
};
