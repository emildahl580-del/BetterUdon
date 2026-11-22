using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Udon;
using VRC.Udon.Common;
using VRC.Udon.Common.Interfaces;

namespace BetterUdon.Editor
{
    internal static class BetterUdonEditorUtility
    {
        public const string ProgramAssetFolder = "Assets/BetterUdonPrograms";

        private static readonly FieldInfo _serializedProgramAssetField =
            typeof(UdonBehaviour).GetField("serializedProgramAsset", BindingFlags.NonPublic | BindingFlags.Instance);

        public static string GetProgramAssetPath(Type behaviourType)
        {
            return $"{ProgramAssetFolder}/{behaviourType.Name}.asset";
        }

        public static BetterUdonProgramAsset GetProgramAsset(Type behaviourType, bool createIfMissing = true)
        {
            string path = GetProgramAssetPath(behaviourType);
            BetterUdonProgramAsset asset = AssetDatabase.LoadAssetAtPath<BetterUdonProgramAsset>(path);

            if (asset != null || !createIfMissing)
                return asset;

            if (!AssetDatabase.IsValidFolder(ProgramAssetFolder))
            {
                if (!AssetDatabase.IsValidFolder("Assets"))
                    return null;

                AssetDatabase.CreateFolder("Assets", "BetterUdonPrograms");
            }

            asset = ScriptableObject.CreateInstance<BetterUdonProgramAsset>();
            asset.name = behaviourType.Name;
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            return asset;
        }

        public static MonoScript FindScriptForType(Type behaviourType)
        {
            foreach (string guid in AssetDatabase.FindAssets($"t:MonoScript {behaviourType.Name}"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);

                if (script != null && script.GetClass() == behaviourType)
                    return script;
            }

            return null;
        }

        /// <summary>
        /// Ensures the behaviour has a backing UdonBehaviour with an up to date program asset.
        /// </summary>
        public static UdonBehaviour SetupBackingBehaviour(BetterUdonBehaviour behaviour, bool recompile = true)
        {
            if (behaviour == null)
                return null;

            UdonBehaviour udonBehaviour = behaviour.udonBehaviour;

            if (udonBehaviour == null)
            {
                udonBehaviour = behaviour.GetComponent<UdonBehaviour>();

                if (udonBehaviour == null)
                {
                    udonBehaviour = behaviour.gameObject.AddComponent<UdonBehaviour>();
                    udonBehaviour.hideFlags = HideFlags.HideInInspector;
                }

                behaviour.udonBehaviour = udonBehaviour;
                EditorUtility.SetDirty(behaviour);
            }

            Type behaviourType = behaviour.GetType();
            BetterUdonProgramAsset programAsset = GetProgramAsset(behaviourType);

            if (programAsset == null)
                return udonBehaviour;

            if (programAsset.sourceScript == null)
                programAsset.SetSource(FindScriptForType(behaviourType), behaviourType.Name);

            if (recompile && programAsset.sourceScript != null)
                programAsset.RefreshProgram();

            if (udonBehaviour.programSource != programAsset)
            {
                udonBehaviour.programSource = programAsset;
                EditorUtility.SetDirty(udonBehaviour);
            }

            if (_serializedProgramAssetField != null && programAsset.SerializedProgramAsset != null &&
                !ReferenceEquals(_serializedProgramAssetField.GetValue(udonBehaviour), programAsset.SerializedProgramAsset))
            {
                _serializedProgramAssetField.SetValue(udonBehaviour, programAsset.SerializedProgramAsset);
                EditorUtility.SetDirty(udonBehaviour);
            }

            ApplySyncMode(udonBehaviour, programAsset);

            if (udonBehaviour.enabled != behaviour.enabled)
                udonBehaviour.enabled = behaviour.enabled;

            return udonBehaviour;
        }

        /// <summary>
        /// Selects the synchronisation mode the program needs. A program with synced variables uses
        /// manual sync, so values are sent by <c>RequestSerialization()</c>; interpolation can only be
        /// done by continuous sync. Programs without synced variables keep whatever mode the
        /// behaviour already has, because <c>SendCustomNetworkEvent</c> also needs a sync mode.
        /// </summary>
        private static void ApplySyncMode(UdonBehaviour udonBehaviour, BetterUdonProgramAsset programAsset)
        {
            if (!programAsset.HasSyncedVariables)
                return;

            VRC.SDKBase.Networking.SyncType desired = programAsset.SyncNeedsContinuous
                ? VRC.SDKBase.Networking.SyncType.Continuous
                : VRC.SDKBase.Networking.SyncType.Manual;

            if (udonBehaviour.SyncMethod == desired)
                return;

            udonBehaviour.SyncMethod = desired;
            EditorUtility.SetDirty(udonBehaviour);
        }

        private static readonly Dictionary<string, FieldInfo[]> _fieldCache = new Dictionary<string, FieldInfo[]>();

        public static IEnumerable<FieldInfo> GetUserFields(Type behaviourType)
        {
            if (_fieldCache.TryGetValue(behaviourType.AssemblyQualifiedName ?? behaviourType.Name, out FieldInfo[] cached))
                return cached;

            List<FieldInfo> fields = new List<FieldInfo>();

            foreach (FieldInfo field in behaviourType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (field.IsInitOnly || field.IsLiteral)
                    continue;

                if (!field.IsPublic && !field.IsDefined(typeof(SerializeField), true))
                    continue;

                fields.Add(field);
            }

            Type baseType = behaviourType.BaseType;
            if (baseType != null && baseType != typeof(BetterUdonBehaviour) && baseType != typeof(MonoBehaviour))
            {
                // Inherited user fields (a behaviour deriving from another behaviour) are handled by the
                // base type's own enumeration.
                fields.AddRange(GetUserFields(baseType));
            }

            FieldInfo[] result = fields.ToArray();
            _fieldCache[behaviourType.AssemblyQualifiedName ?? behaviourType.Name] = result;
            return result;
        }

        /// <summary>
        /// Copies the C# field values into the Udon public variable table so the program sees them.
        /// </summary>
        public static void SyncFieldsToUdon(BetterUdonBehaviour behaviour)
        {
            UdonBehaviour udonBehaviour = behaviour.udonBehaviour;
            if (udonBehaviour == null)
                return;

            BetterUdonProgramAsset programAsset = udonBehaviour.programSource as BetterUdonProgramAsset;

            // Without a stored program there are no symbols to write into, and the UdonBehaviour will
            // refuse to run anyway.
            if (programAsset == null || programAsset.GetProgram() == null)
                return;

            HashSet<string> structTypeNames = programAsset?.GetStructTypeNames() ?? new HashSet<string>();

            bool changed = false;

            foreach (FieldInfo field in GetUserFields(behaviour.GetType()))
            {
                if (field.IsDefined(typeof(BetterUdonNoExportAttribute), true))
                    continue;

                try
                {
                    object value = field.GetValue(behaviour);
                    object heapValue = ToHeapValue(value, field.FieldType, structTypeNames);

                    // Only exported symbols become public variables; everything else keeps the value
                    // baked into the program's heap.
                    Type declaredType = programAsset?.GetExportedSymbolType(field.Name);

                    if (declaredType == null)
                    {
                        // Drop stale entries left behind by an earlier compile.
                        if (udonBehaviour.publicVariables.TryGetVariableType(field.Name, out _))
                        {
                            udonBehaviour.publicVariables.RemoveVariable(field.Name);
                            changed = true;
                        }

                        continue;
                    }

                    if (SetPublicVariable(udonBehaviour, field.Name, heapValue, declaredType))
                        changed = true;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[BetterUdon] Failed to sync field '{field.Name}': {e.Message}");
                }
            }

            if (changed)
            {
                EditorUtility.SetDirty(udonBehaviour);
                if (!Application.isPlaying)
                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(behaviour.gameObject.scene);
            }
        }

        private static bool SetPublicVariable(UdonBehaviour udonBehaviour, string name, object value, Type declaredType)
        {
            IUdonVariableTable publicVariables = udonBehaviour.publicVariables;
            if (publicVariables == null)
                return false;

            // Udon cannot resolve a null GameObject/Component reference in the public variable table, so
            // such variables are removed and fall back to the heap default instead.
            if (value == null && declaredType != null && typeof(UnityEngine.Object).IsAssignableFrom(declaredType))
            {
                if (!publicVariables.TryGetVariableType(name, out _))
                    return false;

                publicVariables.RemoveVariable(name);
                return true;
            }

            bool hasVariable = publicVariables.TryGetVariableType(name, out Type variableType);
            bool typeMatches = hasVariable && (value == null || variableType.IsInstanceOfType(value));

            if (typeMatches)
            {
                publicVariables.TryGetVariableValue(name, out object existing);

                if (Equals(existing, value))
                    return false;

                publicVariables.TrySetVariableValue(name, value);
                return true;
            }

            // The stored variable has the wrong type (for example after a struct layout change), so
            // recreate it with a type that can hold the new value.
            Type newType = declaredType != null && (value == null || declaredType.IsInstanceOfType(value))
                ? declaredType
                : UdonTypeSystem.GetHeapType(value) ?? typeof(object);

            IUdonVariable variable;
            try
            {
                variable = (IUdonVariable)Activator.CreateInstance(typeof(UdonVariable<>).MakeGenericType(newType), name, value);
            }
            catch
            {
                variable = (IUdonVariable)Activator.CreateInstance(typeof(UdonVariable<object>), name, value);
            }

            publicVariables.RemoveVariable(name);

            if (!publicVariables.TryAddVariable(variable))
                return false;

            return true;
        }

        /// <summary>
        /// Converts a C# value into the representation used on the Udon heap. User structs become
        /// <c>object[]</c>s of their fields.
        /// </summary>
        public static object ToHeapValue(object value, Type type, HashSet<string> structTypeNames)
        {
            if (type == null)
                return value;

            // A reference to another BetterUdon behaviour is stored as its backing UdonBehaviour: the
            // C# component is removed from a built world, the UdonBehaviour is what survives and owns
            // the variables.
            if (value is BetterUdonBehaviour behaviourReference)
                return behaviourReference.udonBehaviour;

            // User enums live on the heap as their underlying integer.
            if (type.IsEnum && UdonTypeSystem.IsUserEnum(type))
            {
                if (value == null)
                    return null;

                return Convert.ChangeType(value, Enum.GetUnderlyingType(type));
            }

            if (IsStructType(type, structTypeNames))
            {
                if (value == null)
                    return null;

                FieldInfo[] fields = GetStructFields(type);
                object[] result = new object[fields.Length];

                for (int i = 0; i < fields.Length; i++)
                    result[i] = ToHeapValue(fields[i].GetValue(value), fields[i].FieldType, structTypeNames);

                return result;
            }

            if (type.IsArray && type.GetArrayRank() == 1)
            {
                Type elementType = type.GetElementType();

                if (elementType == null)
                    return value;

                Array source = value as Array;
                if (source == null)
                    return null;

                bool elementIsStruct = IsStructType(elementType, structTypeNames);
                bool elementIsUnityObject = typeof(UnityEngine.Object).IsAssignableFrom(elementType);

                // Struct elements and Unity object references both use the object[] storage form.
                if (!elementIsStruct && !elementIsUnityObject)
                    return value;

                object[] arrayResult = new object[source.Length];
                for (int i = 0; i < source.Length; i++)
                    arrayResult[i] = ToHeapValue(source.GetValue(i), elementType, structTypeNames);

                return arrayResult;
            }

            return value;
        }

        private static bool IsStructType(Type type, HashSet<string> structTypeNames)
        {
            if (structTypeNames != null && structTypeNames.Count > 0)
                return structTypeNames.Contains(type.AssemblyQualifiedName);

            return UdonTypeSystem.IsUserStruct(type);
        }

        public static FieldInfo[] GetStructFields(Type structType)
        {
            return structType
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(f => !f.IsStatic && !f.IsLiteral)
                .ToArray();
        }

        public static void CompileAll()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:BetterUdonProgramAsset"))
            {
                BetterUdonProgramAsset asset = AssetDatabase.LoadAssetAtPath<BetterUdonProgramAsset>(AssetDatabase.GUIDToAssetPath(guid));
                asset?.RefreshProgram();
            }

            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// Runs the setup + sync pass over every BetterUdonBehaviour in the loaded scenes.
        /// </summary>
        public static void SyncAll(bool recompile)
        {
            BetterUdonBehaviour[] behaviours = Resources.FindObjectsOfTypeAll<BetterUdonBehaviour>();

            foreach (BetterUdonBehaviour behaviour in behaviours)
            {
                if (behaviour == null || EditorUtility.IsPersistent(behaviour) || !behaviour.gameObject.scene.IsValid())
                    continue;

                SetupBackingBehaviour(behaviour, recompile);
                SyncFieldsToUdon(behaviour);
            }
        }
    }
}
