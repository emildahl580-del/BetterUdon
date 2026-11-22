using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;
using VRC.Udon;
using VRC.Udon.Editor;

namespace BetterUdon.Editor
{
    /// <summary>
    /// Keeps generated programs in sync with their C# source and swaps BetterUdonBehaviour components for
    /// the backing UdonBehaviour when a world is built.
    /// </summary>
    [InitializeOnLoad]
    internal static class BetterUdonEditorManager
    {
        static BetterUdonEditorManager()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                BetterUdonEditorUtility.CompileAll();
                BetterUdonEditorUtility.SyncAll(true);
            }
        }

        [PostProcessScene]
        private static void OnPostProcessScene()
        {
            SceneSetup();
        }

        private static void SceneSetup()
        {
            BetterUdonBehaviour[] behaviours = Resources.FindObjectsOfTypeAll<BetterUdonBehaviour>();
            List<BetterUdonBehaviour> sceneBehaviours = new List<BetterUdonBehaviour>();

            foreach (BetterUdonBehaviour behaviour in behaviours)
            {
                if (behaviour == null || EditorUtility.IsPersistent(behaviour) || !behaviour.gameObject.scene.IsValid())
                    continue;

                sceneBehaviours.Add(behaviour);
            }

            foreach (BetterUdonBehaviour behaviour in sceneBehaviours)
            {
                UdonBehaviour backing = BetterUdonEditorUtility.SetupBackingBehaviour(behaviour, BuildPipeline.isBuildingPlayer);
                if (backing == null)
                    continue;

                BetterUdonEditorUtility.SyncFieldsToUdon(behaviour);

                // The component scripts are not part of a built world, so the BetterUdonBehaviour
                // component is removed and only the UdonBehaviour remains.
                if (BuildPipeline.isBuildingPlayer)
                    UnityEngine.Object.DestroyImmediate(behaviour);
            }
        }

        [MenuItem("Tools/BetterUdon/Compile All Programs")]
        private static void CompileAllMenu()
        {
            BetterUdonEditorUtility.CompileAll();
            BetterUdonEditorUtility.SyncAll(true);
            Debug.Log("[BetterUdon] Compiled all programs.");
        }

        [MenuItem("Tools/BetterUdon/Setup Scene Behaviours")]
        private static void SetupSceneMenu()
        {
            BetterUdonEditorUtility.SyncAll(true);
            Debug.Log("[BetterUdon] Set up all scene behaviours.");
        }

        [MenuItem("Tools/BetterUdon/Dump Extern Registry")]
        private static void DumpExterns()
        {
            try
            {
                UdonEditorManager.Instance.GetNodeRegistries();
                VRC.Udon.EditorBindings.UdonEditorInterface editorInterface = new VRC.Udon.EditorBindings.UdonEditorInterface();
                System.Text.StringBuilder builder = new System.Text.StringBuilder();

                foreach (VRC.Udon.Graph.UdonNodeDefinition definition in editorInterface.GetNodeDefinitions())
                    builder.AppendLine(definition.fullName);

                string path = System.IO.Path.Combine(Application.dataPath, "../Library/BetterUdonExterns.txt");
                System.IO.File.WriteAllText(path, builder.ToString());
                Debug.Log($"[BetterUdon] Wrote extern list to {path}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[BetterUdon] Failed to dump externs: {e}");
            }
        }
    }
}
