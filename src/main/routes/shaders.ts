import fs from "fs-extra";
import path from "path";
import zip from "adm-zip";
import HttpService from "../services/HttpService";
import { app, BrowserWindow } from "electron";
import EShopMetaService from "../services/EShopMetaService";
import { Buffer } from "buffer";
import FormData from "form-data";
import { MirrorUploadResponse } from "../../types";

export type countShadersProps = [string, string, number];

export type installShadersProps = [string, string];

export type shareShaders = [string, string, number, number];

export const writeZipAsync = (archive: zip, destPath: string): Promise<void> => new Promise((resolve, reject) => {
  archive.writeZip(destPath, (error) => error ? reject(error) : resolve());
});

export const countShaders = async (...args: countShadersProps): Promise<number> => {
  const [titleId, dataPath, minVersion] = args;
  const shaderTocFile = path.resolve(dataPath, "games", titleId.toLocaleLowerCase(), "cache", "shader", "shared.toc");

  if (!await fs.pathExists(shaderTocFile)) {
    return 0;
  }

  // First, check cache version to ensure it will be accepted by Ryujinx
  const fd = await fs.open(shaderTocFile, "r+");
  const buffer = Buffer.alloc(8);
  await fs.read(fd, buffer, 0, 8, 4);
  const cacheVersion = buffer.readUInt32LE();
  await fs.close(fd);

  if (cacheVersion < minVersion) {
    return 0;
  }

  // If cache version is accepted by Ryujinx, computer shader count
  const stat = await fs.stat(shaderTocFile);
  return Math.max((stat.size - 32) / 8, 0);
};

export const installShaders = async (mainWindow: BrowserWindow, ...args: installShadersProps): Promise<boolean> => {
  const [titleId, dataPath] = args;

  const shaderCacheDir = path.resolve(dataPath, "games", titleId.toLowerCase(), "cache", "shader");
  await fs.ensureDir(shaderCacheDir);
  await fs.emptyDir(shaderCacheDir);

  const shaderCacheZipPath = path.resolve(shaderCacheDir, `${titleId}.zip`);
  const result = await HttpService.getShadersZipWithProgress(titleId, shaderCacheZipPath, mainWindow);

  if (!result) {
    return null;
  }

  const shaderCacheZip = new zip(shaderCacheZipPath);
  shaderCacheZip.extractAllTo(shaderCacheDir, true);
  await fs.unlink(shaderCacheZipPath);

  return true;
};

export const shareShaders = async (mainWindow: BrowserWindow, ...args: shareShaders) => {
  const [titleId, dataPath, localCount] = args;
  const metadata = await EShopMetaService.getEShopMeta(titleId);
  const shaderCacheDir = path.resolve(dataPath, "games", titleId.toLowerCase(), "cache", "shader");
  const shaderCacheZipPath = path.resolve(app.getPath("temp"), `ryusak-${titleId}-${Date.now()}.zip`);

  const shaderCacheZip = new zip();
  shaderCacheZip.addLocalFile(path.resolve(shaderCacheDir, "guest.data"));
  shaderCacheZip.addLocalFile(path.resolve(shaderCacheDir, "guest.toc"));
  shaderCacheZip.addLocalFile(path.resolve(shaderCacheDir, "shared.data"));
  shaderCacheZip.addLocalFile(path.resolve(shaderCacheDir, "shared.toc"));

  await writeZipAsync(shaderCacheZip, shaderCacheZipPath);
  mainWindow.webContents.send("download-progress", titleId, "100.00");

  try {
    const formData = new FormData();
    formData.append("file", fs.createReadStream(shaderCacheZipPath));
    const uploadRes = await HttpService.postShaderUpload(formData as any, formData.getHeaders());

    if (!uploadRes.ok) {
      return { error: true, code: "SHADER_UPLOAD_FAIL", message: `${uploadRes.status} - ${uploadRes.statusText}` };
    }

    const uploadJson = await uploadRes.json() as MirrorUploadResponse;
    const postRes = await HttpService.postShaders({
      name: metadata.name,
      titleId: metadata.id,
      fileId: uploadJson.fileId,
      deletionToken: uploadJson.deletionToken,
      shaderCount: localCount
    });

    if (postRes.ok) {
      return { error: false, code: null, message: null };
    }

    if (postRes.status == 409) {
      return { error: true, code: "SHADER_POST_DUPLICATE", message: null };
    }

    return { error: true, code: "SHADER_POST_FAIL", message: `${postRes.status} - ${postRes.statusText}` };
  } finally {
    await fs.remove(shaderCacheZipPath);
  }
};
