using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using RpgEngineAnimatedMeshMod.Patches;
using UnityEngine;

namespace RpgEngineAnimatedMeshMod
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid    = "com.elaravtt.rpgengine.animatedmesh";
        public const string PluginName    = "RPG Engine, Animated Custom Mesh";
        public const string PluginVersion = "3.1.31";

        internal static ManualLogSource Log;

        // If GLTFUtility throws mid-import (animation parser crash, etc.) we
        // preserve the exception by default so nothing is silently swallowed.
        // Turn this on to let the engine finish its post-import work
        // (collider setup, selection wiring), trades animation-parse accuracy
        // for a clickable placed mesh.
        internal static bool SwallowImportExceptions = true;

        // v3.1.27: how strongly an imported model's colour map is fed back in
        // as emission, so it reads like the older Meshy exports (Jade,
        // Dragonsfire) instead of disappearing in dim scenes. 0 = lit only.
        internal static float ImportedTextureGlow = 1f;

        // v3.1.28: hold each imported model's feet on its origin so drag-drops
        // and walking land it on the ground (see GroundOffset).
        internal static bool AutoGroundFeet = true;

        private static ConfigFile _config;

        private void Awake()
        {
            Log = Logger;

            var swallowCfg = Config.Bind(
                section:  "Behavior",
                key:      "SwallowImportExceptions",
                defaultValue: true,
                description: "When GLTFUtility throws during a custom-mesh import, swallow the exception so downstream engine setup (collider, selection handler) completes. Set false to preserve the original error and debug it.");
            SwallowImportExceptions = swallowCfg.Value;

            var glowCfg = Config.Bind(
                section:  "Appearance",
                key:      "ImportedTextureGlow",
                defaultValue: 1f,
                description: "Brightness boost for imported models. Their colour texture is also used as glow so they stay visible in dim scenes, matching older Meshy exports. 1 = same as Jade/Dragonsfire, 0 = scene lighting only, values like 0.5 or 1.5 sit in between or go brighter. Restart the game after changing.");
            ImportedTextureGlow = Mathf.Max(0f, glowCfg.Value);

            var groundCfg = Config.Bind(
                section:  "Appearance",
                key:      "AutoGroundFeet",
                defaultValue: true,
                description: "Keep imported models standing on the ground after you drag them or they walk. The mod measures where each model's feet are and corrects its height automatically. Fine-tune a single model in the [HeightAdjust] section. Restart the game after changing.");
            AutoGroundFeet = groundCfg.Value;
            _config = Config;

            var panelKeyCfg = Config.Bind(
                section:  "Controls",
                key:      "HeightPanelKey",
                defaultValue: new KeyboardShortcut(KeyCode.F8),
                description: "Key that opens and closes the in-game Model Heights panel. F1, F3-F6 and F11 are already used by the game.");
            HeightPanel.ToggleKey = panelKeyCfg.Value;
            HeightPanel.Create();
            Log.LogInfo($"{PluginName} v{PluginVersion} loading…");

            // Everything we touch is inside the RpgEngine assembly. We patch by
            // string name (AccessTools.Method searches all loaded assemblies)
            // so the plugin still finds its targets across minor engine updates.
            var harmony = new Harmony(PluginGuid);

            try
            {
                // v0.4:
                //   • ShaderFallbackPatch, swaps a URP shader in when
                //     GLTFUtility asks for its missing PBR shader, stopping the
                //     ArgumentNullException during GLB material construction.
                //   • CustomMeshCreatorPatch, postfix + finalizer on the real
                //     PickupableMesh import methods (SetRPGMesh /
                //     LoadFromBinaryFile / LoadGlbData). Finalizer fires even
                //     when GLTFUtility crashes so we still install animation
                //     when the skeleton did make it through.
                // Order matters:
                //   1. AnimationSettingsPatch: force useLegacyClips=true on
                //                                 every GLTFUtility ImportSettings
                //                                 so clips are actually populated
                //                                 in the animations out-param
                //   2. ShaderFallbackPatch   : GLB material construction succeeds
                //   3. LegacyClipPatch       : AnimationClip.SetCurve succeeds at runtime
                //   4. GltfImporterCapturePatch, capture clips before the engine drops them
                //   5. CustomMeshCreatorPatch: install captured clips on the imported mesh
                AnimationSettingsPatch.ApplyAll(harmony);
                ShaderFallbackPatch.ApplyAll(harmony);
                // v3.1.26: the URP fallback reads _BaseMap, GLTFUtility writes
                // _MainTex. Copy it across so current Meshy exports import
                // textured instead of as a blank (dark chrome) shape.
                MaterialFixPatch.ApplyAll(harmony);
                LegacyClipPatch.ApplyAll(harmony);
                GltfImporterCapturePatch.ApplyAll(harmony);
                // v1.0: capture clips directly at GLTFAnimation.Import, the
                // fix for the "10 imports fire but Importer.LoadInternal's
                // out-param stays null" mystery revealed by v0.9.1 diag.
                GltfAnimationImportCapturePatch.ApplyAll(harmony);
                CustomMeshCreatorPatch.ApplyAll(harmony);
                // v1.3: bridge our clips into the engine's native animation
                // panel UI so imported meshes get proper buttons in Prop
                // Settings, matching the built-in character menu style.
                // v2.0 flow: NativeAnimationUiPatch's postfix on FindAllAnimations
                // still adds Meshy clip buttons when we FALL BACK to legacy ,
                // it's a no-op for humanoid imports because they don't get an
                // exposer.
                NativeAnimationUiPatch.ApplyAll(harmony);
                // Cache the engine's C_Humanoid controller on first native-char
                // click. Preferred path is the eager Resources scan below ,
                // this is the lazy fallback for engines that haven't loaded
                // the controller by plugin-startup time.
                NativeSkeletonProbe.ApplyAll(harmony);
                // v2.2.2: MoveTo.OnLeftMouseUp is gated on `target is PickupableAI`.
                // Our custom mesh is a PickupableMesh (with HumanoidTag + Puppet),
                // so the gate rejects it. Prefix so tagged meshes get the same
                // walk-to-destination flow (raycast → puppet.ClosestDestination →
                // target.MoveToGlobalLocation → NetworkSend.MoveAI → Hide).
                MoveToInterceptPatch.ApplyAll(harmony);
                // v3.1.3: peer-side smooth walk. Base Pickupable.MoveToGlobalLocation
                // teleports; PickupableAI overrides with puppet.SetDestination. Our
                // custom mesh isn't PickupableAI, so on peers it was teleporting
                // when host walks. Prefix routes through Puppet if HumanoidTag set.
                PeerMovementPatch.ApplyAll(harmony);
                // v2.3: LookAtState.TryLookAt is also gated on `target is PickupableAI`,
                // so vanilla aim-facing (bow shot, ranged FX) doesn't rotate our
                // custom meshes toward the aim point. Prefix so tagged meshes
                // also get the LookAt rotation.
                LookAtInterceptPatch.ApplyAll(harmony);
                // v3.0: multiplayer sync. When the host loads a custom mesh
                // locally, broadcast its bytes to every connected peer so
                // their client cache resolves the mesh instead of showing
                // the '?' placeholder.
                MeshBroadcastPatch.ApplyAll(harmony);
                // v3.1.22: self-heal for corrupt on-disk mesh cache files.
                // If Deserialize throws FormatException (truncated file from
                // interrupted transfer, or format-mismatch from an older mod
                // version's broken broadcast), delete the file so the engine's
                // request-from-server fallback replaces it cleanly next time.
                CorruptCacheSelfHealPatch.ApplyAll(harmony);
                Log.LogInfo("Harmony patches applied.");

                // Eager attempt: often the C_Humanoid controller is loaded
                // by the time our plugin awakes. If so, imports can use the
                // humanoid path without waiting for any user interaction.
                if (NativeControllerCache.TryFind() != null)
                    Log.LogInfo("C_Humanoid controller found eagerly, humanoid retargeting ready.");
                else
                    Log.LogInfo("C_Humanoid controller not loaded yet, will cache on first native-character click.");
            }
            catch (System.Exception ex)
            {
                Log.LogError($"Patch application failed: {ex}");
            }
        }

        /// <summary>
        /// v3.1.28: per-model manual height nudge, in metres. Each model gets
        /// its own [HeightAdjust] line the first time it loads, so the GM can
        /// find it by name. The file is re-read first, so an edit made while
        /// the game runs applies the next time that model loads.
        /// </summary>
        internal static float GetHeightAdjust(string meshId)
        {
            try
            {
                return BindHeightAdjust(meshId, reload: true)?.Value ?? 0f;
            }
            catch (System.Exception ex)
            {
                Log.LogWarning($"Could not read [HeightAdjust] for '{meshId}': {ex.Message}");
                return 0f;
            }
        }

        /// <summary>
        /// v3.1.29: set from the in-game HeightPanel. Saves the entry (BepInEx
        /// writes the file on set) and applies it live to every placed copy.
        /// </summary>
        internal static void SetHeightAdjust(string meshId, float metres)
        {
            metres = Mathf.Round(metres * 100f) / 100f;
            try
            {
                var entry = BindHeightAdjust(meshId, reload: false);
                if (entry != null) entry.Value = metres;
            }
            catch (System.Exception ex)
            {
                Log.LogWarning($"Could not save [HeightAdjust] for '{meshId}': {ex.Message}");
            }

            foreach (var ground in GroundOffset.All)
                if (ground != null && ground.MeshId == meshId) ground.SetManual(metres);
            Log.LogInfo($"HeightPanel: '{meshId}' manual height set to {metres:+0.00;-0.00;0}m.");
        }

        private static ConfigEntry<float> BindHeightAdjust(string meshId, bool reload)
        {
            if (_config == null || string.IsNullOrEmpty(meshId)) return null;
            var key = meshId.Trim();
            foreach (var bad in new[] { '=', '\n', '\t', '\\', '"', '\'', '[', ']' })
                key = key.Replace(bad, '_');
            if (key.Length == 0) return null;

            if (reload) _config.Reload();
            return _config.Bind(
                section:  "HeightAdjust",
                key:      key,
                defaultValue: 0f,
                description: "Extra height in metres for this model, on top of the automatic feet-on-ground fix. Positive raises it, negative lowers it (0.05 = 5 cm up). Easiest to change in game with the Model Heights panel (F8).");
        }
    }
}
