using System.Linq;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;

namespace RpgEngineAnimatedMeshMod
{
    /// <summary>
    /// v3.1.29: in-game "Model Heights" panel (F8 by default). Lists every
    /// imported model in the scene with buttons to nudge its height. A change
    /// applies live to every copy of that model and is saved to its
    /// [HeightAdjust] config entry, so the GM never has to open the cfg file.
    ///
    /// Drawn with IMGUI. The engine only acts on world clicks while the mouse
    /// is over its full-screen uGUI InputPanel, so a transparent uGUI blocker
    /// sits under the window to keep clicks on the panel out of the scene.
    ///
    /// v3.1.30: own opaque skin and one block per model (name line, then the
    /// buttons), the default skin was unreadable over the scene and the
    /// single-row layout clipped long model names and buttons.
    /// </summary>
    internal class HeightPanel : MonoBehaviour
    {
        internal static KeyboardShortcut ToggleKey = new KeyboardShortcut(KeyCode.F8);

        private const int WindowId = 0x4D48;
        private const float WindowWidth = 520f;
        private const int ModelsBeforeScrolling = 4;
        private const float ModelBlockHeight = 96f;
        private static readonly int[] StepsCm = { -5, -1, 1, 5 };

        private bool _visible;
        private Rect _window = new Rect(40f, 140f, WindowWidth, 0f);
        private Vector2 _scroll;
        private RectTransform _blocker;

        private Texture2D _background;
        private Texture2D _blockBackground;
        private GUIStyle _windowStyle, _blockStyle, _nameStyle, _valueStyle, _noteStyle, _buttonStyle;

        internal static void Create()
        {
            var go = new GameObject("AnimatedMesh_HeightPanel");
            DontDestroyOnLoad(go);
            go.AddComponent<HeightPanel>();
        }

        private void Awake()
        {
            BuildBlocker();
        }

        private void OnDestroy()
        {
            if (_background != null) Destroy(_background);
            if (_blockBackground != null) Destroy(_blockBackground);
        }

        private void Update()
        {
            if (ToggleKey.IsDown()) _visible = !_visible;
            if (_blocker != null && _blocker.gameObject.activeSelf != _visible)
                _blocker.gameObject.SetActive(_visible);
        }

        private void OnGUI()
        {
            if (!_visible) return;
            EnsureStyles();

            // IMGUI draws at a fixed pixel size, scale it with screen height.
            float scale = Mathf.Max(1f, Screen.height / 1080f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));

            // Height 0 lets the window shrink back when models leave the scene.
            _window.height = 0f;
            _window = GUILayout.Window(WindowId, _window, DrawWindow, "Model Heights", _windowStyle, GUILayout.Width(WindowWidth));
            _window.x = Mathf.Clamp(_window.x, 0f, Mathf.Max(0f, Screen.width / scale - _window.width));
            _window.y = Mathf.Clamp(_window.y, 0f, Mathf.Max(0f, Screen.height / scale - _window.height));

            if (Event.current.type == EventType.Repaint && _blocker != null)
            {
                _blocker.anchoredPosition = new Vector2(_window.x * scale, Screen.height - (_window.y + _window.height) * scale);
                _blocker.sizeDelta = new Vector2(_window.width * scale, _window.height * scale);
            }
        }

        private void DrawWindow(int id)
        {
            var models = GroundOffset.All
                .Where(g => g != null && !string.IsNullOrEmpty(g.MeshId))
                .GroupBy(g => g.MeshId)
                .OrderBy(g => g.Key)
                .ToList();

            if (models.Count == 0)
            {
                GUILayout.Label("No imported models in this scene yet. Place one and it shows up here.", _noteStyle);
            }
            else
            {
                bool scroll = models.Count > ModelsBeforeScrolling;
                if (scroll)
                    _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(ModelsBeforeScrolling * ModelBlockHeight));

                foreach (var model in models)
                    DrawModel(model.Key, model.First(), model.Count());

                if (scroll) GUILayout.EndScrollView();
            }

            GUILayout.Space(6f);
            GUILayout.Label(ModelSync.IsServer
                ? "Use the buttons to move a model up or down. It changes every copy of that model for everyone in the session straight away and is saved for next time."
                : "Heights are set by the host (game master) and arrive here automatically. This panel just shows them.", _noteStyle);

            GUILayout.BeginHorizontal();
            GUILayout.Label($"{ToggleKey} opens and closes this panel. Drag the title to move it.", _noteStyle);
            if (GUILayout.Button("Close", _buttonStyle, GUILayout.Width(80f))) _visible = false;
            GUILayout.EndHorizontal();

            GUI.DragWindow();
        }

        private void DrawModel(string meshId, GroundOffset ground, int copies)
        {
            int cm = Mathf.RoundToInt(ground.ManualLocal * 100f);
            int autoCm = Mathf.RoundToInt(ground.AutoLocal * 100f);

            GUILayout.BeginVertical(_blockStyle);
            GUILayout.Label(copies > 1 ? $"{meshId}  ({copies} in scene)" : meshId, _nameStyle);

            // v3.1.34: only the host changes heights, players see them read-only.
            bool canEdit = ModelSync.IsServer;
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{(canEdit ? "Your" : "Host's")} height: {FormatCm(cm)}", _valueStyle, GUILayout.Width(160f));
            bool wasEnabled = GUI.enabled;
            GUI.enabled = canEdit;
            foreach (var step in StepsCm)
            {
                if (GUILayout.Button(step > 0 ? $"+{step}" : step.ToString(), _buttonStyle, GUILayout.Width(46f)))
                    Plugin.SetHeightAdjust(meshId, (cm + step) / 100f);
            }
            if (GUILayout.Button("Reset", _buttonStyle, GUILayout.Width(64f)))
                Plugin.SetHeightAdjust(meshId, 0f);
            GUI.enabled = wasEnabled;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Label(autoCm == 0
                ? "Automatic fix: none needed"
                : $"Automatic fix: {(autoCm > 0 ? "raised" : "lowered")} {Mathf.Abs(autoCm)} cm to put the feet on the ground", _noteStyle);
            GUILayout.EndVertical();
            GUILayout.Space(4f);
        }

        private static string FormatCm(int cm) => cm == 0 ? "0 cm" : $"{cm:+0;-0} cm";

        private void EnsureStyles()
        {
            if (_windowStyle != null) return;

            _background = SolidTexture(new Color(0.09f, 0.09f, 0.10f, 0.97f));
            _blockBackground = SolidTexture(new Color(0.16f, 0.16f, 0.18f, 1f));

            _windowStyle = new GUIStyle(GUI.skin.window)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(14, 14, 36, 12),
            };
            _windowStyle.normal.background = _background;
            _windowStyle.onNormal.background = _background;
            _windowStyle.normal.textColor = new Color(0.95f, 0.82f, 0.45f);
            _windowStyle.onNormal.textColor = _windowStyle.normal.textColor;

            _blockStyle = new GUIStyle(GUI.skin.box) { padding = new RectOffset(10, 10, 8, 8) };
            _blockStyle.normal.background = _blockBackground;

            _nameStyle = new GUIStyle(GUI.skin.label) { fontSize = 17, fontStyle = FontStyle.Bold, wordWrap = true };
            _nameStyle.normal.textColor = Color.white;

            _valueStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, alignment = TextAnchor.MiddleLeft, fixedHeight = 30f };
            _valueStyle.normal.textColor = new Color(0.95f, 0.82f, 0.45f);

            _noteStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true };
            _noteStyle.normal.textColor = new Color(0.75f, 0.75f, 0.78f);

            _buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 16, fontStyle = FontStyle.Bold, fixedHeight = 30f };
        }

        private static Texture2D SolidTexture(Color colour)
        {
            var texture = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            texture.SetPixel(0, 0, colour);
            texture.Apply();
            return texture;
        }

        private void BuildBlocker()
        {
            var canvasGo = new GameObject("AnimatedMesh_HeightPanelBlocker");
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = short.MaxValue;
            canvasGo.AddComponent<GraphicRaycaster>();

            var blockerGo = new GameObject("Blocker", typeof(RectTransform));
            blockerGo.transform.SetParent(canvasGo.transform, false);
            var image = blockerGo.AddComponent<Image>();
            image.color = new Color(0f, 0f, 0f, 0f);
            image.raycastTarget = true;

            _blocker = (RectTransform)blockerGo.transform;
            _blocker.anchorMin = Vector2.zero;
            _blocker.anchorMax = Vector2.zero;
            _blocker.pivot = Vector2.zero;
            blockerGo.SetActive(false);
        }
    }
}
