using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UnityEditor;
using UnityEngine;

namespace BetterUdon.Editor
{
    [Serializable]
    public sealed class BetterUdonStructFieldInfo
    {
        public string name;
        public string typeName; // Assembly qualified name of the C# type of the field
        public bool isUserStruct;
        public bool isArray;
        public string elementTypeName;
        public bool elementIsUserStruct;
    }

    [Serializable]
    public sealed class BetterUdonStructTypeInfo
    {
        public string typeName; // Assembly qualified name
        public string displayName;
        public List<BetterUdonStructFieldInfo> fields = new List<BetterUdonStructFieldInfo>();
    }

    internal sealed class BetterUdonCompileResult
    {
        public string ClassName;
        public string Assembly;
        public List<CompileDiagnostic> Diagnostics = new List<CompileDiagnostic>();
        public Dictionary<string, object> HeapInitialValues = new Dictionary<string, object>();
        public List<string> EntryPoints = new List<string>();
        public List<BetterUdonStructTypeInfo> StructTypes = new List<BetterUdonStructTypeInfo>();
        public uint HeapSize;
        public int SlotCount;
        public int GenericInstantiationCount;

        /// <summary>Metadata for <c>[NetworkCallable]</c> entry points, stored with the program.</summary>
        public VRC.SDK3.UdonNetworkCalling.NetworkCallingEntrypointMetadata[] NetworkMetadata;

        /// <summary>True when at least one field is marked with <c>[BetterUdonSynced]</c>.</summary>
        public bool HasSyncedVariables;

        /// <summary>
        /// True when a synced field asks for linear or smooth interpolation, which only continuous
        /// synchronisation supports.
        /// </summary>
        public bool SyncNeedsContinuous;

        /// <summary>
        /// Forced sync mode from <c>[BetterUdonBehaviourSyncMode]</c>: 0 Any, 1 None, 2 NoVariableSync,
        /// 3 Continuous, 4 Manual.
        /// </summary>
        public int SyncModeOverride;

        public bool Success => Assembly != null && Diagnostics.Count == 0;
    }

    /// <summary>
    /// Entry point of the BetterUdon C# to Udon assembly compiler.
    /// </summary>
    internal static class BetterUdonCompiler
    {
        public const string BaseClassName = "BetterUdon.BetterUdonBehaviour";

                private static List<MetadataReference> _references;

        /// <summary>
        /// Compiles every BetterUdonBehaviour declared in the given source file.
        /// </summary>
        public static List<BetterUdonCompileResult> CompileFile(string sourcePath)
        {
            return CompileFile(sourcePath, UdonOptimizationFlags.Recommended);
        }

        /// <summary>
        /// Compiles every BetterUdonBehaviour declared in the given source file using the requested
        /// output optimizations.
        /// </summary>
        public static List<BetterUdonCompileResult> CompileFile(string sourcePath, UdonOptimizationFlags optimizationFlags)
        {
            List<BetterUdonCompileResult> results = new List<BetterUdonCompileResult>();

            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                return results;

            sourcePath = Path.GetFullPath(sourcePath);
            string cacheKey = sourcePath + "|" + (int)optimizationFlags;

            DateTime stamp = GetSourcesStamp();
            if (_resultCacheStamp == stamp && _resultCache.TryGetValue(cacheKey, out List<BetterUdonCompileResult> cached))
                return cached;

            try
            {
                List<SyntaxTree> trees = CollectSyntaxTrees(sourcePath);
                CSharpCompilation compilation = CSharpCompilation.Create(
                    "BetterUdonCompilation",
                    trees,
                    GetReferences(),
                    new CSharpCompilationOptions(
                        OutputKind.DynamicallyLinkedLibrary,
                        allowUnsafe: true,
                        nullableContextOptions: NullableContextOptions.Disable,
                        optimizationLevel: OptimizationLevel.Debug));

                SyntaxTree targetTree = trees.First(t => string.Equals(t.FilePath, sourcePath, StringComparison.OrdinalIgnoreCase));
                SemanticModel model = compilation.GetSemanticModel(targetTree);

                foreach (ClassDeclarationSyntax classDeclaration in targetTree.GetRoot()
                             .DescendantNodes()
                             .OfType<ClassDeclarationSyntax>())
                {
                    INamedTypeSymbol classSymbol = model.GetDeclaredSymbol(classDeclaration);
                    if (classSymbol == null || !DerivesFromBetterUdonBehaviour(classSymbol))
                        continue;

                    results.Add(CompileBehaviour(model, classSymbol, optimizationFlags));
                }

                if (results.Count == 0)
                {
                    results.Add(new BetterUdonCompileResult
                    {
                        ClassName = Path.GetFileNameWithoutExtension(sourcePath),
                        Diagnostics =
                        {
                            new CompileDiagnostic
                            {
                                Message = $"No class deriving from {BaseClassName} was found in this file.",
                                Line = 1,
                                Column = 1,
                            },
                        },
                    });
                }
            }
            catch (Exception e)
            {
                results.Add(new BetterUdonCompileResult
                {
                    ClassName = Path.GetFileNameWithoutExtension(sourcePath),
                    Diagnostics =
                    {
                        new CompileDiagnostic { Message = "Compiler crashed: " + e, Line = 0, Column = 0 },
                    },
                });
            }

            _resultCache[cacheKey] = results;
            return results;
        }

        private static readonly Dictionary<string, List<BetterUdonCompileResult>> _resultCache =
            new Dictionary<string, List<BetterUdonCompileResult>>(StringComparer.OrdinalIgnoreCase);

        private static DateTime _resultCacheStamp;

        private static DateTime GetSourcesStamp()
        {
            DateTime stamp = DateTime.MinValue;

            foreach (string path in EnumerateUserScripts())
            {
                DateTime writeTime;
                try
                {
                    writeTime = File.GetLastWriteTimeUtc(path);
                }
                catch
                {
                    continue;
                }

                if (writeTime > stamp)
                    stamp = writeTime;
            }

            if (stamp != _resultCacheStamp)
            {
                _resultCache.Clear();
                _resultCacheStamp = stamp;
            }

            return stamp;
        }

        private static bool DerivesFromBetterUdonBehaviour(INamedTypeSymbol symbol)
        {
            for (INamedTypeSymbol current = symbol; current != null; current = current.BaseType)
            {
                if (current.ToDisplayString() == BaseClassName)
                    return true;
            }

            return false;
        }

        private static BetterUdonCompileResult CompileBehaviour(SemanticModel model, INamedTypeSymbol classSymbol,
            UdonOptimizationFlags optimizationFlags)
        {
            BetterUdonCompileResult result = new BetterUdonCompileResult
            {
                ClassName = classSymbol.Name,
            };

            BehaviourCodeGenerator generator = new BehaviourCodeGenerator(model, classSymbol, optimizationFlags);
            string assembly = generator.Compile();

            result.Diagnostics.AddRange(generator.Diagnostics);
            result.HeapInitialValues = generator.ComputeHeapInitialValues();
            result.HeapSize = generator.HeapSize;
            result.SlotCount = generator.SlotCount;
            result.GenericInstantiationCount = generator.GenericInstantiationCount;
            result.EntryPoints.AddRange(generator.ExportedEntryPoints);
            result.HasSyncedVariables = generator.HasSyncedVariables;
            result.SyncNeedsContinuous = generator.SyncNeedsContinuous;
            result.SyncModeOverride = generator.SyncModeOverride;
            result.NetworkMetadata = generator.NetworkMetadata.ToArray();

            if (result.Diagnostics.Count == 0)
                result.Assembly = assembly;
            else
                result.Assembly = null;

            foreach (KeyValuePair<ITypeSymbol, StructLayout> pair in generator.StructLayouts)
            {
                BetterUdonStructTypeInfo info = new BetterUdonStructTypeInfo
                {
                    typeName = UdonTypeSystem.ToSystemType(pair.Key)?.AssemblyQualifiedName,
                    displayName = pair.Key.ToDisplayString(),
                };

                foreach (IFieldSymbol field in pair.Value.Fields)
                {
                    Type fieldType = UdonTypeSystem.ToSystemType(field.Type);
                    ITypeSymbol elementType = (field.Type as IArrayTypeSymbol)?.ElementType;

                    info.fields.Add(new BetterUdonStructFieldInfo
                    {
                        name = field.Name,
                        typeName = fieldType?.AssemblyQualifiedName,
                        isUserStruct = UdonTypeSystem.IsUserStruct(field.Type),
                        isArray = field.Type is IArrayTypeSymbol,
                        elementTypeName = elementType != null ? UdonTypeSystem.ToSystemType(elementType)?.AssemblyQualifiedName : null,
                        elementIsUserStruct = elementType != null && UdonTypeSystem.IsUserStruct(elementType),
                    });
                }

                result.StructTypes.Add(info);
            }

            return result;
        }

        private static List<SyntaxTree> CollectSyntaxTrees(string targetPath)
        {
            List<SyntaxTree> trees = new List<SyntaxTree>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string path in EnumerateUserScripts())
            {
                if (!seen.Add(path))
                    continue;

                SyntaxTree tree = ParseSyntaxTree(path);
                if (tree != null)
                    trees.Add(tree);
            }

            if (!seen.Contains(targetPath))
            {
                SyntaxTree tree = ParseSyntaxTree(targetPath);
                if (tree != null)
                    trees.Add(tree);
            }

            // Two syntax trees for the same file would make Roslyn return symbols whose declaring
            // references point at the wrong tree, so collapse duplicates.
            if (trees.Count > 1)
            {
                HashSet<string> uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                List<SyntaxTree> unique = new List<SyntaxTree>(trees.Count);

                foreach (SyntaxTree tree in trees)
                {
                    if (uniquePaths.Add(tree.FilePath))
                        unique.Add(tree);
                }

                trees = unique;
            }

            return trees;
        }

        private static SyntaxTree ParseSyntaxTree(string path)
        {
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch
            {
                return null;
            }

            CSharpParseOptions options = new CSharpParseOptions(
                LanguageVersion.Latest,
                preprocessorSymbols: GetDefines());

            return CSharpSyntaxTree.ParseText(text, options, path);
        }

        private static IEnumerable<string> EnumerateUserScripts()
        {
            string assetsPath = Application.dataPath;

            foreach (string path in Directory.EnumerateFiles(assetsPath, "*.cs", SearchOption.AllDirectories))
            {
                // Skip editor only scripts, they are not part of the runtime assembly.
                string relative = path.Substring(assetsPath.Length).Replace('\\', '/');
                if (relative.Contains("/Editor/") || relative.StartsWith("/Editor/", StringComparison.Ordinal))
                    continue;

                if (relative.Contains("/BetterUdonPrograms/"))
                    continue;

                yield return Path.GetFullPath(path);
            }
        }


        private static string[] _defines;

        private static string[] GetDefines()
        {
            if (_defines != null)
                return _defines;

            List<string> defines = new List<string>
            {
                "UNITY_EDITOR",
                "UNITY_2022_3_OR_NEWER",
                "UNITY_STANDALONE",
                "UNITY_STANDALONE_WIN",
                "VRC_SDK_VRCSDK3",
            };

            try
            {
                defines.AddRange(EditorUserBuildSettings.activeScriptCompilationDefines);
            }
            catch
            {
                // Ignored, the fallback defines above are enough to parse most scripts.
            }

            _defines = defines.Distinct().ToArray();
            return _defines;
        }

        private static List<MetadataReference> GetReferences()
        {
            if (_references != null)
                return _references;

            Dictionary<string, MetadataReference> byName = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);

            foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                    continue;

                string location;
                try
                {
                    location = assembly.Location;
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrEmpty(location) || !File.Exists(location))
                    continue;

                string name = assembly.GetName().Name;
                if (byName.ContainsKey(name))
                    continue;

                try
                {
                    byName.Add(name, MetadataReference.CreateFromFile(location));
                }
                catch
                {
                    // Ignore assemblies that cannot be referenced.
                }
            }

            _references = byName.Values.ToList();
            return _references;
        }

        public static void ResetCaches()
        {
            _references = null;
        }
    }
}
