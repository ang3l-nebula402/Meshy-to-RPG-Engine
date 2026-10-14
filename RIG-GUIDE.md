# Getting a Character into the Engine

The whole "Meshy AI to walking-around-in-your-game" journey, start to finish. Should take about five minutes if it's your first time and ninety seconds after that.

## 1. Make a rigged character in Meshy

Head to [meshy.ai](https://www.meshy.ai). Generate a character from a prompt, or upload your own 3D model. Either way, use Meshy's **Rig** feature to bolt a humanoid skeleton onto it. You want a proper humanoid rig, not a "static object" export.

## 2. Download the GLB

Hit download, choose `.glb`, and tick the **Rigged Character** box.

Meshy will hand you **two** `.glb` files. You want the one whose filename does **not** have `animated` in it. The animated one has a walk cycle baked in, which sounds like what you want but isn't. This mod supplies the animations from the engine's own library, so you want the plain rigged file. Trust the process.

## 3. Get it into the engine

1. Launch the game.
2. Hit **Tab** to enter Editing Mode. This is where the fun happens.
3. Open the **Prop Master**.
4. Type `import` in the search box and grab the **Custom Mesh** entry.
5. Drop that Custom Mesh onto your canvas. It'll look pretty boring for now.

## 4. Feed it the GLB

1. Click your placed Custom Mesh. The **Prop Settings** panel opens on the right.
2. In Prop Settings, click the **Import** button. Not the *Import Texture* button (that's a trap for later). Just the one labelled Import.
3. Point it at the `.glb` you downloaded from Meshy.
4. Your character appears with its textures already applied, because the mod knows what you meant.

## 5. Save it for reuse

1. Click the **floppy disc icon** in the top left.
2. Give the prop a memorable name (or a forgettable one, we don't judge).

Done. Your character now lives in the prop menu like every other prop, with the full humanoid animation library, walk-to movement, and everything else this mod does.

## When things look weird

**Character looks distorted during animations.**  
The rig is probably using bone axes the engine's animations weren't built for. Meshy's default rig should be fine; if you re-rigged in Mixamo, AccuRIG, Blender, or something else and it's misbehaving, that's most likely why.

Exports with the arms angled down (an A-pose, which Meshy's Mixamo skeleton option produces) used to fold the arms behind the back. The mod now straightens A-pose arms when it sets up the animations, so either pose works.

**Character floats above the ground or sinks into it.**  
The mod measures each model and keeps its feet on the floor automatically, through drags, walking and reloads. If one is still a little off, press **F8** in game to open the Model Heights panel and nudge it up or down in centimetres. The change applies to every copy of that model straight away and is saved for next time. If you raised a model by hand in edit mode before this fix, drag it once so it lands at the corrected height.

**Character imports as a static mannequin, no animations.**  
Check `BepInEx\LogOutput.log` for a line about missing required bones. Means the mod couldn't find enough of the humanoid skeleton to build an avatar. Usually a rig-name mismatch. Meshy's default names are known-good.
