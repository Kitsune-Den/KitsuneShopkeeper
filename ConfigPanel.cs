using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace KitsunePricer
{
    /// <summary>
    /// In-game settings window, toggled by a hotkey (F8 by default).
    ///
    /// Built from the config file itself rather than a hand-written form, so every
    /// setting added later shows up here with no extra work. Writing BoxedValue
    /// raises SettingChanged and saves the .cfg, exactly as editing the file and
    /// reloading would.
    ///
    /// Drawn with IMGUI from Runner.OnGUI. In a shop the panel enters the game's
    /// own UI mode, the same call the game's confirm screens make, so the cursor
    /// is freed and mouse-look stops while it is open.
    /// </summary>
    internal static class ConfigPanel
    {
        private const int WindowId = 0x4B505243; // "KPRC"

        private static bool _open;
        private static Rect _rect = new Rect(0, 0, 520, 600);
        private static bool _centered;
        private static Vector2 _scroll;
        private static readonly Dictionary<ConfigDefinition, string> _textBuffers = new Dictionary<ConfigDefinition, string>();

        private static InteractionPlayerController _controller;
        private static bool _enteredUIMode;

        public static bool IsOpen => _open;

        private static GUIStyle _header, _tip;
        private static GUIStyle Header => _header ?? (_header = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold });
        private static GUIStyle Tip => _tip ?? (_tip = new GUIStyle(GUI.skin.box) { wordWrap = true, alignment = TextAnchor.UpperLeft });

        public static void Toggle()
        {
            if (_open) Close();
            else Open();
        }

        private static void Open()
        {
            _open = true;
            _textBuffers.Clear();

            // FindObjectOfType, never CSingleton: there is no controller on the
            // title screen, and the singleton getter would manufacture one.
            try
            {
                _controller = UnityEngine.Object.FindObjectOfType<InteractionPlayerController>();
                if (_controller != null && !_controller.IsInUIMode())
                {
                    _controller.EnterUIMode();
                    _enteredUIMode = true;
                }
            }
            catch (Exception ex)
            {
                Plugin.DiagOnce("panel-enter-ui", "config panel: EnterUIMode failed: " + ex.Message);
            }
        }

        private static void Close()
        {
            _open = false;

            // Only hand back control we took. If another game screen was already
            // open, leaving UI mode would yank the cursor away from it.
            try
            {
                if (_enteredUIMode && _controller != null) _controller.ExitUIMode();
            }
            catch (Exception ex)
            {
                Plugin.DiagOnce("panel-exit-ui", "config panel: ExitUIMode failed: " + ex.Message);
            }

            _enteredUIMode = false;
            _controller = null;
        }

        public static void Draw()
        {
            if (!_open || Plugin.Cfg == null) return;

            if (!_centered)
            {
                _rect.x = (Screen.width - _rect.width) / 2f;
                _rect.y = (Screen.height - _rect.height) / 2f;
                _centered = true;
            }

            _rect = GUILayout.Window(WindowId, _rect, DrawWindow, $"Kitsune Shopkeeper v{Plugin.Version}");
        }

        private static void DrawWindow(int id)
        {
            _scroll = GUILayout.BeginScrollView(_scroll);

            var sections = Plugin.Cfg.Keys
                .GroupBy(d => d.Section)
                .OrderBy(g => g.Key);

            foreach (var section in sections)
            {
                GUILayout.Space(6);
                GUILayout.Label(section.Key, Header);

                foreach (var def in section)
                {
                    try { DrawEntry(Plugin.Cfg[def]); }
                    catch (Exception ex)
                    {
                        Plugin.DiagOnce("panel-entry-" + def.Key, $"config panel: {def.Key} failed to draw: {ex.Message}");
                    }
                }
            }

            GUILayout.EndScrollView();

            // Descriptions from the .cfg, for whatever the mouse is over. Fixed
            // height so the window does not jump around as the hover changes.
            GUILayout.Label(GUI.tooltip ?? "", Tip, GUILayout.Height(58));

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Saved automatically. {Plugin.KeyConfigPanel.Value} to close.");
            if (GUILayout.Button("Close", GUILayout.Width(80))) Close();
            GUILayout.EndHorizontal();

            GUI.DragWindow();
        }

        private static void DrawEntry(ConfigEntryBase entry)
        {
            Type type = entry.SettingType;
            string name = entry.Definition.Key;
            string tip = entry.Description?.Description ?? "";

            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent(name, tip), GUILayout.Width(200));

            if (type == typeof(bool))
            {
                bool value = (bool)entry.BoxedValue;
                bool next = GUILayout.Toggle(value, value ? "On" : "Off");
                if (next != value) entry.BoxedValue = next;
            }
            else if (type.IsEnum)
            {
                if (GUILayout.Button(entry.BoxedValue.ToString()))
                {
                    var values = Enum.GetValues(type);
                    int index = Array.IndexOf(values, entry.BoxedValue);
                    entry.BoxedValue = values.GetValue((index + 1) % values.Length);
                }
            }
            else if (type == typeof(float) || type == typeof(int))
            {
                DrawNumber(entry, type);
            }
            else
            {
                // Hotkeys and anything exotic: show it, edit it in the .cfg.
                GUILayout.Label(entry.BoxedValue?.ToString() ?? "");
            }

            GUILayout.EndHorizontal();
        }

        private static void DrawNumber(ConfigEntryBase entry, Type type)
        {
            bool isInt = type == typeof(int);
            float value = isInt ? (int)entry.BoxedValue : (float)entry.BoxedValue;

            // Ranged settings get a slider: no typing, no invalid input.
            if (entry.Description?.AcceptableValues is AcceptableValueRange<float> fr)
            {
                float next = Mathf.Round(GUILayout.HorizontalSlider(value, fr.MinValue, fr.MaxValue));
                GUILayout.Label(next.ToString("0", CultureInfo.InvariantCulture), GUILayout.Width(40));
                if (!Mathf.Approximately(next, value)) entry.BoxedValue = next;
                return;
            }

            if (entry.Description?.AcceptableValues is AcceptableValueRange<int> ir)
            {
                int current = (int)entry.BoxedValue;
                int next = Mathf.RoundToInt(GUILayout.HorizontalSlider(current, ir.MinValue, ir.MaxValue));
                GUILayout.Label(next.ToString(CultureInfo.InvariantCulture), GUILayout.Width(40));
                if (next != current) entry.BoxedValue = next;
                return;
            }

            // Unranged: a text box that commits only when it parses, so a
            // half-typed "-" or "1." never gets written to the config.
            var def = entry.Definition;
            if (!_textBuffers.TryGetValue(def, out string text))
                text = isInt ? ((int)entry.BoxedValue).ToString(CultureInfo.InvariantCulture)
                             : ((float)entry.BoxedValue).ToString("0.##", CultureInfo.InvariantCulture);

            string edited = GUILayout.TextField(text, GUILayout.Width(90));
            _textBuffers[def] = edited;

            if (edited == text) return;

            if (isInt && int.TryParse(edited, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv))
                entry.BoxedValue = iv;
            else if (!isInt && float.TryParse(edited, NumberStyles.Float, CultureInfo.InvariantCulture, out float fv))
                entry.BoxedValue = fv;
        }

        // ------------------------------------------------------- draw ownership

        private static Runner _drawer;
        private static int _drawerFrame = -10;

        /// <summary>
        /// Up to two Runners can be alive at once (our own and the copy on the
        /// light manager). Only one may draw, or the window renders twice and each
        /// click lands on both copies.
        /// </summary>
        public static bool ClaimDraw(Runner runner)
        {
            int frame = Time.frameCount;
            if (_drawer != null && _drawer != runner && _drawer.isActiveAndEnabled && frame - _drawerFrame <= 1)
                return false;

            _drawer = runner;
            _drawerFrame = frame;
            return true;
        }
    }
}
