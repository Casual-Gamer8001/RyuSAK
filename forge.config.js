const fs = require("fs-extra");
const path = require("path");
const Zip = require("adm-zip");
const { buildUpdateHelper } = require("./scripts/build-update-helper");

module.exports = {
  "forge": "./forge.config.js",
  "packagerConfig": {
    "icon": "./src/assets/icon",
    "executableName": "RyuSAK"
  },
  "makers": [
    { "name": "@electron-forge/maker-squirrel", "config": { "setupIcon": "./src/assets/icon.ico" } },
    { "name": "@electron-forge/maker-dmg", "config": { "background": "./src/assets/dmg_background.png", "icon": "./src/assets/icon.icns" } },
    { "name": "@electron-forge/maker-zip" },
    { "name": "@electron-forge/maker-deb" },
    { "name": "@electron-forge/maker-rpm" }
  ],
  "plugins": [
    [
      "@electron-forge/plugin-webpack",
      {
        "mainConfig": "./webpack.main.config.js",
        "renderer": {
          "config": "./webpack.renderer.config.js",
          "entryPoints": [
            { "html": "./src/index.html", "js": "./src/renderer.ts", "name": "main_window" }
          ]
        }
      }
    ]
  ],
  "hooks": {
    // Windows builds ship the update helper and its build receipt in resources/updater.
    "packageAfterCopy": async (_, buildPath, __, platform) => {
      if (platform !== "win32") {
        return;
      }

      const { helperPath, receiptPath } = buildUpdateHelper();
      const updaterDir = path.resolve(buildPath, "..", "updater");
      fs.ensureDirSync(updaterDir);
      fs.copySync(helperPath, path.join(updaterDir, path.basename(helperPath)));
      fs.copySync(receiptPath, path.join(updaterDir, path.basename(receiptPath)));
    },
    "postMake": async (_, makeResults) => {
      const zipPath = makeResults.map(result => result.artifacts).flat().find(path => path.endsWith(".zip"));
      if (zipPath) {
        const zip = new Zip(zipPath);
        zip.addFile("portable", Buffer.from("portable", "utf8"));
        fs.removeSync(zipPath);
        zip.writeZip(zipPath);
      }
    }
  }
};