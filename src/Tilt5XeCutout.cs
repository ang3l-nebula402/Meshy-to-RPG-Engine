using System;
using System.Reflection;
using TiltFive;
using UnityEngine;

namespace RpgEngineAnimatedMeshMod
{
    /// <summary>
    /// v3.1.32: let a Tilt Five XE gameboard show its whole mat.
    ///
    /// The engine clips everything the glasses see to Tilt5BoardCutout, a box
    /// it always sizes as a square from the board's 70 cm width minus an 8 cm
    /// margin, i.e. the standard LE board. An XE board's viewable area is
    /// 70 x 96.7 cm with the extra length on the far side, so once a Tilt Five
    /// board object spreads the map over the whole XE mat, the far third
    /// renders black.
    ///
    /// While the view is locked to a board object and the board is an XE (as
    /// the glasses report it, or the board object's own type setting when they
    /// don't), stretch the box to the board's full viewable area and centre it
    /// on that area. Otherwise the engine's square is left exactly as it was.
    /// </summary>
    internal class Tilt5XeCutout : MonoBehaviour
    {
        internal static bool Enabled = true;

        private const float LookupIntervalSeconds = 2f;

        // XE viewable extents in cm (+X, -X, +Z, -Z), from the Tilt Five SDK,
        // used when the native plugin can't report them.
        private static readonly float[] XeFallbackCm = { 35f, 35f, 61.667f, 35f };
        private static readonly float[] XeRaisedFallbackCm = { 35f, 35f, 55.944f, 35f };

        private Type _cutoutType;
        private Type _boardType;
        private PropertyInfo _lockedToProp;
        private PropertyInfo _scaleRatio;
        private MethodInfo _updateScales;

        private Transform _cutout;
        private Component _board;
        private float _nextLookup;
        private bool _applied;
        private Vector3 _originalLocalPosition;
        private string _lastState;

        internal static void Create()
        {
            var go = new GameObject("AnimatedMesh_Tilt5XeCutout");
            DontDestroyOnLoad(go);
            go.AddComponent<Tilt5XeCutout>();
        }

        private void Awake()
        {
            _cutoutType = HarmonyLib.AccessTools.TypeByName("RpgEngine.Tilt5.Tilt5BoardCutout");
            _boardType = HarmonyLib.AccessTools.TypeByName("RpgEngine.Tilt5.Tilt5Board");
            var scalarType = HarmonyLib.AccessTools.TypeByName("RpgEngine.Tilt5.Tilt5MapScalar");
            _lockedToProp = _boardType?.GetProperty("lockedToProp", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _scaleRatio = scalarType?.GetProperty("scaleRatio", BindingFlags.Static | BindingFlags.Public);
            _updateScales = _cutoutType?.GetMethod("UpdateScales", BindingFlags.Static | BindingFlags.Public);

            if (_cutoutType == null || _boardType == null || _lockedToProp == null || _scaleRatio == null || _updateScales == null)
            {
                Plugin.Log.LogWarning("Tilt5XeCutout: engine Tilt Five types not found, XE mat fix disabled.");
                enabled = false;
            }
        }

        private void LateUpdate()
        {
            if (!FindEngineObjects()) return;

            try
            {
                if (Enabled && TryGetXeExtents(out var extentsCm, out var source))
                    Apply(extentsCm, source);
                else
                    Revert();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Tilt5XeCutout: {ex.Message}, XE mat fix disabled.");
                Revert();
                enabled = false;
            }
        }

        private bool FindEngineObjects()
        {
            if (_cutout != null && _board != null) return true;
            if (Time.unscaledTime < _nextLookup) return false;
            _nextLookup = Time.unscaledTime + LookupIntervalSeconds;

            if (_cutout == null) _cutout = (UnityEngine.Object.FindObjectOfType(_cutoutType) as Component)?.transform;
            if (_board == null) _board = UnityEngine.Object.FindObjectOfType(_boardType) as Component;
            _applied = false;
            return _cutout != null && _board != null;
        }

        /// <summary>
        /// Viewable extents in cm (+X, -X, +Z, -Z) when the view is locked to a
        /// board object and the board is an XE or XE Raised.
        /// </summary>
        private bool TryGetXeExtents(out float[] extentsCm, out string source)
        {
            extentsCm = null;
            source = null;

            var prop = _lockedToProp.GetValue(_board) as Component;
            if (prop == null) return false;

            GameboardType type;
            if (GameBoard.TryGetGameboardType(out type) && type != GameboardType.GameboardType_None)
            {
                source = "glasses";
            }
            else
            {
                // PickupableTilt5Board.board: 0 = LE, 1 = XE, 2 = XE Raised.
                var setting = prop.GetType().GetProperty("boardType")?.GetValue(prop);
                int value = setting == null ? 0 : Convert.ToInt32(setting);
                type = value == 1 ? GameboardType.GameboardType_XE
                     : value == 2 ? GameboardType.GameboardType_XE_Raised
                     : GameboardType.GameboardType_LE;
                source = "board object setting";
            }

            if (type != GameboardType.GameboardType_XE && type != GameboardType.GameboardType_XE_Raised) return false;

            if (GameBoard.TryGetGameboardExtents(type, out var extents))
            {
                extentsCm = new[]
                {
                    extents.ViewableExtentPositiveX.ToCentimeters,
                    extents.ViewableExtentNegativeX.ToCentimeters,
                    extents.ViewableExtentPositiveZ.ToCentimeters,
                    extents.ViewableExtentNegativeZ.ToCentimeters,
                };
            }
            else
            {
                extentsCm = type == GameboardType.GameboardType_XE ? XeFallbackCm : XeRaisedFallbackCm;
            }
            source = $"{type} from {source}";
            return true;
        }

        private void Apply(float[] extentsCm, string source)
        {
            // World units per cm, the same factor the engine sizes the box with.
            float ratio = (float)_scaleRatio.GetValue(null);
            float spanX = extentsCm[0] + extentsCm[1];
            float spanZ = extentsCm[2] + extentsCm[3];
            float centreX = (extentsCm[0] - extentsCm[1]) * 0.5f;
            float centreZ = (extentsCm[2] - extentsCm[3]) * 0.5f;

            // The physical board's frame: +Z runs toward the XE's long end.
            var gameboard = SingletonComponent<TiltFiveManager2>.Instance?.playerOneSettings?.gameboardSettings?.currentGameBoard;
            var frame = gameboard != null ? gameboard.transform : _board.transform;

            if (!_applied) _originalLocalPosition = _cutout.localPosition;

            var parent = _cutout.parent;
            Vector3 alongLength = parent != null ? parent.InverseTransformDirection(frame.forward) : frame.forward;
            Vector3 alongWidth = parent != null ? parent.InverseTransformDirection(frame.right) : frame.right;
            _cutout.localPosition = _originalLocalPosition + (alongLength * centreZ + alongWidth * centreX) * ratio;

            // Stretch whichever of the box's own axes runs along the board's length.
            bool lengthOnZ = Mathf.Abs(Vector3.Dot(_cutout.forward, frame.forward)) >= Mathf.Abs(Vector3.Dot(_cutout.right, frame.forward));
            float height = _cutout.localScale.y;
            _cutout.localScale = lengthOnZ
                ? new Vector3(spanX * ratio, height, spanZ * ratio)
                : new Vector3(spanZ * ratio, height, spanX * ratio);

            _applied = true;
            LogState($"Tilt5XeCutout: showing the full {spanX:F0} x {spanZ:F0} cm mat ({source}), centre shifted {centreZ:F1} cm toward the long end.");
        }

        private void Revert()
        {
            if (!_applied) return;
            _cutout.localPosition = _originalLocalPosition;
            _updateScales.Invoke(null, null);
            _applied = false;
            LogState("Tilt5XeCutout: back to the engine's square view.");
        }

        private void LogState(string message)
        {
            if (message == _lastState) return;
            _lastState = message;
            Plugin.Log.LogInfo(message);
        }
    }
}
