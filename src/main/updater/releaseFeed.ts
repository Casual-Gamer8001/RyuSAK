import semver from "semver";

export const RELEASES_PAGE_URL = "https://github.com/Casual-Gamer8001/RyuSAK/releases/latest";

export interface ReleaseAsset {
  name: string;
  size: number;
  downloadUrl: string;
  digest?: string;
}

export interface ReleaseInfo {
  version: string;
  pageUrl: string;
  assets: ReleaseAsset[];
}

// The parts of GitHub's "latest release" response the updater reads.
export interface GithubReleaseJson {
  tag_name?: unknown;
  html_url?: unknown;
  assets?: unknown;
}

interface GithubAssetJson {
  name?: unknown;
  size?: unknown;
  browser_download_url?: unknown;
  digest?: unknown;
}

const SHA256_PATTERN = /^[0-9a-f]{64}$/;
const CHECKSUM_ASSET_PATTERN = /^SHA256SUMS(\.txt)?$/i;

export const parseRelease = (json: GithubReleaseJson): ReleaseInfo => {
  const tag = String(json?.tag_name || "");
  const version = semver.valid(semver.clean(tag));

  if (!version) {
    throw new Error(`The latest release tag "${tag}" is not a valid version.`);
  }

  const assets: ReleaseAsset[] = (Array.isArray(json.assets) ? json.assets as GithubAssetJson[] : []).map((asset) => ({
    name: String(asset?.name || ""),
    size: Number(asset?.size) || 0,
    downloadUrl: String(asset?.browser_download_url || ""),
    digest: typeof asset?.digest === "string" ? asset.digest : undefined
  }));

  return {
    version,
    pageUrl: typeof json.html_url === "string" ? json.html_url : RELEASES_PAGE_URL,
    assets
  };
};

export const getPlatformAssetPattern = (platform: string, arch: string): RegExp | null => {
  if (platform === "win32") {
    return /^RyuSAK-(.+)-Setup\.exe$/i;
  }

  if (platform === "linux" && arch === "x64") {
    return /^RyuSAK-(.+)-steamdeck-linux-x64\.zip$/i;
  }

  return null;
};

// The asset has to be built for the release it is attached to (MDPro4 checks its version marker the same way).
export const selectReleaseAsset = (release: ReleaseInfo, pattern: RegExp) => {
  const asset = release.assets.find((item) => {
    const match = pattern.exec(item.name);
    return !!match && semver.valid(match[1]) === release.version;
  });

  if (!asset) {
    throw new Error(`The v${release.version} release does not include a RyuSAK download for this platform.`);
  }

  if (!/^https?:\/\//i.test(asset.downloadUrl)) {
    throw new Error(`The download link for ${asset.name} is not valid.`);
  }

  return asset;
};

export const parseChecksumFile = (text: string) => {
  const checksums = new Map<string, string>();

  text.split(/\r?\n/).forEach((line) => {
    const match = /^([0-9a-fA-F]{64})\s+\*?(.+?)\s*$/.exec(line.trim());
    if (match) {
      checksums.set(match[2], match[1].toLowerCase());
    }
  });

  return checksums;
};

// GitHub publishes a SHA-256 digest for every release asset, and releases also ship SHA256SUMS.txt.
// Without either, the update is refused rather than installed unverified.
export const resolveExpectedSha256 = async (release: ReleaseInfo, asset: ReleaseAsset, fetchText: (url: string) => Promise<string>) => {
  const digest = (asset.digest || "").trim().toLowerCase();

  if (digest.indexOf("sha256:") === 0 && SHA256_PATTERN.test(digest.slice(7))) {
    return { sha256: digest.slice(7), source: "GitHub asset digest" };
  }

  const checksumAsset = release.assets.find((item) => CHECKSUM_ASSET_PATTERN.test(item.name));
  if (checksumAsset) {
    const sha256 = parseChecksumFile(await fetchText(checksumAsset.downloadUrl)).get(asset.name);
    if (sha256 && SHA256_PATTERN.test(sha256)) {
      return { sha256, source: checksumAsset.name };
    }
  }

  throw new Error(`The v${release.version} release doesn't publish a SHA-256 checksum for ${asset.name}, so RyuSAK can't verify it. Please download the update from GitHub instead.`);
};
