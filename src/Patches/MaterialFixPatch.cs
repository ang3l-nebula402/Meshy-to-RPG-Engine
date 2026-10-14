using System;
using System.Collections.Generic;
using HarmonyLib;
using Siccity.GLTFUtility;
using UnityEngine;

namespace RpgEngineAnimatedMeshMod.Patches
{
    /// <summary>
    /// v3.1.26: make the base colour texture actually render on GLB imports.
    ///
    /// GLTFUtility writes the glTF base colour to `_MainTex`, but the URP/Lit
    /// shader ShaderFallbackPatch swaps in samples `_BaseMap`, so the colour
    /// map never shows. It also leaves `_Metallic` at the glTF default of 1,
    /// which turns the blank model into dark chrome.
    ///
    /// Older Meshy exports hid this by duplicating the colour map into the
    /// emissive slot, so Jade/Dragonsfire looked fine. Current Meshy exports
    /// (Meshy Rig, Unity, Blender and other skeleton templates) don't, so they
    /// import as a blank shape.
    ///
    /// Postfix on Importer.LoadInternal, which every GLB/GLTF import passes
    /// through (characters and static props alike): copy `_MainTex` onto
    /// `_BaseMap`, zero metallic and (v3.1.27) reuse the colour map as
    /// emission at Plugin.ImportedTextureGlow. Files that already carry the colour map
    /// in the emissive slot are left untouched, otherwise they'd render the
    /// colour twice and wash out.
    /// </summary>
    internal static class MaterialFixPatch
    {
        internal static void ApplyAll(Harmony harmony)
        {
            var log = Plugin.Log;
            var method = AccessTools.Method(typeof(Importer), "LoadInternal");
            if (method == null)
            {
                log.LogWarning("MaterialFixPatch: Importer.LoadInternal not found, GLB colour textures will import blank.");
                return;
            }

            try
            {
                harmony.Patch(
                    original: method,
                    postfix: new HarmonyMethod(typeof(MaterialFixPatch), nameof(LoadInternal_Postfix)));
                log.LogInfo("Patched Importer.LoadInternal (postfix, maps the glTF base colour texture onto URP _BaseMap).");
            }
            catch (Exception ex)
            {
                log.LogError($"Failed to patch Importer.LoadInternal for materials: {ex.Message}");
            }
        }

        private static void LoadInternal_Postfix(GLTFObject gltfObject, GameObject __result)
        {
            if (__result == null || gltfObject == null) return;
            var log = Plugin.Log;

            try
            {
                if (HasColourInEmission(gltfObject))
                {
                    log.LogInfo($"MaterialFix: '{__result.name}' carries its colour map in the emissive slot (older Meshy export), leaving materials as they are.");
                    return;
                }

                var seen = new HashSet<Material>();
                int mapped = 0, glowing = 0, materials = 0;
                foreach (var renderer in __result.GetComponentsInChildren<Renderer>(true))
                {
                    foreach (var m in renderer.sharedMaterials)
                    {
                        if (m == null || !seen.Add(m)) continue;
                        materials++;

                        if (m.HasProperty("_BaseMap") && m.HasProperty("_MainTex"))
                        {
                            var main = m.GetTexture("_MainTex");
                            if (main != null)
                            {
                                m.SetTexture("_BaseMap", main);
                                m.SetTextureScale("_BaseMap", m.GetTextureScale("_MainTex"));
                                m.SetTextureOffset("_BaseMap", m.GetTextureOffset("_MainTex"));
                                mapped++;

                                // v3.1.27: lit-only models read far darker than
                                // the older emissive-colour exports beside them,
                                // so feed the colour map back in as emission the
                                // same way those exports do.
                                if (Plugin.ImportedTextureGlow > 0f && m.HasProperty("_EmissionMap") && m.HasProperty("_EmissionColor"))
                                {
                                    m.SetTexture("_EmissionMap", main);
                                    m.SetColor("_EmissionColor", Color.white * Plugin.ImportedTextureGlow);
                                    m.EnableKeyword("_EMISSION");
                                    glowing++;
                                }
                            }
                        }

                        // URP can't read glTF's metallic-roughness map (different
                        // channel layout), so the material falls back to the
                        // metallic factor, which defaults to fully metal.
                        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
                    }
                }

                log.LogInfo($"MaterialFix: '{__result.name}' mapped base colour on {mapped} of {materials} material(s), metallic→0, glow {Plugin.ImportedTextureGlow} on {glowing}.");
            }
            catch (Exception ex)
            {
                log.LogWarning($"MaterialFix: could not fix materials on '{__result.name}': {ex.Message}");
            }
        }

        /// <summary>
        /// True when any material's emissive texture is the base colour map,
        /// either the same image (Dragonsfire) or a second copy stored under
        /// the same image name (Jade).
        /// </summary>
        private static bool HasColourInEmission(GLTFObject gltf)
        {
            if (gltf.materials == null) return false;
            foreach (var mat in gltf.materials)
            {
                if (mat == null) continue;
                int baseImage = ImageIndex(gltf, mat.pbrMetallicRoughness?.baseColorTexture);
                int emitImage = ImageIndex(gltf, mat.emissiveTexture);
                if (baseImage < 0 || emitImage < 0) continue;
                if (baseImage == emitImage) return true;

                var baseName = gltf.images[baseImage]?.name;
                var emitName = gltf.images[emitImage]?.name;
                if (!string.IsNullOrEmpty(baseName) && baseName == emitName) return true;
            }
            return false;
        }

        private static int ImageIndex(GLTFObject gltf, GLTFMaterial.TextureInfo info)
        {
            if (info == null || gltf.textures == null || gltf.images == null) return -1;
            if (info.index < 0 || info.index >= gltf.textures.Count) return -1;
            var source = gltf.textures[info.index]?.source;
            if (source == null || source.Value < 0 || source.Value >= gltf.images.Count) return -1;
            return source.Value;
        }
    }
}
