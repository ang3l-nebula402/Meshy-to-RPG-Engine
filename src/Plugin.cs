using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using RpgEngineAnimatedMeshMod.Patches;

namespace RpgEngineAnimatedMeshMod
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid    = "com.elaravtt.rpgengine.animatedmesh";
        public const string PluginName    = "RPG Engine, Animated Custom Mesh";
        public const string PluginVersion = "3.1.25";

        internal static ManualLogSource Log;

        // If GLTFUtility throws mid-import (animation parser crash, etc.) we
        // preserve the exception by default so nothing is silently swallowed.
        // Turn this on to let the engine finish its post-import work
        // (collider setup, selection wiring), trades animation-parse accuracy
        // for a clickable placed mesh.
        internal static bool SwallowImportExceptions = true;

        private void Awake()
        {
            Log = Logger;

            var swallowCfg = Config.Bind(
                section:  "Behavior",
                key:      "SwallowImportExceptions",
                defaultValue: true,
                description: "When GLTFUtility throws during a custom-mesh import, swallow the exception so downstream engine setup (collider, selection handler) completes. Set false to preserve the original error and debug it.");
            SwallowImportExceptions = swallowCfg.Value;
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
    }
}
