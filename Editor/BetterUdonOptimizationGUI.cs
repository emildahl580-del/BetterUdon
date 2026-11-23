using UnityEditor;
using UnityEngine;

namespace BetterUdon.Editor
{
    /// <summary>
    /// Compiler optimization toggles shared by the behaviour inspector and the program asset inspector.
    /// </summary>
    internal static class BetterUdonOptimizationGUI
    {
        public static bool Draw(BetterUdonProgramAsset asset)
        {
            if (asset == null)
                return false;

            UdonOptimizationFlags flags = asset.OptimizationFlags;
            bool changed = false;

            BetterUdonInspectorGUI.SectionTitle("Optimization");

            changed |= Toggle(ref flags, UdonOptimizationFlags.Peephole,
                "Remove redundant instructions",
                "Drops NOPs, self copies, push/pop pairs and jumps to the next instruction.");

            changed |= Toggle(ref flags, UdonOptimizationFlags.DeadCode,
                "Remove unreachable code",
                "Drops code after an unconditional jump or halt and removes unused compiler temporaries.");

            changed |= Toggle(ref flags, UdonOptimizationFlags.CopyPropagation,
                "Propagate copies",
                "Reads values directly from constants, fields and parameters instead of copying them into temporaries first.");

            changed |= Toggle(ref flags, UdonOptimizationFlags.SlotReuse,
                "Reuse heap slots",
                "Shares compiler temporaries whose lifetimes do not overlap. Exported and synced variables are never shared.");

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Recommended", EditorStyles.miniButtonLeft, GUILayout.Width(100f)))
                {
                    flags = UdonOptimizationFlags.Recommended;
                    changed = true;
                }

                if (GUILayout.Button("None", EditorStyles.miniButtonRight, GUILayout.Width(60f)))
                {
                    flags = UdonOptimizationFlags.None;
                    changed = true;
                }
            }

            if (changed)
            {
                Undo.RecordObject(asset, "Change BetterUdon optimization");
                asset.OptimizationFlags = flags;
                EditorUtility.SetDirty(asset);
            }

            return changed;
        }

        private static bool Toggle(
            ref UdonOptimizationFlags flags,
            UdonOptimizationFlags flag,
            string label,
            string tooltip)
        {
            bool enabled = (flags & flag) != 0;
            bool value = EditorGUILayout.ToggleLeft(new GUIContent(label, tooltip), enabled);

            if (value == enabled)
                return false;

            flags = value ? flags | flag : flags & ~flag;
            return true;
        }
    }
}
