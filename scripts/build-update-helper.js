// Compiles the Windows update helper (updater/windows/RyuSAK.UpdateHelper.cs) with the C# compiler that ships
// with .NET Framework 4, and writes a build receipt next to it (MDPro4 ships the same kind of receipt with its
// helper). RyuSAK refuses to run a helper whose SHA-256 doesn't match the receipt.
//
// Usage: node scripts/build-update-helper.js   (also runs automatically when packaging for Windows)
const { execFileSync } = require("child_process");
const crypto = require("crypto");
const fs = require("fs");
const path = require("path");

const ROOT = path.resolve(__dirname, "..");
const SOURCE_PATH = path.join(ROOT, "updater", "windows", "RyuSAK.UpdateHelper.cs");
const OUTPUT_DIR = path.join(ROOT, ".update-helper");
const HELPER_FILE_NAME = "RyuSAK.UpdateHelper.exe";
const RECEIPT_FILE_NAME = "build-receipt.json";
const WORKFLOW_PATH = path.join(ROOT, ".github", "workflows", "build.yml");

const sha256File = (filePath) => crypto.createHash("sha256").update(fs.readFileSync(filePath)).digest("hex");

const findCsc = () => {
  const windowsDir = process.env.WINDIR || "C:\\Windows";
  const candidates = ["Framework64", "Framework"].map((framework) => path.join(windowsDir, "Microsoft.NET", framework, "v4.0.30319", "csc.exe"));
  const csc = candidates.find((candidate) => fs.existsSync(candidate));

  if (!csc) {
    throw new Error(`The .NET Framework 4 C# compiler was not found (looked for ${candidates.join(", ")}).`);
  }

  return csc;
};

const buildUpdateHelper = () => {
  if (process.platform !== "win32") {
    throw new Error("The Windows update helper can only be compiled on Windows.");
  }

  fs.mkdirSync(OUTPUT_DIR, { recursive: true });
  const helperPath = path.join(OUTPUT_DIR, HELPER_FILE_NAME);
  const receiptPath = path.join(OUTPUT_DIR, RECEIPT_FILE_NAME);

  execFileSync(findCsc(), [
    "/nologo",
    "/target:winexe",
    "/optimize+",
    "/platform:anycpu",
    "/warnaserror+",
    `/out:${helperPath}`,
    "/reference:System.dll",
    "/reference:System.Core.dll",
    "/reference:System.Drawing.dll",
    "/reference:System.Windows.Forms.dll",
    "/reference:System.Runtime.Serialization.dll",
    SOURCE_PATH
  ], { stdio: "inherit" });

  const fileVersion = (/AssemblyFileVersion\("([^"]+)"\)/.exec(fs.readFileSync(SOURCE_PATH, "utf8")) || [])[1] || null;
  const receipt = {
    schema: "ryusak-windows-update-helper-v1",
    sourceRevision: process.env.GITHUB_SHA || null,
    sourcePath: "updater/windows/RyuSAK.UpdateHelper.cs",
    sourceSha256: sha256File(SOURCE_PATH),
    workflowSha256: fs.existsSync(WORKFLOW_PATH) ? sha256File(WORKFLOW_PATH) : null,
    fileVersion,
    fileName: HELPER_FILE_NAME,
    sizeBytes: fs.statSync(helperPath).size,
    sha256: sha256File(helperPath)
  };
  fs.writeFileSync(receiptPath, `${JSON.stringify(receipt, null, 2)}\n`);

  console.log(`Built ${HELPER_FILE_NAME} (${receipt.sizeBytes} bytes, sha256 ${receipt.sha256}).`);
  return { helperPath, receiptPath };
};

module.exports = { buildUpdateHelper, OUTPUT_DIR, HELPER_FILE_NAME, RECEIPT_FILE_NAME };

if (require.main === module) {
  buildUpdateHelper();
}
