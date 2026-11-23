using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;
using VRC.Udon.Editor;

namespace BetterUdon.Editor
{
    /// <summary>
    /// Inspector for BetterUdon scripts.
    ///
    /// The layout keeps three things apart: the fields the script exposes (Unity's default inspector),
    /// the BetterUdon tooling, and the generated program. The tooling and the program live in their own
    /// boxes below the fields, so pressing Recompile or opening the assembly is never confused with
    /// editing a public variable.
    /// </summary>
    [CustomEditor(typeof(BetterUdonBehaviour), true)]
    [CanEditMultipleObjects]
    public sealed class BetterUdonBehaviourEditor : UnityEditor.Editor
    {
        private bool _showAssembly;
        private bool _showDisassembly;
        private Vector2 _assemblyScroll;
        private Vector2 _disassemblyScroll;

        private void OnEnable()
        {
            foreach (UnityEngine.Object targetObject in targets)
            {
                if (!(targetObject is BetterUdonBehaviour behaviour))
                    continue;

                try
                {
                    BetterUdonEditorUtility.SetupBackingBehaviour(behaviour, false);
                    BetterUdonEditorUtility.SyncFieldsToUdon(behaviour);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[BetterUdon] Failed to set up '{behaviour.name}': {e.Message}");
                }
            }
        }

        public override void OnInspectorGUI()
        {
            // 1. Everything the script exposes. This is the only part that belongs to the user.
            bool changed = DrawDefaultInspector();

            BetterUdonBehaviour behaviour = (BetterUdonBehaviour)target;
            UdonBehaviour backing = behaviour != null ? behaviour.udonBehaviour : null;
            BetterUdonProgramAsset programAsset = backing != null ? backing.programSource as BetterUdonProgramAsset : null;

            EditorGUILayout.Space(8f);

            // 2. BetterUdon tooling.
            DrawToolingBox(behaviour, programAsset);

            // 3. The generated program, kept apart from both the fields and the tools.
            if (programAsset != null)
            {
                EditorGUILayout.Space(4f);
                DrawProgramBox(behaviour, programAsset);
            }

            if (changed)
            {
                BetterUdonEditorUtility.SyncFieldsToUdon(behaviour);
                EditorUtility.SetDirty(behaviour);
            }
        }

        private void DrawToolingBox(BetterUdonBehaviour behaviour, BetterUdonProgramAsset programAsset)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    BetterUdonInspectorGUI.SectionTitle("BetterUdon");

                    if (targets.Length > 1)
                    {
                        GUILayout.FlexibleSpace();
                        EditorGUILayout.LabelField($"editing '{behaviour.name}'", EditorStyles.miniLabel, GUILayout.Width(150f));
                    }
                }

                if (programAsset == null)
                {
                    EditorGUILayout.HelpBox(
                        "This behaviour has no generated Udon program yet. Recompile to create one.",
                        MessageType.Info);

                    if (GUILayout.Button("Recompile", GUILayout.Height(22f)))
                    {
                        BetterUdonEditorUtility.SetupBackingBehaviour(behaviour);
                        BetterUdonEditorUtility.SyncFieldsToUdon(behaviour);
                    }

                    return;
                }

                EditorGUILayout.LabelField(
                    "Source",
                    programAsset.sourceScript != null ? programAsset.sourceScript.name : "(not assigned)");

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Recompile", GUILayout.Height(22f)))
                    {
                        programAsset.RefreshProgram();
                        BetterUdonEditorUtility.SetupBackingBehaviour(behaviour);
                        BetterUdonEditorUtility.SyncFieldsToUdon(behaviour);
                    }

                    if (GUILayout.Button("Copy Assembly", GUILayout.Height(22f)))
                        EditorGUIUtility.systemCopyBuffer = programAsset.GetAssembly();

                    GUILayout.FlexibleSpace();

                    if (GUILayout.Button("Program Asset", EditorStyles.miniButton, GUILayout.Width(110f), GUILayout.Height(22f)))
                        Selection.activeObject = programAsset;
                }

                if (programAsset.LastDiagnostics.Count > 0)
                {
                    EditorGUILayout.HelpBox(
                        "Compile errors:\n" + string.Join("\n", programAsset.LastDiagnostics.Select(d => d.ToString())),
                        MessageType.Error);
                }
                else if (!string.IsNullOrEmpty(programAsset.AssemblyError))
                {
                    EditorGUILayout.HelpBox("The assembly could not be built:\n" + programAsset.AssemblyError, MessageType.Error);
                }
            }
        }

        private void DrawProgramBox(BetterUdonBehaviour behaviour, BetterUdonProgramAsset programAsset)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                BetterUdonInspectorGUI.SectionTitle("Program");

                if (BetterUdonOptimizationGUI.Draw(programAsset) && behaviour != null)
                {
                    // Recompile immediately so the complexity bar shows the effect of the toggle.
                    programAsset.RefreshProgram();
                    BetterUdonEditorUtility.SetupBackingBehaviour(behaviour, false);
                    BetterUdonEditorUtility.SyncFieldsToUdon(behaviour);
                }

                BetterUdonComplexity.Draw(programAsset);

                _showAssembly = EditorGUILayout.Foldout(_showAssembly, "Assembly", true);
                if (_showAssembly)
                    BetterUdonInspectorGUI.CodeBlock(programAsset.GetAssembly(), ref _assemblyScroll);

                _showDisassembly = EditorGUILayout.Foldout(_showDisassembly, "Disassembly", true);
                if (_showDisassembly)
                {
                    string disassembly;

                    try
                    {
                        IUdonProgram program = programAsset.GetProgram();
                        disassembly = program == null
                            ? string.Empty
                            : string.Join("\n", UdonEditorManager.Instance.DisassembleProgram(program));
                    }
                    catch (Exception e)
                    {
                        disassembly = "Disassembly failed: " + e.Message;
                    }

                    BetterUdonInspectorGUI.CodeBlock(disassembly, ref _disassemblyScroll);
                }
            }
        }
    }
}
