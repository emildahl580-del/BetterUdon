using System.Linq;
using UnityEditor;
using UnityEngine;

namespace BetterUdon.Editor
{
    /// <summary>
    /// BetterUdon's own complexity budgets and the inspector bar that visualises them.
    ///
    /// These are not hard Udon limits. Udon heap addresses are 32-bit and UdonSharp has no matching
    /// slot or generic-instance limit. The budgets exist because BetterUdon inlines calls and
    /// monomorphises generics, so large programs should be visible before they become a runtime or
    /// upload problem.
    /// </summary>
    internal static class BetterUdonComplexity
    {
        public const int SlotBudget = 20000;
        public const int GenericBudget = 256;
        public const int InstructionWordBudget = 50000;

        private static GUIStyle _barLabelStyle;

        public static float Ratio(int slots, int genericInstantiations, int instructionWords)
        {
            float slotRatio = slots / (float)SlotBudget;
            float genericRatio = genericInstantiations / (float)GenericBudget;
            float instructionRatio = instructionWords / (float)InstructionWordBudget;
            return Mathf.Max(slotRatio, Mathf.Max(genericRatio, instructionRatio));
        }

        public static Color ColorFor(float ratio)
        {
            Color green = new Color(0.22f, 0.72f, 0.30f);
            Color yellow = new Color(0.95f, 0.82f, 0.18f);
            Color orange = new Color(0.95f, 0.50f, 0.12f);
            Color red = new Color(0.88f, 0.18f, 0.18f);

            ratio = Mathf.Clamp01(ratio);

            if (ratio < 0.5f)
                return Color.Lerp(green, yellow, ratio / 0.5f);

            if (ratio < 0.8f)
                return Color.Lerp(yellow, orange, (ratio - 0.5f) / 0.3f);

            return Color.Lerp(orange, red, (ratio - 0.8f) / 0.2f);
        }

        public static void Draw(BetterUdonProgramAsset asset)
        {
            if (asset == null)
                return;

            int slots = asset.SlotCount;
            int generics = asset.GenericInstantiationCount;
            int words = asset.InstructionWordCount;
            float ratio = Ratio(slots, generics, words);

            Rect rect = EditorGUILayout.GetControlRect(false, 18f);
            EditorGUI.DrawRect(rect, new Color(0.12f, 0.12f, 0.12f, 0.65f));

            Rect fill = new Rect(rect.x, rect.y, rect.width * Mathf.Clamp01(ratio), rect.height);
            EditorGUI.DrawRect(fill, ColorFor(ratio));

            GUI.Label(rect,
                $"Complexity {Mathf.RoundToInt(ratio * 100f)}%",
                GetBarLabelStyle());

            EditorGUILayout.LabelField(
                $"{slots:N0} / {SlotBudget:N0} slots   ·   {words:N0} / {InstructionWordBudget:N0} instruction words   ·   " +
                $"{generics:N0} / {GenericBudget:N0} generic instantiations",
                EditorStyles.miniLabel);

            if (ratio >= 1f)
            {
                EditorGUILayout.HelpBox(
                    "This program is over BetterUdon's complexity budget. Udon has no matching hard limit, " +
                    "but large generated programs are expensive to serialise and run. Reduce inlining or split the behaviour.",
                    MessageType.Warning);
            }
        }

        private static GUIStyle GetBarLabelStyle()
        {
            if (_barLabelStyle != null)
                return _barLabelStyle;

            _barLabelStyle = new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(6, 4, 0, 0),
                normal = { textColor = Color.white },
            };

            return _barLabelStyle;
        }

        public static int CountSymbols(BetterUdonProgramAsset asset)
        {
            if (asset == null)
                return 0;

            try
            {
                return asset.ProgramSymbolCount;
            }
            catch
            {
                return 0;
            }
        }
    }
}
