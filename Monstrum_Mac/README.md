# Monstrum Mac

Therzie's **Monstrum**, **Armory**, **Warfare** and **Wizardry**, set up to work on a Mac.

On a Mac, Valheim draws everything with Apple's Metal. Mods built on Windows ship their shaders without any Metal
code, so on a Mac their creatures, armour, weapons and spell effects can show up pink, invisible, or as dark
squares. This pack installs Therzie's mods together with **Monstrum-Mac.dll**, a small plugin that moves those
materials onto Valheim's own shaders (or the closest match), keeping their textures and colours.

On Windows and Linux the plugin does nothing, so the pack is safe to share with friends on any platform.

## What gets installed

- **Monstrum-Mac.dll** (the plugin in this package)
- **Monstrum**, **Armory**, **Warfare** and **Wizardry** by Therzie, downloaded from Therzie's own Thunderstore
  pages. All credit for those mods goes to Therzie.
- **BepInExPack_Valheim**, the mod loader

Therzie's older **Monstrum Deep North** and **Warfare Fire and Ice** are not included: they were made before
Valheim 1.0 and freeze the game at the main menu on it.

## Installing on a Mac

### With a mod manager

[Macheim](https://github.com/lofcgi/macheim) is a Thunderstore mod manager for macOS. Search for
**Monstrum Mac**, install it, and the manager installs everything listed above with it.

### By hand

1. Open the Valheim folder: in Steam, right-click Valheim, then **Manage > Browse local files**. On a Mac this is
   `~/Library/Application Support/Steam/steamapps/common/Valheim`.
2. Download **BepInExPack_Valheim** and copy everything inside its `BepInExPack_Valheim` folder into the Valheim folder.
3. Download each of Therzie's mods above and put its `.dll` files in `BepInEx/plugins`.
4. Put `Monstrum-Mac.dll` from this package's `plugins` folder in `BepInEx/plugins` too.
5. Start the game with mods from Terminal. On Apple Silicon, BepInEx only runs under Rosetta, so the command
   starts the game through it:

   ```
   cd ~/Library/Application\ Support/Steam/steamapps/common/Valheim
   chmod +x start_game_bepinex.sh
   arch -x86_64 ./start_game_bepinex.sh "$PWD/Valheim.app/Contents/MacOS/load"
   ```

   If macOS says Rosetta isn't installed, run `softwareupdate --install-rosetta --agree-to-license` first.
   Starting Valheim from Steam as usual starts it **without** mods.

## Settings

`BepInEx/config/Monstrum-Mac.cfg`:

| Setting | Default | What it does |
|---|---|---|
| `General.Enabled` | `true` | Turns the plugin off without uninstalling it. |
| `Diagnostics.LogEachMaterial` | `false` | Writes a line to the BepInEx log for every material moved to another shader. |
| `Diagnostics.SnapshotKey` | `F8` | Saves a report of what is on screen (see below). |

## If something still looks wrong

Stand where you can see it and press **F8**. The plugin writes `snapshot.txt` and `audit.txt` to
`~/Library/Caches/Monstrum-Mac/`. Include those two files and `BepInEx/LogOutput.log` when you report it.

## Known limits

- The replacement shaders are close, not identical. Some glows and particles look a little different from Windows.
- Heat-haze (distortion) effects are hidden, because nothing on the Mac can draw them.
- The first load into a world takes a moment longer while the plugin checks which shaders work.
- With mods, Valheim runs under Rosetta on Apple Silicon, which costs some speed. Lower graphics settings help,
  especially on a MacBook Air.
