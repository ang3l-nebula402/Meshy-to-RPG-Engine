# Install guide

Two flavours: **players** (drop files in, done) and **developers** (build from source, iterate). Pick whichever describes your relationship with terminals.

---

## Players (Windows)

Grab a release archive from the [Releases](https://github.com/ang3l-nebula402/Meshy-to-RPG-Engine/releases) page, or from whoever's sharing the mod with you.

### Easiest: the installer

Extract the archive and double-click `install-windows.bat`. It finds the game folder, copies everything in, and unblocks the DLLs for you. Then launch the game via Steam. If it can't find the game, use the manual steps below.

### Updating an install you already have

If BepInEx and an older version of the mod are already installed, you only need the new mod file. Download `RpgEngineAnimatedMeshMod.dll` from the release, put it in `The RPG Engine\BepInEx\plugins\` (replace the old one), then unblock it:
```powershell
Unblock-File "C:\Program Files (x86)\Steam\steamapps\common\The RPG Engine\BepInEx\plugins\RpgEngineAnimatedMeshMod.dll"
```

### Manual steps

1. **Extract the archive.** You'll get a folder with `BepInEx/`, `winhttp.dll`, `doorstop_config.ini`, `changelog.txt`.
2. **Copy the contents into your RPG Engine install folder.** Usually:
   ```
   C:\Program Files (x86)\Steam\steamapps\common\The RPG Engine\
   ```
   The files land next to `The_RPG_Engine.exe` and merge with any existing `BepInEx/` you have.
3. **Unblock the DLLs.** This is the step everyone forgets and then wonders why nothing works. Windows quietly refuses to load DLLs it thinks came from the internet. Open PowerShell in the game folder and run:
   ```powershell
   cd "C:\Program Files (x86)\Steam\steamapps\common\The RPG Engine"
   Get-ChildItem -Recurse | Unblock-File
   ```
4. **Launch the game via Steam.**

### Did it work?

Open `BepInEx\LogOutput.log` in the game folder. Near the top you should see:

```
RPG Engine, Animated Custom Mesh v3.1.30 loading
Harmony patches applied.
```

If the log file doesn't exist at all, BepInEx never fired. Jump to [Troubleshooting](#troubleshooting-windows).

---

## Players (Linux, Steam Proton)

Same as Windows-native, but with one extra Proton dance step:

1. Extract + copy files as above, but into:
   ```
   ~/.local/share/Steam/steamapps/common/The RPG Engine/
   ```
2. Right-click **The RPG Engine** in Steam, go to **Properties** → **Launch Options**, and set:
   ```
   WINEDLLOVERRIDES="winhttp=n,b" %command%
   ```
   This tells Proton "please use the winhttp.dll we brought, not the one Wine ships with." Without this the mod does absolutely nothing.
3. Launch.

---

## Developers (build from source)

If you want to hack on the mod.

### Prerequisites

- .NET SDK 6.0 or newer.
- BepInEx 5.4.23.2 (Windows x64) installed in your game folder.
- Access to the RPG Engine's `The_RPG_Engine_Data/Managed/` folder for reference DLLs (the csproj points at it).

### Build

```bash
cd src
dotnet build -c Release
```

The build has an `AfterTargets="Build"` step that auto-copies the DLL straight into:
```
$GAMEDIR/BepInEx/plugins/RpgEngineAnimatedMeshMod.dll
```

Default `$GAMEDIR` is `~/.local/share/Steam/steamapps/common/The RPG Engine/`. Point elsewhere with:

```bash
dotnet build -c Release -p:GAMEDIR="/your/path/to/The RPG Engine"
```

### Iteration loop

1. Edit code in `src/`.
2. `dotnet build -c Release`.
3. Restart the game (the DLL's already copied).
4. Read `BepInEx/LogOutput.log`, gasp or celebrate accordingly.

### Live console logs (highly recommended while hacking)

Edit `BepInEx/config/BepInEx.cfg`:

```ini
[Logging.Console]
Enabled = true
```

You'll get a separate console window with live plugin output. Much more fun than tailing a file.

---

## Troubleshooting (Windows)

**`BepInEx\LogOutput.log` doesn't exist after launch.**  
99% of the time this is either DLLs Windows-blocked (see step 3 above and its `Unblock-File`) or `winhttp.dll` is not in the game folder. It has to sit **right next to `The_RPG_Engine.exe`**, not inside `BepInEx/`. If both look right, try re-downloading; the file could be truncated.

**Log exists but never mentions `Animated Custom Mesh`.**  
The plugin DLL didn't load. Check that `BepInEx\plugins\RpgEngineAnimatedMeshMod.dll` actually exists, and unblock it too:
```powershell
Get-ChildItem "$path\BepInEx\plugins\" | Unblock-File
```

**Character imports and just stands there like a mannequin.**  
The mod loaded but couldn't build a humanoid animator for that particular rig. Look for `HumanoidAvatarBuilder: '...' missing required bones` in the log. Usually the rig has non-standard bone names the mod doesn't recognize. [RIG-GUIDE.md](RIG-GUIDE.md) has more on this.

**Multiplayer peer sees a `?` where the character should be.**  
They don't have the mod installed. Send them the release zip and the install instructions. See [README.md § Multiplayer](README.md#multiplayer).

## Troubleshooting (Linux / Proton)

**`BepInEx\LogOutput.log` doesn't exist.**  
Almost always the launch option. Make sure it's exactly:
```
WINEDLLOVERRIDES="winhttp=n,b" %command%
```
Case matters, quotes matter, the trailing `%command%` matters.

**Plugin loads but you see method-hook warnings.**  
Look for `[Warning]` lines about `AccessTools.TypeByName: Could not find type`. That's the engine's internal type names having shifted, usually because of a game version bump. File an issue with the log attached and we'll add fallback lookups.
