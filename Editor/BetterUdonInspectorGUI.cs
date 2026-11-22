using UnityEditor;
using UnityEngine;

namespace BetterUdon.Editor
{
    /// <summary>
    /// Drawing helpers shared by the behaviour and program inspectors so that the generated artifacts
    /// (assembly, disassembly) look the same everywhere and are visually distinct from the fields the
    /// script exposes.
    /// </summary>
    internal static class BetterUdonInspectorGUI
    {
        private static GUIStyle _codeStyle;

        /// <summary>
        /// Read-only, scrollable, monospaced block for assembly and disassembly. Horizontal scrolling is
        /// not available in an IMGUI text area, so long lines are shown in full and the block is wide.
        /// </summary>
        public static void CodeBlock(string text, ref Vector2 scroll, float maxHeight = 240f)
        {
            GUIStyle style = GetCodeStyle();

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MaxHeight(maxHeight));

                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.TextArea(
                        string.IsNullOrEmpty(text) ? string.Empty : text,
                        style,
                        GUILayout.ExpandHeight(true));
                }

                EditorGUILayout.EndScrollView();
            }
        }

        /// <summary>Bold heading used at the top of a boxed section.</summary>
        public static void SectionTitle(string title)
        {
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        }

        private static GUIStyle GetCodeStyle()
        {
            if (_codeStyle != null)
                return _codeStyle;

            GUIStyle style = new GUIStyle(EditorStyles.textArea)
            {
                wordWrap = false,
                richText = false,
                fontSize = 11,
                padding = new RectOffset(6, 6, 6, 6),
            };

            try
            {
                Font font = Font.CreateDynamicFontFromOSFont(
                    new[] { "Consolas", "DejaVu Sans Mono", "Liberation Mono", "Courier New" }, 11);

                if (font != null)
                    style.font = font;
            }
            catch
            {
                // Not every platform has a monospaced font; the default editor font is fine.
            }

            _codeStyle = style;
            return _codeStyle;
        }
    }
}
