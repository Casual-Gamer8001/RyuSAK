# RyuSAK 1.7.1 Community Build

![showDownloads](https://img.shields.io/github/downloads/Casual-Gamer8001/RyuSAK/total?style=for-the-badge)
![showVersion](https://img.shields.io/badge/version-1.7.1-blue?style=for-the-badge)
![showLicense](https://img.shields.io/github/license/Casual-Gamer8001/RyuSAK?style=for-the-badge)

<p align="center">
  <img width="80%" alt="screenshot" src="screenshot.png" />
</p>

This is a community-maintained fork of the original [Ecks1337/RyuSAK](https://github.com/Ecks1337/RyuSAK) project.

## Installation
Download `RyuSAK-1.7.1-Setup.exe` from the [v1.7.1 release](https://github.com/Casual-Gamer8001/RyuSAK/releases/tag/v1.7.1). The setup wizard asks whether you want a standard install or a portable install before you choose the install folder.

### Windows

#### Standard install
Choose **Standard install** in setup to store RyuSAK settings in your Windows user profile.

#### Portable
Choose **Portable install** in setup to create the `portable` marker and keep settings in an `electron_cache` folder next to `RyuSAK.exe`.

## Features
* Add one or multiple Ryujinx/Ryubing data folders to manage different emulator installs
* List your game library
* Download, share, and merge shader caches through the community backend
* Use shader cache variant metadata when different Ryujinx/Ryubing cache formats are available
* Set custom game titles and covers
* Hide duplicates/unknowns from the RyuSAK library
* Show Ryubing compatibility data
* Install supported GameBanana mods directly into the emulator mods folder
* Get clear manual-install guidance when a GameBanana mod cannot be safely installed automatically
* Adjust game icon size with a polished live slider using friendly size labels
* Update from standard or portable installs with a silent installer and visible progress window

In an effort to keep Nintendo from fondling my booty cheeks. Product keys and firmware download/install functionality have been removed from this community build.

## Contributing
Requirements:
* Node.js
* Git

Install dependencies: `npm install --include=dev`

Run local build: `npm start`

Package a Windows build: `npm run build`

## Credits
* Ecks1337 and contributors for the original RyuSAK project
* CapitaineJSparrow for creating the original [emusak-ui](https://github.com/CapitaineJSparrow/emusak-ui) project
