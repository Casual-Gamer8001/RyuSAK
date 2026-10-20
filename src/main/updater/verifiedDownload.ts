import crypto from "crypto";
import fs from "fs-extra";

export interface DownloadResponse {
  ok: boolean;
  status: number;
  statusText: string;
  body: NodeJS.ReadableStream | null;
}

export type DownloadFetch = (url: string, init: { signal: AbortSignal }) => Promise<DownloadResponse>;

export interface VerifiedDownloadOptions {
  url: string;
  destination: string;
  sha256: string;
  size: number;
  fetch: DownloadFetch;
  onProgress?: (received: number, total: number) => void;
  stallTimeoutMs?: number;
}

export const sha256File = (filePath: string) => new Promise<string>((resolve, reject) => {
  const hash = crypto.createHash("sha256");
  fs.createReadStream(filePath)
    .on("error", reject)
    .on("data", (chunk) => hash.update(chunk))
    .on("end", () => resolve(hash.digest("hex")));
});

// Streams into "<destination>.partial" while hashing, and only moves the file into place once its size and
// SHA-256 match the release. A verified file left by an earlier attempt is reused instead of downloaded again.
export const downloadVerified = async (options: VerifiedDownloadOptions) => {
  const expectedSha256 = options.sha256.toLowerCase();

  if (await fs.pathExists(options.destination)) {
    if (await sha256File(options.destination) === expectedSha256) {
      return { reused: true };
    }

    await fs.remove(options.destination);
  }

  const partialPath = `${options.destination}.partial`;
  await fs.remove(partialPath);

  const controller = new AbortController();
  const stallTimeoutMs = options.stallTimeoutMs || 60000;
  let stalled = false;
  let stallTimer: NodeJS.Timeout = null;
  const restartStallTimer = () => {
    clearTimeout(stallTimer);
    stallTimer = setTimeout(() => {
      stalled = true;
      controller.abort();
    }, stallTimeoutMs);
  };

  try {
    restartStallTimer();
    const response = await options.fetch(options.url, { signal: controller.signal });

    if (!response.ok || !response.body) {
      throw new Error(`Download failed with ${response.status} ${response.statusText}`);
    }

    const body = response.body;
    const hash = crypto.createHash("sha256");
    let received = 0;

    await new Promise<void>((resolve, reject) => {
      const output = fs.createWriteStream(partialPath);
      let failed = false;
      const fail = (error: Error) => {
        if (failed) return;
        failed = true;
        output.destroy();
        reject(error);
      };

      body.on("data", (chunk: Buffer) => {
        restartStallTimer();
        received += chunk.length;
        hash.update(chunk);
        options.onProgress?.(received, options.size);
      });
      body.on("error", fail);
      output.on("error", fail);
      // "close" rather than "finish": Windows won't rename the file while the handle is still open.
      output.on("close", () => {
        if (!failed) resolve();
      });
      body.pipe(output);
    });
    clearTimeout(stallTimer);

    if (options.size && received !== options.size) {
      throw new Error(`The download was incomplete (${received} of ${options.size} bytes).`);
    }

    const actualSha256 = hash.digest("hex");
    if (actualSha256 !== expectedSha256) {
      throw new Error(`The download failed verification: its SHA-256 (${actualSha256}) doesn't match the release (${expectedSha256}).`);
    }

    await fs.move(partialPath, options.destination, { overwrite: true });
    return { reused: false };
  } catch (error) {
    await fs.remove(partialPath).catch((): void => undefined);

    if (stalled) {
      throw new Error(`The download stalled for ${Math.round(stallTimeoutMs / 1000)} seconds and was cancelled.`);
    }

    throw error;
  } finally {
    clearTimeout(stallTimer);
  }
};
