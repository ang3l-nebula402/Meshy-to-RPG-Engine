using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RpgEngineAnimatedMeshMod.Patches
{
    /// <summary>
    /// Force GLTFUtility's AnimationSettings.useLegacyClips = true.
    ///
    /// GLTFUtility exposes an ImportSettings → AnimationSettings → useLegacyClips
    /// flag. When false (the default), GLTFUtility either skips animation
    /// import entirely or builds Mecanim-style clips whose SetCurve calls fail
    /// at runtime, either way the animations out-param comes back null, which
    /// is exactly what we've been seeing in the log.
    ///
    /// Fix: postfix the AnimationSettings default constructor to flip
    /// useLegacyClips=true right when the object is constructed. Every
    /// subsequent GLTFUtility import, including the RPG Engine's, will
    /// build proper legacy clips that our downstream code can play through
    /// a UnityEngine.Animation component.
    /// </summary>
    internal static class AnimationSettingsPatch
    {
        internal static void ApplyAll(Harmony harmony)
        {
            var log = Plugin.Log;

            var settingsType = AccessTools.TypeByName("Siccity.GLTFUtility.AnimationSettings");
            if (settingsType == null)
            {
                log.LogWarning("Siccity.GLTFUtility.AnimationSettings not found. Cannot force useLegacyClips.");
                return;
            }
            log.LogInfo($"Found {settingsType.FullName}");

            // Log fields so we see the actual shape (bool / property / field).
            foreach (var f in settingsType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                log.LogInfo($"  field: {f.FieldType.Name} {f.Name}");
            foreach (var p in settingsType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                log.LogInfo($"  prop:  {p.PropertyType.Name} {p.Name}");

            // Hook every parameterless / few-arg constructor to force the flag.
            foreach (var ctor in settingsType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                try
                {
                    harmony.Patch(
                        original: ctor,
                        postfix: new HarmonyMethod(typeof(AnimationSettingsPatch), nameof(AnimationSettings_Postfix)));
                    var parms = string.Join(", ", ctor.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                    log.LogInfo($"Patched AnimationSettings.ctor({parms}), will force useLegacyClips=true.");
                }
                catch (Exception ex)
                {
                    log.LogWarning($"Could not patch AnimationSettings constructor: {ex.Message}");
                }
            }

            // Also patch ImportSettings.ctor as belt-and-braces so any nested
            // AnimationSettings created inside it also gets flipped after.
            var importSettingsType = AccessTools.TypeByName("Siccity.GLTFUtility.ImportSettings");
            if (importSettingsType != null)
            {
                foreach (var ctor in importSettingsType.GetConstructors(
                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try
                    {
                        harmony.Patch(
                            original: ctor,
                            postfix: new HarmonyMethod(typeof(AnimationSettingsPatch), nameof(ImportSettings_Postfix)));
                        var parms = string.Join(", ", ctor.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                        log.LogInfo($"Patched ImportSettings.ctor({parms}), will force nested useLegacyClips=true.");
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning($"Could not patch ImportSettings constructor: {ex.Message}");
                    }
                }
            }
        }

        // After AnimationSettings is constructed, flip its useLegacyClips flag.
        // Works for either a field or a property.
        private static void AnimationSettings_Postfix(object __instance)
        {
            SetUseLegacyClips(__instance);
        }

        // After ImportSettings is constructed, find its nested animationSettings
        // and flip its flag too.
        private static void ImportSettings_Postfix(object __instance)
        {
            if (__instance == null) return;
            var t = __instance.GetType();
            foreach (var name in new[] { "animationSettings", "AnimationSettings" })
            {
                var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f == null) continue;
                var val = f.GetValue(__instance);
                if (val != null) SetUseLegacyClips(val);
            }
        }

        private static void SetUseLegacyClips(object animationSettings)
        {
            if (animationSettings == null) return;
            var t = animationSettings.GetType();
            var f = t.GetField("useLegacyClips",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null && f.FieldType == typeof(bool))
            {
                if (!(bool)f.GetValue(animationSettings))
                {
                    f.SetValue(animationSettings, true);
                    Plugin.Log.LogInfo($"Flipped {t.Name}.useLegacyClips=true on new instance.");
                }
                return;
            }
            var p = t.GetProperty("useLegacyClips",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null && p.PropertyType == typeof(bool) && p.CanWrite)
            {
                if (!(bool)p.GetValue(animationSettings))
                {
                    p.SetValue(animationSettings, true);
                    Plugin.Log.LogInfo($"Flipped {t.Name}.useLegacyClips=true (property) on new instance.");
                }
            }
        }
    }
}
