using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;

namespace BetterUdon.Editor
{
    /// <summary>
    /// Development helper used while building the compiler. It compiles every BetterUdon behaviour in the
    /// project and writes a full report (generated assembly, diagnostics, entry points and heap values)
    /// to <c>Library/BetterUdonReport.txt</c>. It can also run a short play mode smoke test.
    /// </summary>
    internal static class BetterUdonSelfTest
    {
        public const string ReportPath = "Library/BetterUdonReport.txt";
        public const string PlayReportPath = "Library/BetterUdonPlayReport.txt";

        private const string AutoRunFlag = "Library/BetterUdonAutoRun.flag";
        private const string PlayTestFlag = "Library/BetterUdonPlayTest.flag";
        private const string SetupSceneFlag = "Library/BetterUdonSetupScene.flag";

        private static readonly List<string> _playLogs = new List<string>();
        private static bool _playTestRunning;
        private static bool _sentCustomEvents;
        private static DateTime _playTestStart;

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            EditorApplication.update += PollFlags;
            EditorApplication.update += AutoStopPlayMode;
        }

        private const string PlayEndKey = "BetterUdonPlayEnd";
        private const string PlayTimerFile = "Library/BetterUdonPlayTimer.txt";

        private static void AutoStopPlayMode()
        {
            if (!EditorApplication.isPlaying)
                return;

            if (!File.Exists(PlayTimerFile))
                return;

            string text = File.ReadAllText(PlayTimerFile).Trim();
            string[] parts = text.Split('|');
            if (parts.Length != 2 || !DateTime.TryParse(parts[0], out DateTime start) || !DateTime.TryParse(parts[1], out DateTime end))
                return;

            double elapsed = (DateTime.UtcNow - start).TotalSeconds;

            if (elapsed > 5 && parts[1] != "sent")
            {
                // nothing to do here, the flag file is rewritten below
            }

            if (elapsed > 5)
            {
                string sentMarker = PlayTimerFile + ".sent";
                if (!File.Exists(sentMarker))
                {
                    File.WriteAllText(sentMarker, "1");
                    WritePlayReport("sending custom events");

                    foreach (UdonBehaviour behaviour in UnityEngine.Object.FindObjectsOfType<UdonBehaviour>())
                    {
                        behaviour.SendCustomEvent("PrintStats");
                        behaviour.SendCustomEvent("Interact");
                    }
                }
            }

            if (DateTime.UtcNow >= end)
            {
                File.Delete(PlayTimerFile);
                WritePlayReport("auto stop");
                EditorApplication.isPlaying = false;
            }
        }

        private static void PollFlags()
        {
            if (File.Exists(PlayTestFlag))
            {
                File.Delete(PlayTestFlag);
                StartPlayTest();
                return;
            }

            if (File.Exists(AutoRunFlag))
            {
                File.Delete(AutoRunFlag);
                TryRun(File.Exists(SetupSceneFlag));
            }
        }

        [MenuItem("Tools/BetterUdon/Run Diagnostics")]
        public static void RunDiagnostics()
        {
            TryRun(false);
        }

        [MenuItem("Tools/BetterUdon/Setup Test Scene")]
        public static void SetupTestScene()
        {
            TryRun(true);
        }

        /// <summary>
        /// Makes sure the integration test objects exist in the active scene with a compiled program.
        /// </summary>
        public static string SetupTestObjects()
        {
            StringBuilder report = new StringBuilder();
            SetupScene(report);
            return report.ToString();
        }

        #region Diagnostics

        private static void TryRun(bool setupScene)
        {
            StringBuilder report = new StringBuilder();
            report.AppendLine("BetterUdon diagnostics " + DateTime.Now.ToString("u"));

            try
            {
                RunCompileChecks(report);
                if (setupScene)
                    SetupScene(report);
            }
            catch (Exception e)
            {
                report.AppendLine("EXCEPTION: " + e);
            }

            File.WriteAllText(ReportPath, report.ToString());
            Debug.Log("[BetterUdon] Diagnostics written to " + ReportPath);
        }

        private static IEnumerable<string> EnumerateBehaviourSources()
        {
            string testFolder = Path.Combine(Application.dataPath, "BetterUdonTests");
            if (!Directory.Exists(testFolder))
                return Enumerable.Empty<string>();

            return Directory.EnumerateFiles(testFolder, "*.cs", SearchOption.AllDirectories);
        }

        private static void RunCompileChecks(StringBuilder report)
        {
            foreach (string path in EnumerateBehaviourSources())
            {
                report.AppendLine();
                report.AppendLine("=======================================================");
                report.AppendLine("SOURCE: " + path);

                List<BetterUdonCompileResult> results = BetterUdonCompiler.CompileFile(path);

                foreach (BetterUdonCompileResult result in results)
                {
                    report.AppendLine("  RESULT: " + result.ClassName + " success=" + result.Success);

                    foreach (CompileDiagnostic diagnostic in result.Diagnostics)
                        report.AppendLine("    DIAG " + diagnostic);

                    if (result.Assembly != null)
                        report.AppendLine(result.Assembly);

                    report.AppendLine("  Entry points: " + string.Join(", ", result.EntryPoints));
                    report.AppendLine("  Heap initial values: " + result.HeapInitialValues.Count);

                    List<BetterUdonCompileResult> second = BetterUdonCompiler.CompileFile(path);
                    report.AppendLine("  SECOND COMPILE: " + (second.Count > 0 ? second[0].Success.ToString() : "none")
                        + (second.Count > 0 && second[0].Diagnostics.Count > 0 ? " -> " + second[0].Diagnostics[0] : ""));

                    BetterUdonProgramAsset asset = ScriptableObject.CreateInstance<BetterUdonProgramAsset>();
                    string relativePath = "Assets" + path.Substring(Application.dataPath.Length).Replace('\\', '/');
                    asset.SetSource(AssetDatabase.LoadAssetAtPath<MonoScript>(relativePath), result.ClassName);
                    asset.RefreshProgram();
                    report.AppendLine("  ASSEMBLER ERROR: " + (asset.AssemblyError ?? "<none>"));

                    IUdonProgram program = asset.GetProgram();
                    if (program != null)
                    {
                        report.AppendLine("  Byte code length: " + (program.ByteCode?.Length ?? 0));
                        report.AppendLine("  Exported symbols: " + string.Join(", ", program.SymbolTable.GetExportedSymbols()));
                        report.AppendLine("  Entry points (assembled): " + string.Join(", ", program.EntryPoints.GetExportedSymbols()));
                    }

                    UnityEngine.Object.DestroyImmediate(asset);
                }
            }
        }

        #endregion

        #region Scene

        private static void SetupScene(StringBuilder report)
        {
            Scene scene = SceneManager.GetActiveScene();
            report.AppendLine();
            report.AppendLine("Active scene: " + scene.path + " loaded=" + scene.isLoaded);

            foreach ((string typeName, string objectName) in new[]
                     {
                         ("BetterUdonTests.StructTestBehaviour, Assembly-CSharp", "BetterUdonTestObject"),
                         ("BetterUdonTests.LanguageFeatureTests, Assembly-CSharp", "BetterUdonLangTestObject"),
                     })
            {
                Type behaviourType = Type.GetType(typeName);

                if (behaviourType == null)
                {
                    report.AppendLine("Test behaviour type not found: " + typeName);
                    continue;
                }

                GameObject root = scene.GetRootGameObjects().FirstOrDefault(g => g.name == objectName);

                if (root == null)
                {
                    root = new GameObject(objectName);
                    root.AddComponent(behaviourType);
                    report.AppendLine("Created " + objectName + " in the scene.");
                }
                else if (root.GetComponent(behaviourType) == null)
                {
                    root.AddComponent(behaviourType);
                    report.AppendLine("Added missing component to " + objectName + ".");
                }

                foreach (BetterUdonBehaviour behaviour in root.GetComponents<BetterUdonBehaviour>())
                {
                    UdonBehaviour backing = BetterUdonEditorUtility.SetupBackingBehaviour(behaviour, true);
                    BetterUdonEditorUtility.SyncFieldsToUdon(behaviour);
                    report.AppendLine($"  Behaviour {behaviour.GetType().Name}: backing={(backing != null)}");
                    report.AppendLine($"  Public variables: {string.Join(", ", backing != null ? backing.publicVariables.VariableSymbols.ToArray() : new string[0])}");
                }
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            report.AppendLine("Scene saved.");
        }

        #endregion

        #region Play mode smoke test

        private static void StartPlayTest()
        {
            _playLogs.Clear();
            _sentCustomEvents = false;
            WritePlayReport("starting");
            Application.logMessageReceived += OnPlayLog;
            EditorApplication.update += OnPlayTestUpdate;
            _playTestRunning = true;
            _playTestStart = DateTime.UtcNow;

            try
            {
                EditorSceneManager.SaveOpenScenes();
                WritePlayReport("saved scenes");

                DateTime now = DateTime.UtcNow;
                File.WriteAllText(PlayTimerFile, now.ToString("O") + "|" + now.AddSeconds(14).ToString("O"));
                string sentMarker = PlayTimerFile + ".sent";
                if (File.Exists(sentMarker))
                    File.Delete(sentMarker);

                EditorApplication.isPlaying = true;
            }
            catch (Exception e)
            {
                WritePlayReport("failed to enter play mode: " + e);
            }
        }

        private static void WritePlayReport(string note)
        {
            File.AppendAllText(PlayReportPath, note + " (isPlaying=" + EditorApplication.isPlaying + ")\n");
        }

        private static void OnPlayLog(string condition, string stackTrace, LogType type)
        {
            _playLogs.Add($"[{type}] {condition}");
        }

        private static void OnPlayTestUpdate()
        {
            if (!_playTestRunning)
                return;

            double elapsed = (DateTime.UtcNow - _playTestStart).TotalSeconds;

            if (EditorApplication.isPlaying && !_sentCustomEvents && elapsed > 6)
            {
                _sentCustomEvents = true;

                foreach (UdonBehaviour behaviour in UnityEngine.Object.FindObjectsOfType<UdonBehaviour>())
                {
                    behaviour.SendCustomEvent("Interact");
                    behaviour.SendCustomEvent("Interact");
                    behaviour.SendCustomEvent("PrintStats");
                }
            }

            if (EditorApplication.isPlaying && elapsed > 12)
            {
                WritePlayReport("stopping play mode");
                EditorApplication.isPlaying = false;
                return;
            }

            if (!EditorApplication.isPlaying && elapsed > 4)
            {
                _playTestRunning = false;
                EditorApplication.update -= OnPlayTestUpdate;
                Application.logMessageReceived -= OnPlayLog;

                File.AppendAllText(PlayReportPath, "=== LOGS ===\n" + string.Join("\n", _playLogs));
            }
        }

        #endregion
    }
}
