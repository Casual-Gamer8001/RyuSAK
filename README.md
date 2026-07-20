# RyuSAK 1.7.0 Community Build

![showDownloads](https://img.shields.io/github/downloads/Ecks1337/RyuSAK/total?style=for-the-badge)
![showVersion](https://img.shields.io/github/package-json/v/Ecks1337/RyuSAK?style=for-the-badge)
![showLicense](https://img.shields.io/github/license/Ecks1337/RyuSAK?style=for-the-badge)

<p align="center">
  <img width="80%" alt="screenshot" src="https://raw.githubusercontent.com/Ecks1337/RyuSAK/master/screenshot.png" />
</p>

## Installation
Download `RyuSAK-1.7.0-Setup.exe` from this fork's releases. The setup wizard lets you choose either a standard install or a portable install.

### Windows

#### Standard install
Choose **Standard install** in setup to store RyuSAK settings in your Windows user profile.

#### Portable
Choose **Portable install** in setup to create the `portable` marker and keep settings in an `electron_cache` folder next to `RyuSAK.exe`.

## Features
* Add one or multiple Ryujinx folders (where `Ryujinx.exe` is located) to manage different builds (such as mainline, portable, LDN, etc.) 
* List your game library
* Display your local shaders count & RyuSAK shaders count (to download them if you have fewer shaders)
* Download and share shader caches through the Azure shader backend
* Use Azure title metadata and cover fallback
* Search SteamGridDB covers with your own API key
* Set custom game titles and covers
* Hide games from the RyuSAK library
* Show Ryubing compatibility data from the cached compatibility CSV
* Download saves for a specific game
* Download shaders for a specific game
* Downloads mods for a specific game

Product keys and firmware download/install functionality have been removed from this community build.

## Contributing
Requirements:
* NodeJS v14.20.0

Install dependencies: `npm install --include=dev`

Run local build: `npm start`

## Credits
* CapitaineJSparrow for creating the original [emusak-ui](https://github.com/CapitaineJSparrow/emusak-ui) project
* Ecchibitionist for hosting the firmware, saves, shaders and mods on his CDN
