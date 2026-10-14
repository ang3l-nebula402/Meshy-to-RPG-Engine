# Meshy → RPG Engine

![Custom Models](Custom%20Models.png)

Import a character from [Meshy AI](https://meshy.ai) into [The RPG Engine](https://store.steampowered.com/app/1818180/), then watch it walk, fight, dance, and emote using the engine's own built-in animation library. Like a native NPC, but you made it in your pyjamas.

This is a [BepInEx](https://github.com/BepInEx/BepInEx) plugin. You put a file in a folder, the game gets superpowers.

## What you actually get

Without the mod your imported character is basically a very expensive statue. The animation panel either does nothing or plays a single sad T-pose clip. Movement is not a thing. Friends who join your session see a `?` where the character should be, which is not exactly immersive.

With the mod, your Meshy character behaves like it grew up in the engine:

- **The full 135-clip animation library.** Every idle, walk, run, dance, emote, and combat animation the built-in NPCs use, playing correctly on your imported model via Unity's Humanoid retargeting.
- **A Move button that actually moves.** Click Move, pick a spot, character walks there with a proper walk cycle. Works for you and everyone else in the session.
- **Aim-facing.** Bow shots, throws, and other directional actions rotate the character to face the target instead of firing sideways like it's mad at the wall.
- **Automatic multiplayer mesh sync.** Drop a custom mesh, everyone in the session gets it. No "hey can you download this glb and put it in..." over Discord.
- **Feet on the ground.** Meshy models rarely have their feet where the engine expects, so they used to sink or float every time you dragged them. The mod measures each model and keeps its feet on the floor through drags, walking and reloads. Need a centimetre or two either way? Press **F8** in game for the Model Heights panel.
- **Textures that actually show up.** Current Meshy exports used to import as a blank (or dark chrome) shape. The mod puts the colour texture where the engine's shader looks for it, with a gentle glow so models stay readable in dim scenes like the older exports did.
- **Less chrome.** URP's default rendering makes every GLB look like it was dipped in chrome. This dials that back to something closer to what Meshy showed you.
- **No stuck-pose surprises.** Reloading a session used to leave characters in bizarre saved-mid-attack poses. Fixed.

## Requirements

- **The RPG Engine** from Steam (app 1818180). If you don't own it, this mod will not be very useful.
- **BepInEx 5.4.23.2**, Windows x64 build. Yes, even on Linux, because the game runs under Proton. Don't overthink it.
- A humanoid-rigged GLB for your character. Meshy's rigged export works out of the box. Other rigging tools can work too, mileage varies. See [RIG-GUIDE.md](RIG-GUIDE.md) for the how-to.

## Install

Full step-by-step in [INSTALL.md](INSTALL.md). The one-paragraph version:

Download the release zip, drop the contents into your `The RPG Engine\` game folder, and if you're on Windows, right-click every DLL and hit **Unblock** (Windows silently refuses to load unsigned DLLs from the internet, this catches everyone once). Launch the game. Peek at `BepInEx/LogOutput.log` for a line like `RPG Engine, Animated Custom Mesh v3.1.31 loading` and you're in.

## Multiplayer

Two things worth knowing:

- **Everyone in the session needs the mod.** Without it on a peer's machine, they will see your beautifully-imported character standing in a rigid T-pose while it silently judges them.
- **Only the host needs to import the file.** Once the host places a custom mesh, the mod ships the GLB bytes to every peer automatically. No file sharing gymnastics.

## Building from source

If you want to hack on it:

```bash
cd src
dotnet build -c Release
```

The build's `AfterTargets="Build"` step drops the DLL straight into `The RPG Engine\BepInEx\plugins\` so you can build-restart-test in a loop. Edit the game path in [src/RpgEngineAnimatedMeshMod.csproj](src/RpgEngineAnimatedMeshMod.csproj) if yours isn't the default `~/.local/share/Steam/steamapps/common/The RPG Engine/`.

## Under the hood (for the curious)

The mod hangs Harmony patches on various engine methods. Rough tour:

- `CustomMeshCreatorPatch`: postfixes on `PickupableMesh.SetRPGMesh` / `LoadFromBinaryFile` / `LoadGlbData`. This is where every custom mesh gets its humanoid animator, movement wiring, grounding fix, and material tone-down installed.
- `NativeAnimationUiPatch`: bridges the mod's custom mesh into the engine's native animation panel so you get all the normal buttons.
- `NativeSkeletonProbe`: the first time you click any native NPC in the scene, this steals their `C_Humanoid` controller and stashes it so your custom mesh can borrow it.
- `MoveToInterceptPatch` + `PeerMovementPatch` + `LookAtInterceptPatch`: the engine hard-gates several interactions on "is this a PickupableAI?", these prefixes politely let our `PickupableMesh` through the velvet rope.
- `MeshBroadcastPatch`: when the host loads a mesh, this broadcasts the raw bytes to peers using the engine's existing sync packet.
- `MaterialFixPatch`: GLTFUtility writes the colour map to `_MainTex`, the URP shader reads `_BaseMap`. This copies it across, zeroes metallic, and reuses the colour map as emission (`ImportedTextureGlow` in the config). Older exports that already carry the colour in their emissive slot are left alone.
- `GroundOffset` + `HeightPanel`: the engine drops props with their origin on the ground, so each import gets its lowest point measured once it settles and held on that origin (on the hips for humanoids, on the mesh child for static props). The F8 panel edits a per-model `[HeightAdjust]` nudge on top, live.
- A pile of GLTFUtility fallbacks (`ShaderFallbackPatch`, `AnimationSettingsPatch`, `LegacyClipPatch`, `GltfImporterCapturePatch`) that stop the import pipeline from throwing when it hits missing shaders or awkward clip data.

Runtime components attached to each import: `HumanoidTag` (a marker so we know this pickupable is ours), `MovementBridge` (injects the `⁂move⁂` and `Movement Speed` delegates the panel needs), `IdleGuard` (spends its first 30 frames aggressively resetting the animator to Idle to override the engine's stuck-state-restoration bug).

## Known quirks

None of these are dealbreakers, all of them are honest:

- **Weirdly-rigged models can show distortion during action animations.** Meshy's default rig is fine; if you're using a different rigging tool and things look off, [RIG-GUIDE.md](RIG-GUIDE.md) has thoughts.
- **A slight sheen remains** even after the material tone-down. URP interprets Meshy's PBR properties differently than Meshy's own viewer does. The mod gets it close, not identical.
- **Height nudges are per PC.** The automatic feet-on-ground fix runs on everyone's machine, but the nudges you make in the F8 panel are saved on your own PC only.
- **Only the host's imports sync.** If a peer imports a mesh, the host and other peers won't see it. Usually fine (the GM does the imports) but worth knowing.
- **Newly placed characters need one small GM nudge to fully sync on peer screens.** After you place a custom character, click it in edit mode (Tab) and drag it a hair in any direction. That triggers the last bit of engine state that the mod can't cleanly force on its own. One click per placement. After that, everything on the peer side is fully in sync (position, animations, movement, aim). Existing placements load correctly on session restart with no nudge needed.

## License

MIT. See [LICENSE](LICENSE). Do what you want, just don't sue anyone.

Not affiliated with Polydemons or Meshy. Just a fan project.
