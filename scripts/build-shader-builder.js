// Publishes the shader builder (shaderbuilder/RyuSAK.ShaderBuilder) as one self-contained file for the platform
// RyuSAK is being packaged for. RyuSAK runs it to add shaders from the game files to Ryubing's shader cache.
// Needs the .NET 10 SDK.
//
// Usage: node scripts/build-shader-builder.js [platform] [arch]   (also runs automatically when packaging)
const { execFileSync } = require("child_process");
const fs = require("fs");
const path = require("path");

const ROOT = path.resolve(__dirname, "..");
const PROJECT_PATH = path.join(ROOT, "shaderbuilder", "RyuSAK.ShaderBuilder", "RyuSAK.ShaderBuilder.csproj");
const OUTPUT_DIR = path.join(ROOT, ".shader-builder");

// Electron platform and arch to .NET runtime identifier.
const RUNTIME_IDS = {
  "win32-x64": "win-x64",
  "win32-arm64": "win-arm64",
  "linux-x64": "linux-x64",
  "linux-arm64": "linux-arm64",
  "darwin-x64": "osx-x64",
  "darwin-arm64": "osx-arm64"
};

const builderFileName = (platform) => platform === "win32" ? "RyuSAK.ShaderBuilder.exe" : "RyuSAK.ShaderBuilder";

const buildShaderBuilder = (platform = process.platform, arch = process.arch) => {
  const runtimeId = RUNTIME_IDS[`${platform}-${arch}`];
  if (!runtimeId) {
    throw new Error(`The shader builder can't be built for ${platform}-${arch}.`);
  }

  const outputDir = path.join(OUTPUT_DIR, runtimeId);
  fs.rmSync(outputDir, { recursive: true, force: true });

  execFileSync("dotnet", [
    "publish",
    PROJECT_PATH,
    "--configuration", "Release",
    "--runtime", runtimeId,
    "--self-contained",
    "--output", outputDir,
    "--nologo",
    "-p:PublishSingleFile=true",
    "-p:PublishTrimmed=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:DebugType=none"
  ], { stdio: "inherit" });

  const builderPath = path.join(outputDir, builderFileName(platform));
  if (!fs.existsSync(builderPath)) {
    throw new Error(`dotnet publish did not produce ${builderPath}.`);
  }

  if (platform !== "win32") {
    fs.chmodSync(builderPath, 0o755);
  }

  console.log(`Built ${path.relative(ROOT, builderPath)} (${fs.statSync(builderPath).size} bytes).`);
  return builderPath;
};

module.exports = { buildShaderBuilder, OUTPUT_DIR };

if (require.main === module) {
  buildShaderBuilder(process.argv[2], process.argv[3]);
}
