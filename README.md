# AfNotAllon: online co-op for Afallon

A BepInEx mod that turns Afallon into an online co-op game for up to 5 players, counting players and their bots
together. The host's world is shared: everyone sees the same mobs, bosses, weather, time of day, world events and
auction house. Each player keeps their own character and save.

- **Host and join:** main menu > CO-OP. Host over Steam (your Steam friends see the game) or LAN/IP, with an optional
  password. Join from your Steam friends' games, the LAN list or IP:port.
- **Shared world:** the same mobs for everyone, with their attacks, projectiles, boss phases, casts and ground effects.
  Time of day, weather, world quests and random world events follow the host.
- **Combat:** XP, loot and quest credit go to everyone who took part. Buffs and debuffs show on other players. Mounts,
  druid forms and dragon flights are visible to everyone.
- **Groups (O):** the game's own party frames. Bots heal, protect and revive the whole group. Follow, Summon and Travel to.
- **Dungeons:** each group gets its own instance. The Dungeon Finder queues the whole group, bots fill the missing
  roles, group loot uses Need/Greed rolls.
- **Between players:** trade, duels, inspect, mail (to offline players too), friends, shared quests, a damage meter.
- **Host tools:** Esc > CO-OP INFO shows your LAN addresses and the players, and lets you ban a player (and lift the
  ban). F9 pauses the game for everyone.

This is an unofficial fan mod. Afallon belongs to its developers.

---

## For players: install

### What you need
- **Afallon 0.16.3** from Steam, on Windows 64-bit. Every player needs the same game version and the same mod version.
- **BepInEx 6 Bleeding Edge, IL2CPP, build #788** (the mod loader). Other BepInEx versions are not tested.
  Download: [BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788+5b766a3.zip](https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip)
  (from [builds.bepinex.dev](https://builds.bepinex.dev/projects/bepinex_be)).
- **The mod:** `AfNotAllon-<version>.zip` from this repository's [Releases](../../releases).

### Steps
1. Open the game folder: in Steam, right-click Afallon > Manage > Browse local files. It is the folder with `Afallon.exe`.
2. Unzip the **BepInEx** archive into that folder. You should now have `winhttp.dll`, `doorstop_config.ini` and the
   `BepInEx` and `dotnet` folders next to `Afallon.exe`.
3. Start the game once and wait for the main menu. The first start takes 1-2 minutes longer: BepInEx prepares itself
   (it downloads the Unity libraries for the game once). Close the game.
4. Unzip the **mod** archive into the same folder. It adds `BepInEx/plugins/AfallonCoop/`.
5. Start the game. The main menu now has a CO-OP button.

### Uninstall
Delete `BepInEx/plugins/AfallonCoop`. To remove BepInEx too, delete `winhttp.dll`, `doorstop_config.ini`,
`.doorstop_version`, `changelog.txt` and the `BepInEx` and `dotnet` folders.

### Troubleshooting
- **The log** is `BepInEx/LogOutput.log` in the game folder. Please attach it to bug reports.
- **`Failed to generate Il2Cpp interop assemblies ... End of Central Directory`** or **`Could not load ...
  UnityEngine.CoreModule`**: BepInEx could not download the Unity libraries. Download
  [6000.3.23.zip](https://unity.bepinex.dev/libraries/6000.3.23.zip), put it in `BepInEx/unity-libs/` (create the
  folder if needed; replace a broken file of that name) and start the game again.
- **"Version mismatch" / "protocol mismatch"** when joining: the players have different game builds or mod versions.
- **"This game has a password" / "Wrong password"**: type the host's password in CO-OP > JOIN GAME and join again.
- **Steam game not listed:** only the host's Steam friends see and can join a Steam game. Others join over LAN/IP.
- A game update can break the mod until a new version comes out.

---

## For developers: build from source

### What you need
1. **Windows** and the **.NET 8 SDK** ([download](https://dotnet.microsoft.com/download/dotnet/8.0)). It builds the
   plugin (net6.0) and the tests (net8.0).
2. **Afallon with BepInEx installed and started once** (player steps 1-3 above). The build references the game's
   interop assemblies that BepInEx generates on that first start, in `<game>/BepInEx/interop`.
3. **Steamworks.NET 2025.164.1, standalone build:** download
   [Steamworks.NET-Standalone_2025.164.1.zip](https://github.com/rlabrecque/Steamworks.NET/releases/tag/2025.164.1)
   and unzip it into `lib/steamworks/` in this repository, so that `lib/steamworks/Windows-x64/Steamworks.NET.dll` and
   `lib/steamworks/Windows-x64/steam_api64.dll` exist.
   LiteNetLib and the test libraries come from NuGet by themselves.

The game is expected in `C:\Program Files (x86)\Steam\steamapps\common\Afallon`. If yours is elsewhere, add
`-p:GameDir="D:\Path\To\Afallon"` to the commands below.

### Build
```
dotnet build src/AfallonCoop.Plugin
```
This builds the **Debug** version and copies the mod into the game (`BepInEx/plugins/AfallonCoop`); close the game
first, or the copy fails because the DLL is in use. The Debug version has developer tools on: F6/F7/F8 hotkeys,
traces, and a command file the test scripts drive (`BepInEx/AfCoop_cmd_<pid>.txt`).

```
dotnet build src/AfallonCoop.Plugin -c Release
```
The **Release** version is the one for players: the developer tools are not in it. Add `-p:Deploy=false` to build
without copying into the game (the files are then in `src/AfallonCoop.Plugin/bin/Release/net6.0/`).

```
dotnet test src/AfallonCoop.Tests
```
Runs the tests of the network core (sessions, groups, ownership of mobs, passwords, bans...). They need no game.

### Make a release archive
1. Raise the version in both places: `Version` in `src/AfallonCoop.Plugin/Plugin.cs` and `<Version>` in
   `src/AfallonCoop.Plugin/AfallonCoop.Plugin.csproj`. Players with different versions cannot play together.
2. Build the archive:
   ```
   dotnet build src/AfallonCoop.Plugin -c Release -p:Package=true -p:Deploy=false
   ```
   It writes `dist/AfNotAllon-<version>.zip`, laid out to be unzipped into the game folder:
   ```
   README.md
   BepInEx/plugins/AfallonCoop/AfallonCoop.dll
   BepInEx/plugins/AfallonCoop/AfallonCoop.Core.dll
   BepInEx/plugins/AfallonCoop/LiteNetLib.dll
   BepInEx/plugins/AfallonCoop/Steamworks.NET.dll
   BepInEx/plugins/AfallonCoop/steam_api64.dll
   ```
   BepInEx is not in it: players install it themselves (see above).
3. On GitHub: Releases > Draft a new release, tag `v<version>`, attach the zip, and paste the changelog.

### Project layout
- `src/AfallonCoop.Core`: the network core with no game code in it: sessions and the handshake, the LAN (LiteNetLib)
  and Steam transports, groups, who simulates which mobs. Covered by the tests.
- `src/AfallonCoop.Plugin`: the BepInEx plugin, the game side: menus, players' look and motion, mobs, combat, states,
  dungeons, loot, trade, mail, auction house... One file per feature; `Plugin.cs` installs them.
- `src/AfallonCoop.Tests`: tests of the core (xUnit).

---

## Credits
- [BepInEx](https://github.com/BepInEx/BepInEx) (LGPL-2.1), Il2CppInterop and HarmonyX
- [LiteNetLib](https://github.com/RevenantX/LiteNetLib) (MIT)
- [Steamworks.NET](https://github.com/rlabrecque/Steamworks.NET) (MIT) and the Steamworks SDK `steam_api64.dll` (Valve)
- Built with Claude Code (Anthropic, Claude Opus model): AI wrote the code under human direction and testing. The mod
  contains no AI-generated art or audio.

## License
The mod's code is under the [MIT license](LICENSE). BepInEx, LiteNetLib and Steamworks.NET keep their own licenses.

## Changelog
- **0.6.0:**
  - Fixes: no more lost items or gold when the host is on a loading screen; trades that freeze when someone
    disconnects give the items back; a loot roll won by a player who left reaches them by mail; auction mail is no
    longer lost; a player who rejoins no longer keeps another player's form, mount or duel.
  - Fixes: effects and heal-over-time ticks no longer play twice; bots show the right rings and stats, and inspect
    shows their real items; a bot handed to another player keeps its item stats.
  - Less traffic: the auction house updates every 3 s only while someone has it open, mobs are sent only to players
    near them.
  - New: host password, ban list, Steam games open to the host's friends only, scrolling server lists, SHOW DEBUG
    switch (the status line in the corner, off by default).
  - Removed: joining by lobby code (it did not work); join Steam friends from the list instead.
- **0.5.0:** dungeon instances and Dungeon Finder for groups, corruption keys, challenge stones, group loot rolls,
  trade, duels, inspect, mail, friends, shared auction house, shared world quests and random events, boss mechanics on
  every machine, dragon flights, Follow/Summon/Travel to, riding druid forms, quest sharing, shared damage meter.
- **0.4.0:** groups with native frames, bot companions for the group, buffs and debuffs sync, mounts and forms.
- **0.3.0:** shared mobs, combat, loot and quest credit, pets and bots, world time and weather.
