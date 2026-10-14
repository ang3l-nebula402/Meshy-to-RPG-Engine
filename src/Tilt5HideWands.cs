using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RpgEngineAnimatedMeshMod
{
    /// <summary>
    /// v3.1.33: Tilt Five without wands. For tables that use the glasses only
    /// to look at the map, the wand, its laser and the per-player button deck
    /// (the "toolbar") just get in the way.
    ///
    /// Two parts:
    ///   • Input: every wand button, trigger and stick read in the game goes
    ///     through TiltFive.Input, so prefixes there report nothing pressed and
    ///     the engine never acts on the wand.
    ///   • Visuals: every half second, hide the renderers, canvases and
    ///     colliders under each Tilt5Wand and Tilt5Deck, and switch off the
    ///     selection manager (which toggles the laser) and the selectors. The
    ///     objects stay alive so the engine code that references them keeps
    ///     working; the wand pose still tracks, it just isn't drawn.
    /// </summary>
    internal class Tilt5HideWands : MonoBehaviour
    {
        internal static bool Enabled = true;

        private const float ScanIntervalSeconds = 0.5f;

        // TiltFive.Input method name → the prefix that reports "nothing pressed".
        private static readonly (string Method, string Prefix)[] InputMethods =
        {
            ("GetButton",       nameof(Button_Prefix)),
            ("GetButtonDown",   nameof(Button_Prefix)),
            ("GetButtonUp",     nameof(Button_Prefix)),
            ("TryGetButton",    nameof(TryButton_Prefix)),
            ("GetTrigger",      nameof(Trigger_Prefix)),
            ("TryGetTrigger",   nameof(TryTrigger_Prefix)),
            ("GetStickTilt",    nameof(Stick_Prefix)),
            ("TryGetStickTilt", nameof(TryStick_Prefix)),
        };

        private Type[] _hideTypes;
        private Type[] _disableTypes;
        private float _nextScan;
        private bool _logged;

        internal static void ApplyAll(Harmony harmony)
        {
            if (!Enabled) return;
            var log = Plugin.Log;
            var inputType = AccessTools.TypeByName("TiltFive.Input");
            if (inputType == null)
            {
                log.LogWarning("Tilt5HideWands: TiltFive.Input not found, wand input left on.");
                return;
            }

            int patched = 0;
            foreach (var (name, prefix) in InputMethods)
            {
                foreach (var method in inputType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                                                .Where(m => m.Name == name))
                {
                    try
                    {
                        harmony.Patch(method, prefix: new HarmonyMethod(typeof(Tilt5HideWands), prefix));
                        patched++;
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning($"Tilt5HideWands: could not patch TiltFive.Input.{name}: {ex.Message}");
                    }
                }
            }
            log.LogInfo($"Tilt5HideWands: blocked {patched} TiltFive.Input method(s), wands do nothing in game.");
        }

        // "Nothing pressed" for every wand read. The Try* variants still report
        // a successful read, with the value cleared.
        private static bool Button_Prefix(ref bool __result) { __result = false; return false; }
        private static bool TryButton_Prefix(out bool pressed, ref bool __result) { pressed = false; __result = true; return false; }
        private static bool Trigger_Prefix(ref float __result) { __result = 0f; return false; }
        private static bool TryTrigger_Prefix(out float triggerDisplacement, ref bool __result) { triggerDisplacement = 0f; __result = true; return false; }
        private static bool Stick_Prefix(ref Vector2 __result) { __result = Vector2.zero; return false; }
        private static bool TryStick_Prefix(out Vector2 stickTilt, ref bool __result) { stickTilt = Vector2.zero; __result = true; return false; }

        internal static void Create()
        {
            if (!Enabled) return;
            var go = new GameObject("AnimatedMesh_Tilt5HideWands");
            DontDestroyOnLoad(go);
            go.AddComponent<Tilt5HideWands>();
        }

        private void Awake()
        {
            _hideTypes = new[] { "RpgEngine.Tilt5.Tilt5Wand", "RpgEngine.Tilt5.Tilt5Deck" }
                .Select(AccessTools.TypeByName).Where(t => t != null).ToArray();
            _disableTypes = new[] { "RpgEngine.Tilt5.Tilt5SelectionManager", "RpgEngine.Tilt5.Tilt5Selector" }
                .Select(AccessTools.TypeByName).Where(t => t != null).ToArray();
        }

        private void LateUpdate()
        {
            if (Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + ScanIntervalSeconds;

            int hidden = 0;
            foreach (var type in _hideTypes)
            {
                foreach (var obj in UnityEngine.Object.FindObjectsOfType(type))
                {
                    if (!(obj is Component root)) continue;
                    foreach (var renderer in root.GetComponentsInChildren<Renderer>(true)) renderer.enabled = false;
                    foreach (var canvas in root.GetComponentsInChildren<Canvas>(true)) canvas.enabled = false;
                    foreach (var collider in root.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
                    hidden++;
                }
            }

            foreach (var type in _disableTypes)
            {
                foreach (var obj in UnityEngine.Object.FindObjectsOfType(type))
                {
                    if (!(obj is Behaviour behaviour)) continue;
                    behaviour.enabled = false;
                    // The selection manager parks its laser under the wand tip
                    // or fingertip, hide it wherever it currently lives.
                    var laser = AccessTools.Field(type, "laserPointer")?.GetValue(behaviour) as GameObject;
                    if (laser != null)
                        foreach (var renderer in laser.GetComponentsInChildren<Renderer>(true)) renderer.enabled = false;
                }
            }

            if (hidden > 0 && !_logged)
            {
                _logged = true;
                Plugin.Log.LogInfo($"Tilt5HideWands: hid {hidden} wand/toolbar object(s) for the Tilt Five glasses.");
            }
        }
    }
}
