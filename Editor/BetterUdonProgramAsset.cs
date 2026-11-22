using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.Udon;
using VRC.Udon.Common;
using VRC.Udon.Common.Interfaces;
using VRC.Udon.Editor;
using VRC.Udon.Editor.ProgramSources;

namespace BetterUdon.Editor
{
    /// <summary>
    /// The Udon program asset generated from a <see cref="BetterUdonBehaviour"/> script.
    /// Holds the generated Udon assembly and the compiled program.
    /// </summary>
    [CreateAssetMenu(menuName = "BetterUdon/Better Udon Program", fileName = "New Better Udon Program")]
    public sealed class BetterUdonProgramAsset : UdonAssemblyProgramAsset
    {
        [HideInInspector]
        public MonoScript sourceScript;

        [SerializeField, HideInInspector]
        private string className;

        [SerializeField, HideInInspector]
        private List<BetterUdonStructTypeInfo> structTypes = new List<BetterUdonStructTypeInfo>();

        [SerializeField, HideInInspector]
        private List<string> exportedFieldNames = new List<string>();

        [SerializeField, HideInInspector]
        private List<string> exportedFieldTypes = new List<string>();

        [SerializeField, HideInInspector]
        private bool hasSyncedVariables;

        [SerializeField, HideInInspector]
        private bool syncNeedsContinuous;

        [SerializeField, HideInInspector]
        private int lastSlotCount;

        [SerializeField, HideInInspector]
        private int lastGenericInstantiationCount;

        // 0 Any, 1 None, 2 NoVariableSync, 3 Continuous, 4 Manual (BetterUdonBehaviourSyncMode).
        [SerializeField, HideInInspector]
        private int syncModeOverride;

        [NonSerialized] private List<CompileDiagnostic> _lastDiagnostics = new List<CompileDiagnostic>();

        [NonSerialized] private VRC.SDK3.UdonNetworkCalling.NetworkCallingEntrypointMetadata[] _networkMetadata =
            new VRC.SDK3.UdonNetworkCalling.NetworkCallingEntrypointMetadata[0];

        // Foldout state for the generated program. Not serialised: it only has to survive repaints.
        [NonSerialized] private bool _showPublicVariables;
        [NonSerialized] private bool _showAssembly;
        [NonSerialized] private bool _showDisassembly;
        [NonSerialized] private Vector2 _assemblyScroll;
        [NonSerialized] private Vector2 _disassemblyScroll;

        public IReadOnlyList<CompileDiagnostic> LastDiagnostics => _lastDiagnostics;
        public IReadOnlyList<BetterUdonStructTypeInfo> StructTypes => structTypes;
        public string ClassName => className;

        public int SlotCount => lastSlotCount > 0 ? lastSlotCount : ProgramSymbolCount;

        public int GenericInstantiationCount => lastGenericInstantiationCount;

        public int InstructionWordCount
        {
            get
            {
                try
                {
                    return program?.ByteCode != null ? program.ByteCode.Length / 4 : 0;
                }
                catch
                {
                    return 0;
                }
            }
        }

        public int ProgramSymbolCount
        {
            get
            {
                try
                {
                    if (program?.SymbolTable == null)
                        return 0;

                    return program.SymbolTable.GetSymbols().Length;
                }
                catch
                {
                    return 0;
                }
            }
        }

        /// <summary>True when the program declares at least one synced variable.</summary>
        public bool HasSyncedVariables => hasSyncedVariables;

        /// <summary>
        /// True when the synced variables ask for interpolation, which needs continuous sync rather
        /// than the manual mode used otherwise.
        /// </summary>
        public bool SyncNeedsContinuous => syncNeedsContinuous;

        /// <summary>
        /// Forced sync mode from <c>[BetterUdonBehaviourSyncMode]</c>: 0 Any, 1 None, 2 NoVariableSync,
        /// 3 Continuous, 4 Manual.
        /// </summary>
        public int SyncModeOverride => syncModeOverride;

        public void SetSource(MonoScript script, string typeName)
        {
            sourceScript = script;
            className = typeName;
            EditorUtility.SetDirty(this);
        }

        public static string GetSourcePath(MonoScript script)
        {
            if (script == null)
                return null;

            string path = AssetDatabase.GetAssetPath(script);
            return string.IsNullOrEmpty(path) ? null : Path.GetFullPath(path);
        }

        protected override void RefreshProgramImpl()
        {
            if (sourceScript == null)
            {
                assemblyError = "No source script assigned.";
                program = null;
                return;
            }

            string sourcePath = GetSourcePath(sourceScript);
            List<BetterUdonCompileResult> results = BetterUdonCompiler.CompileFile(sourcePath);

            BetterUdonCompileResult result = results.FirstOrDefault(r => r.ClassName == className)
                                             ?? results.FirstOrDefault();

            if (result == null)
            {
                assemblyError = "The compiler did not return a result.";
                program = null;
                return;
            }

            className = result.ClassName;
            _lastDiagnostics = result.Diagnostics;

            if (result.Success)
            {
                lastSlotCount = result.SlotCount;
                lastGenericInstantiationCount = result.GenericInstantiationCount;
            }

            if (result.Success)
            {
                hasSyncedVariables = result.HasSyncedVariables;
                syncNeedsContinuous = result.SyncNeedsContinuous;
                syncModeOverride = result.SyncModeOverride;
                _networkMetadata = result.NetworkMetadata ?? new VRC.SDK3.UdonNetworkCalling.NetworkCallingEntrypointMetadata[0];
            }

            if (!result.Success)
            {
                program = null;
                assemblyError = string.Join("\n", result.Diagnostics.Select(d => d.ToString()));
                structTypes = result.StructTypes ?? new List<BetterUdonStructTypeInfo>();
                return;
            }

            structTypes = result.StructTypes ?? new List<BetterUdonStructTypeInfo>();

            udonAssembly = result.Assembly;

            try
            {
                program = BetterUdonAssembler.Assemble(result.Assembly, result.HeapSize);
                assemblyError = null;
            }
            catch (Exception e)
            {
                program = null;
                assemblyError = e.Message;
            }

            if (program != null)
            {
                ApplyHeapInitialValues(result.HeapInitialValues);
                CollectExportedFields(result.HeapInitialValues);

                string serializationError = ValidateSerializable(program);

                if (serializationError != null)
                {
                    program = null;
                    assemblyError = "The compiled program cannot be stored: " + serializationError;
                }
            }

            EditorUtility.SetDirty(this);
        }

        private void ApplyHeapInitialValues(Dictionary<string, object> values)
        {
            if (program?.Heap == null || program.SymbolTable == null)
                return;

            foreach (KeyValuePair<string, object> pair in values)
            {
                if (!program.SymbolTable.HasAddressForSymbol(pair.Key))
                    continue;

                if (pair.Value == null)
                    continue;

                // Scene and asset references belong in the behaviour's public variable table, not in
                // the program: Udon serialises programs without a UnityEngine.Object context, and a
                // heap variable holding one of those references makes the stored program unreadable.
                if (ContainsUnityObject(pair.Value))
                    continue;

                uint address = program.SymbolTable.GetAddressFromSymbol(pair.Key);
                Type valueType = UdonTypeSystem.GetHeapType(pair.Value);

                // Prefer the type the symbol already declares. Udon's serialiser loses the symbol table
                // for some heap type/value combinations (array types, boxed enums next to strings), and
                // the declared type is the one the program itself expects anyway.
                Type symbolType = program.SymbolTable.GetSymbolType(pair.Key);

                if (symbolType != null)
                    valueType = symbolType;

                try
                {
                    program.Heap.SetHeapVariable(address, pair.Value, valueType ?? typeof(object));
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[BetterUdon] Failed to set the initial value of '{pair.Key}' on '{name}': {e.Message}");
                }
            }
        }

        /// <summary>
        /// Udon persists programs with Odin's serializer. A value it cannot round-trip corrupts the
        /// whole program: the writer succeeds, but the reader throws per symbol and the symbol table is
        /// silently lost, so the behaviour only fails at load time with errors that point at Udon
        /// internals rather than at the offending value. Round-tripping the program here turns that
        /// into a compile error naming the cause.
        /// </summary>
        private static string ValidateSerializable(IUdonProgram candidate)
        {
            try
            {
                byte[] bytes = VRC.Udon.Serialization.OdinSerializer.SerializationUtility.SerializeValue<IUdonProgram>(
                    candidate, VRC.Udon.Serialization.OdinSerializer.DataFormat.Binary);

                IUdonProgram roundTripped = VRC.Udon.Serialization.OdinSerializer.SerializationUtility.DeserializeValue<IUdonProgram>(
                    bytes, VRC.Udon.Serialization.OdinSerializer.DataFormat.Binary);

                if (roundTripped == null)
                    return "the program was lost.";

                if (roundTripped.SymbolTable == null)
                    return "the symbol table was lost.";

                if (roundTripped.Heap == null)
                    return "the heap was lost.";

                return null;
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }

        /// <summary>
        /// Whether a heap value refers to a <see cref="UnityEngine.Object"/> directly or through an
        /// array. Udon stores programs with an object table that is built separately, so such values
        /// cannot be baked into the heap default.
        /// </summary>
        private static bool ContainsUnityObject(object value)
        {
            if (value == null)
                return false;

            if (value is UnityEngine.Object)
                return true;

            if (!(value is Array array))
                return false;

            // An array of references is serialised by its element type, so the elements do not have to
            // be assigned for the value to be unsafe to bake into the program.
            Type elementType = value.GetType().GetElementType();
            if (elementType != null && typeof(UnityEngine.Object).IsAssignableFrom(elementType))
                return true;

            foreach (object element in array)
            {
                if (ContainsUnityObject(element))
                    return true;
            }

            return false;
        }

        private void CollectExportedFields(Dictionary<string, object> heapValues)
        {
            exportedFieldNames.Clear();
            exportedFieldTypes.Clear();

            if (program?.SymbolTable == null)
                return;

            foreach (string symbol in program.SymbolTable.GetExportedSymbols())
            {
                exportedFieldNames.Add(symbol);

                object value;
                if (!heapValues.TryGetValue(symbol, out value) || value == null)
                {
                    exportedFieldTypes.Add(null);
                    continue;
                }

                exportedFieldTypes.Add(UdonTypeSystem.GetHeapType(value)?.AssemblyQualifiedName);
            }
        }

        protected override VRC.SDK3.UdonNetworkCalling.NetworkCallingEntrypointMetadata[] GetLastNetworkCallingMetadata()
        {
            return _networkMetadata != null && _networkMetadata.Length > 0
                ? _networkMetadata
                : new VRC.SDK3.UdonNetworkCalling.NetworkCallingEntrypointMetadata[0];
        }

        protected override object GetPublicVariableDefaultValue(string symbol, Type type)
        {
            if (program == null && SerializedProgramAsset != null)
                program = SerializedProgramAsset.RetrieveProgram();

            if (program?.SymbolTable == null || program.Heap == null)
                return null;

            if (!program.SymbolTable.HasAddressForSymbol(symbol))
                return null;

            return program.Heap.GetHeapVariable(program.SymbolTable.GetAddressFromSymbol(symbol));
        }

        protected override void DrawProgramSourceGUI(UdonBehaviour udonBehaviour, ref bool dirty)
        {
            // Tools first, in their own box.
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                BetterUdonInspectorGUI.SectionTitle("BetterUdon");

                EditorGUI.BeginChangeCheck();
                MonoScript newSource = (MonoScript)EditorGUILayout.ObjectField("Source Script", sourceScript, typeof(MonoScript), false);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(this, "Set BetterUdon source script");
                    sourceScript = newSource;
                    dirty = true;

                    if (newSource != null)
                    {
                        Type scriptType = newSource.GetClass();
                        if (scriptType != null)
                            className = scriptType.Name;
                    }
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Recompile", GUILayout.Height(22f)))
                        RefreshProgram();

                    if (GUILayout.Button("Copy Assembly", GUILayout.Height(22f)))
                        EditorGUIUtility.systemCopyBuffer = udonAssembly;
                }

                if (_lastDiagnostics.Count > 0)
                {
                    EditorGUILayout.HelpBox(
                        "Compile errors:\n" + string.Join("\n", _lastDiagnostics.Select(d => d.ToString())),
                        MessageType.Error);
                }
                else if (!string.IsNullOrEmpty(assemblyError))
                {
                    EditorGUILayout.HelpBox("The assembly could not be built:\n" + assemblyError, MessageType.Error);
                }
            }

            // The values the script exposes, on their own. Collapsed by default because synced structs
            // add compiler generated mirrors (__sync_*) to this list.
            EditorGUILayout.Space(8f);
            _showPublicVariables = EditorGUILayout.Foldout(_showPublicVariables, "Udon Variables", true);

            if (_showPublicVariables)
                DrawPublicVariables(udonBehaviour, ref dirty);

            // The generated program, collapsed by default so it does not compete with the fields.
            EditorGUILayout.Space(8f);
            DrawProgramBox();
        }

        private void DrawProgramBox()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                BetterUdonInspectorGUI.SectionTitle("Program");

                _showAssembly = EditorGUILayout.Foldout(_showAssembly, "Assembly", true);
                if (_showAssembly)
                    BetterUdonInspectorGUI.CodeBlock(udonAssembly, ref _assemblyScroll);

                _showDisassembly = EditorGUILayout.Foldout(_showDisassembly, "Disassembly", true);
                if (_showDisassembly)
                {
                    string disassembly;

                    try
                    {
                        IUdonProgram currentProgram = GetProgram();
                        disassembly = currentProgram == null
                            ? string.Empty
                            : string.Join("\n", UdonEditorManager.Instance.DisassembleProgram(currentProgram));
                    }
                    catch (Exception e)
                    {
                        disassembly = "Disassembly failed: " + e.Message;
                    }

                    BetterUdonInspectorGUI.CodeBlock(disassembly, ref _disassemblyScroll);
                }
            }
        }

        public HashSet<string> GetStructTypeNames()
        {
            HashSet<string> names = new HashSet<string>();

            foreach (BetterUdonStructTypeInfo info in structTypes)
            {
                if (!string.IsNullOrEmpty(info.typeName))
                    names.Add(info.typeName);
            }

            return names;
        }

        public string GetAssembly() => udonAssembly;

        public IUdonProgram GetProgram()
        {
            if (program == null && SerializedProgramAsset != null)
            {
                try
                {
                    program = SerializedProgramAsset.RetrieveProgram();
                }
                catch (Exception e)
                {
                    // The asset exists but has no stored program yet (a failed compile), so there is
                    // nothing to retrieve.
                    Debug.LogWarning($"[BetterUdon] '{name}' has no stored program: {e.Message}");
                    program = null;
                }
            }

            return program;
        }

        public Type GetExportedSymbolType(string symbol)
        {
            IUdonProgram currentProgram = GetProgram();
            if (currentProgram?.SymbolTable == null)
                return null;

            if (!currentProgram.SymbolTable.HasExportedSymbol(symbol))
                return null;

            return currentProgram.SymbolTable.GetSymbolType(symbol);
        }
    }
}
