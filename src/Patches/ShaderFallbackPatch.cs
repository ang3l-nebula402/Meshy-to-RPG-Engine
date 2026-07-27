using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RpgEngineAnimatedMeshMod.Patches
{
    /// <summary>
    /// Fix for the ArgumentNullException in Siccity.GLTFUtility.GLTFMaterial.
    ///
    /// GLTFUtility calls Shader.Find("GLTFUtility/Standard (Metallic)") (or
    /// similar names) to build materials for imported GLB models. The RPG
    /// Engine ships with URP but WITHOUT the GLTFUtility shader, Find returns
    /// null, and the subsequent `new Material(null)` throws, killing the whole
    /// coroutine BEFORE mesh + skeleton finish binding. That's why the user's
    /// imported GLB shows up hollow and why our postfix on SetRPGMesh never
    /// runs, the exception unwinds the whole call chain.
    ///
    /// The fix: postfix Shader.Find. When it returned null AND the name looks
    /// like GLTFUtility's expected shader, return the engine's URP Lit shader
    /// instead. Material creation succeeds, GLB parsing finishes cleanly, our
    /// downstream postfix fires, and the mesh renders with a plain URP material
    /// (which the user then re-textures via the engine's Import Texture button
    /// as they already do).
    ///
    /// Very small blast radius: we only intervene when Find already returned
    /// null and the requested name matches known GLTFUtility patterns.
    /// </summary>
    internal static class ShaderFallbackPatch
    {
        // Cache the fallback so we don't Shader.Find on every call.
        private static Shader _fallback;
        private static bool _fallbackAttempted;

        internal static void ApplyAll(Harmony harmony)
        {
            var log = Plugin.Log;
            var findMethod = AccessTools.Method(typeof(Shader), nameof(Shader.Find), new[] { typeof(string) });
            if (findMethod == null)
            {
                log.LogWarning("Shader.Find(string) not found, cannot install shader fallback.");
                return;
            }

            try
            {
                harmony.Patch(
                    original: findMethod,
                    postfix: new HarmonyMethod(typeof(ShaderFallbackPatch), nameof(ShaderFind_Postfix)));
                log.LogInfo("Patched Shader.Find(string) with URP fallback for GLTFUtility shaders.");
            }
            catch (Exception ex)
            {
                log.LogError($"Failed to patch Shader.Find: {ex.Message}");
            }
        }

        // Harmony postfix: __result is the Shader that Find returned; name is
        // the argument. If null AND the name is a GLTFUtility shader, swap in
        // our fallback. Silently pass through everything else.
        private static void ShaderFind_Postfix(string name, ref Shader __result)
        {
            if (__result != null) return;
            if (string.IsNullOrEmpty(name)) return;
            if (!LooksLikeGltfShader(name)) return;

            var fb = GetFallback();
            if (fb != null)
            {
                Plugin.Log.LogInfo($"Shader.Find('{name}') → null; substituting URP fallback '{fb.name}'.");
                __result = fb;
            }
        }

        private static bool LooksLikeGltfShader(string name)
        {
            // GLTFUtility asks for names like:
            //   "GLTFUtility/Standard (Metallic)"
            //   "GLTFUtility/Standard (Specular)"
            //   "GLTFUtility/URP/Standard"
            //   "GLTFUtility/UnlitTransparent"
            // Match by prefix rather than exact string so version drift doesn't
            // silently disable the fallback.
            return name.IndexOf("GLTFUtility", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("GLTF/",       StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Shader GetFallback()
        {
            if (_fallbackAttempted) return _fallback;
            _fallbackAttempted = true;

            // Try URP shaders in order of preference, the engine's own DLLs
            // reference Unity.RenderPipelines.Universal so at least one of
            // these will exist. Fall back to Unity built-in Standard as a
            // last resort so materials at least construct.
            var candidates = new[]
            {
                "Universal Render Pipeline/Lit",
                "Universal Render Pipeline/Simple Lit",
                "Universal Render Pipeline/Unlit",
                "Standard",
                "Hidden/InternalErrorShader"
            };
            foreach (var candidate in candidates)
            {
                // Bypass our own postfix (it only fires for GLTFUtility names)
                var found = Shader.Find(candidate);
                if (found != null)
                {
                    _fallback = found;
                    Plugin.Log.LogInfo($"Shader fallback resolved: '{candidate}'.");
                    return _fallback;
                }
            }
            Plugin.Log.LogWarning("No fallback shader could be resolved. GLB material creation will still fail.");
            return null;
        }
    }
}
