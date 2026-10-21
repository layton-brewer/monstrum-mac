# Monstrum Mac

A BepInEx plugin that makes Valheim mods built on Windows draw properly on a Mac.

Valheim on macOS renders with Apple's Metal. Mods built on Windows usually ship their asset bundles with only
DirectX (and sometimes OpenGL/Vulkan) shader code, so on a Mac their materials show up bright pink, invisible, or
as dark squares, and the Unity log says `Desired shader compiler platform 14 is not available in shader blob`.
Monstrum Mac finds those materials at runtime and moves them onto Valheim's own shaders (or the closest working
match), keeping their textures, colours and transparency. It was built for Therzie's Monstrum, Armory, Warfare
and Wizardry, but it works on any mod's materials.

On Windows and Linux it stays idle, so it is safe in a mod pack shared with friends on any platform.

- Thunderstore: https://thunderstore.io/c/valheim/p/Kitty_Meow/Monstrum_Mac/
- Player-facing readme and install steps: [Monstrum_Mac/README.md](Monstrum_Mac/README.md)
- Changes: [Monstrum_Mac/CHANGELOG.md](Monstrum_Mac/CHANGELOG.md)

## How it works

1. **Find broken materials.** After every asset bundle load and scene change, the plugin checks each loaded
   material. A shader counts as broken when Unity reports it unsupported, or when a small off-screen test draw
   comes back pink (some shaders claim Metal support but have no working Metal code).
2. **Pick a replacement.** Each broken material goes to the best working game shader for what it is: lit Standard
   for solid and cut-out surfaces, legacy particle shaders (Alpha Blended or Additive) for particles, trails and
   lines, and invisible for heat-haze distortion, which nothing on the Mac can draw.
3. **Keep the look.** Textures (albedo and normal map), colour, tint, keywords and blend mode are carried over,
   and see-through materials keep their render queue so they still sort correctly.
4. **Stay cheap.** Test draws are read back asynchronously a few per frame, verdicts are cached per shader, and
   background re-checks back off to every 10 minutes once nothing new needs fixing.

Press **F8** (fn+F8 on a Mac keyboard) in game to write `snapshot.txt` and `audit.txt` to
`~/Library/Caches/Monstrum-Mac/`, listing what is on screen and which shader each material really uses.

## Source layout

| Path | What |
|---|---|
| `src/Plugin.cs` | Entry point, config, Metal-only gate, sweep scheduling |
| `src/Fallback.cs` | Shader tests and the material-to-replacement rules |
| `src/Bundles.cs` | Asset bundle hooks (and optional swap for pre-converted bundles in the cache folder) |
| `src/Snapshot.cs` | F8 snapshot and audit reports |
| `Monstrum_Mac/` | Thunderstore package: manifest, readme, changelog, default config |
| `make-release.sh` | Builds the DLL and zips the package |

## Building

You need a Mac with Valheim and [BepInExPack_Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/)
installed (the build references the game's and BepInEx's DLLs; they are not in this repo), and the .NET SDK.

```sh
# Install the .NET SDK once, if you don't have it
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0

git clone https://github.com/layton-brewer/monstrum-mac.git
cd monstrum-mac
zsh make-release.sh
```

The DLL lands in `Monstrum_Mac/plugins/Monstrum-Mac.dll`. To make the Thunderstore zip, add a 256x256
`Monstrum_Mac/icon.png` first. Set `VALHEIM_DIR` if Valheim is not in the default Steam folder.

## Credits

- Therzie's mods (Monstrum, Armory, Warfare, Wizardry) are by [Therzie](https://thunderstore.io/c/valheim/p/Therzie/).
  This repo contains none of their files; the Thunderstore package lists them as dependencies.
- [BepInEx](https://github.com/BepInEx/BepInEx) and [Harmony](https://github.com/pardeike/Harmony).

## License

[MIT](LICENSE) © Layton Brewer
