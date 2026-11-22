using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;

namespace BetterUdon.Editor
{
    /// <summary>
    /// Maps Roslyn type symbols onto Udon type names and decides how a given C# type is stored on the
    /// Udon heap.
    /// </summary>
    internal static class UdonTypeSystem
    {
        private static readonly Dictionary<string, string> _primitiveNames = new Dictionary<string, string>
        {
            { "System.Boolean", "SystemBoolean" },
            { "System.Byte", "SystemByte" },
            { "System.SByte", "SystemSByte" },
            { "System.Int16", "SystemInt16" },
            { "System.UInt16", "SystemUInt16" },
            { "System.Int32", "SystemInt32" },
            { "System.UInt32", "SystemUInt32" },
            { "System.Int64", "SystemInt64" },
            { "System.UInt64", "SystemUInt64" },
            { "System.Single", "SystemSingle" },
            { "System.Double", "SystemDouble" },
            { "System.Decimal", "SystemDecimal" },
            { "System.Char", "SystemChar" },
            { "System.String", "SystemString" },
            { "System.Object", "SystemObject" },
            { "System.Void", "SystemVoid" },
            { "System.IntPtr", "SystemIntPtr" },
            { "System.UIntPtr", "SystemUIntPtr" },
        };

        // Type remaps that Udon performs on the SDK side.
        private static readonly Dictionary<string, string> _remappedTypeNames = new Dictionary<string, string>
        {
            { "VRC.Udon.UdonBehaviour", "VRCUdonCommonInterfacesIUdonEventReceiver" },
            { "UdonSharp.UdonSharpBehaviour", "VRCUdonCommonInterfacesIUdonEventReceiver" },
            { "BetterUdon.BetterUdonBehaviour", "VRCUdonCommonInterfacesIUdonEventReceiver" },
        };

        private static readonly HashSet<string> _externAssemblyPrefixes = new HashSet<string>
        {
            "mscorlib", "netstandard", "System", "UnityEngine", "Unity.", "VRC.", "UdonSharp",
            "TMPro", "TextMeshPro", "nunit", "Newtonsoft", "Sirenix", "Cinemachine", "DOTween",
            "BestHTTP", "UnityEditor", "Bee", "Roslyn", "Microsoft.",
        };

        public static string Sanitize(string typeName)
        {
            return typeName
                .Replace("`", "")
                .Replace(",", "")
                .Replace(" ", "")
                .Replace(".", "")
                .Replace("[]", "Array")
                .Replace("&", "Ref")
                .Replace("+", "");
        }

        public static bool IsExternAssembly(IAssemblySymbol assembly)
        {
            if (assembly == null)
                return true;

            string name = assembly.Name;
            foreach (string prefix in _externAssemblyPrefixes)
            {
                if (name == prefix || name.StartsWith(prefix, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// A struct declared by the user. These are lowered to an <c>object[]</c> on the Udon heap so that
        /// they can actually be stored and copied while keeping value semantics.
        /// </summary>
        public static bool IsUserStruct(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol named && named.IsValueType && type.TypeKind == TypeKind.Struct)
            {
                if (named.SpecialType != SpecialType.None)
                    return false;

                if (named.DeclaringSyntaxReferences.Length > 0)
                    return true;

                return !IsExternAssembly(named.ContainingAssembly);
            }

            return false;
        }

        /// <summary>
        /// A struct type that Udon already knows about natively (Vector3, Color, ...).
        /// </summary>
        public static bool IsExternStruct(ITypeSymbol type)
        {
            return type != null && type.TypeKind == TypeKind.Struct && !IsUserStruct(type);
        }

        /// <summary>
        /// Runtime equivalent of <see cref="IsUserEnum(ITypeSymbol)"/>.
        /// </summary>
        public static bool IsUserEnum(Type type)
        {
            if (type == null || !type.IsEnum)
                return false;

            string assemblyName = type.Assembly.GetName().Name;

            foreach (string prefix in _externAssemblyPrefixes)
            {
                if (assemblyName == prefix || assemblyName.StartsWith(prefix, StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Runtime equivalent of <see cref="IsUserStruct(ITypeSymbol)"/>, used by the editor tooling when
        /// it converts inspector values into their heap representation.
        /// </summary>
        public static bool IsUserStruct(Type type)
        {
            if (type == null || !type.IsValueType || type.IsEnum || type.IsPrimitive)
                return false;

            string assemblyName = type.Assembly.GetName().Name;

            foreach (string prefix in _externAssemblyPrefixes)
            {
                if (assemblyName == prefix || assemblyName.StartsWith(prefix, StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        public static bool IsUserEnum(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol named && type.TypeKind == TypeKind.Enum)
                return !IsExternAssembly(named.ContainingAssembly) && named.DeclaringSyntaxReferences.Length > 0;

            return false;
        }

        public static ITypeSymbol GetEffectiveType(ITypeSymbol type)
        {
            if (type == null)
                return null;

            if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol namedEnum)
                return namedEnum.EnumUnderlyingType;

            return type;
        }

        /// <summary>
        /// Name used inside Udon heap declarations and extern signatures.
        /// </summary>
        public static string GetUdonTypeName(ITypeSymbol type)
        {
            if (type == null)
                return "SystemObject";

            if (type is IArrayTypeSymbol arrayType)
            {
                if (arrayType.ElementType is IArrayTypeSymbol || IsUserStruct(arrayType.ElementType))
                    return "SystemObjectArray";

                return GetUdonTypeName(arrayType.ElementType) + "Array";
            }

            if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol enumType)
            {
                // Enums live on the heap as their real enum values so that extern wrappers can unbox
                // them. Enums declared by the user are lowered to their underlying integer type since
                // Udon has no type for them.
                if (IsUserEnum(type))
                    return GetUdonTypeName(enumType.EnumUnderlyingType);

                return Sanitize(GetMetadataFullName(enumType));
            }

            if (type is INamedTypeSymbol named)
            {
                if (named.SpecialType != SpecialType.None)
                {
                    string specialName = GetSpecialTypeName(named.SpecialType);
                    if (specialName != null)
                        return specialName;
                }

                if (named.IsGenericType)
                {
                    // Generics are not exposed by Udon, with the exception of the generic collection types
                    // that the runtime special cases. Best effort only.
                    string genericName = Sanitize(named.ConstructedFrom.ToDisplayString());
                    foreach (ITypeSymbol typeArgument in named.TypeArguments)
                        genericName += GetUdonTypeName(typeArgument);

                    return genericName;
                }

                string fullName = GetMetadataFullName(named);
                if (_remappedTypeNames.TryGetValue(fullName, out string remapped))
                    return remapped;

                return Sanitize(fullName);
            }

            return "SystemObject";
        }

        /// <summary>
        /// The Udon type a value of this C# type is stored as. Every non extern value type is stored as an
        /// object (an <c>object[]</c> holding the struct fields).
        /// </summary>
        public static string GetStorageTypeName(ITypeSymbol type)
        {
            if (type == null)
                return "SystemObject";

            if (type.TypeKind == TypeKind.Struct || type.TypeKind == TypeKind.Enum)
            {
                if (IsUserStruct(type))
                    return "SystemObject";

                return GetUdonTypeName(type);
            }

            if (type is IArrayTypeSymbol array)
            {
                if (array.ElementType is IArrayTypeSymbol || IsUserStruct(array.ElementType))
                    return "SystemObjectArray";
            }

            if (type.TypeKind == TypeKind.Interface || type.TypeKind == TypeKind.Class || type.TypeKind == TypeKind.TypeParameter)
                return "SystemObject";

            if (type.TypeKind == TypeKind.Delegate)
                return "SystemObject";

            if (type is INamedTypeSymbol namedType && IsUserStruct(namedType))
                return "SystemObject";

            return GetUdonTypeName(type);
        }

        private static string GetSpecialTypeName(SpecialType specialType)
        {
            string key = specialType switch
            {
                SpecialType.System_Boolean => "System.Boolean",
                SpecialType.System_Byte => "System.Byte",
                SpecialType.System_SByte => "System.SByte",
                SpecialType.System_Int16 => "System.Int16",
                SpecialType.System_UInt16 => "System.UInt16",
                SpecialType.System_Int32 => "System.Int32",
                SpecialType.System_UInt32 => "System.UInt32",
                SpecialType.System_Int64 => "System.Int64",
                SpecialType.System_UInt64 => "System.UInt64",
                SpecialType.System_Single => "System.Single",
                SpecialType.System_Double => "System.Double",
                SpecialType.System_Decimal => "System.Decimal",
                SpecialType.System_Char => "System.Char",
                SpecialType.System_String => "System.String",
                SpecialType.System_Object => "System.Object",
                SpecialType.System_Void => "System.Void",
                SpecialType.System_IntPtr => "System.IntPtr",
                SpecialType.System_UIntPtr => "System.UIntPtr",
                _ => null,
            };

            return key != null && _primitiveNames.TryGetValue(key, out string name) ? name : null;
        }

        public static string GetMetadataFullName(INamedTypeSymbol type)
        {
            List<string> nesting = new List<string>();
            INamedTypeSymbol current = type;

            while (current != null)
            {
                nesting.Insert(0, current.MetadataName);
                current = current.ContainingType;
            }

            string name = string.Join("+", nesting);
            string ns = type.ContainingNamespace != null && !type.ContainingNamespace.IsGlobalNamespace
                ? type.ContainingNamespace.ToDisplayString()
                : null;

            return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
        }

        /// <summary>
        /// Resolves the runtime <see cref="Type"/> for a Roslyn symbol using the assemblies that are
        /// currently loaded in the editor.
        /// </summary>
        public static Type ToSystemType(ITypeSymbol type)
        {
            if (type == null)
                return null;

            if (type is IArrayTypeSymbol arrayType)
            {
                Type elementType = ToSystemType(arrayType.ElementType);
                if (elementType == null)
                    return null;

                return arrayType.Rank == 1 ? elementType.MakeArrayType() : elementType.MakeArrayType(arrayType.Rank);
            }

            if (type is INamedTypeSymbol named)
            {
                string fullName = GetMetadataFullName(named);

                if (named.IsGenericType)
                {
                    fullName = fullName.Substring(0, fullName.IndexOf('`'));
                    Type genericType = FindType(fullName);
                    if (genericType == null)
                        return null;

                    Type[] arguments = new Type[named.TypeArguments.Length];
                    for (int i = 0; i < arguments.Length; i++)
                    {
                        arguments[i] = ToSystemType(named.TypeArguments[i]);
                        if (arguments[i] == null)
                            return null;
                    }

                    try
                    {
                        return genericType.MakeGenericType(arguments);
                    }
                    catch
                    {
                        return null;
                    }
                }

                return FindType(fullName);
            }

            return null;
        }

        /// <summary>
        /// The heap variable type Udon should declare for a value that the compiler or the editor
        /// writes into the heap.
        ///
        /// Reflection reports the runtime type of a <see cref="Type"/> instance
        /// (<c>System.RuntimeType</c>), and Udon's Odin serializer cannot round-trip that name. It
        /// writes the type reference into the program stream, fails to resolve it when the program is
        /// read back, and the deserialisation error cascades: every symbol in the symbol table is
        /// dropped, which surfaces at load time as
        /// <c>SerializationException: Member 'Type' was not found</c> followed by a
        /// <c>NullReferenceException</c> in <c>UdonSymbolTable</c>. Declaring the value as
        /// <c>System.Type</c> keeps it serialisable.
        /// </summary>
        public static Type GetHeapType(object value)
        {
            if (value is Type)
                return typeof(Type);

            return value?.GetType();
        }

        private static readonly Dictionary<string, Type> _typeCache = new Dictionary<string, Type>();

        private static Type FindType(string fullName)
        {
            if (_typeCache.TryGetValue(fullName, out Type cached))
                return cached;

            Type found = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                    continue;

                try
                {
                    found = assembly.GetType(fullName, false);
                }
                catch
                {
                    found = null;
                }

                if (found != null)
                    break;
            }

            _typeCache[fullName] = found;
            return found;
        }
    }

    /// <summary>
    /// Describes how a user defined struct is laid out in its <c>object[]</c> heap representation.
    /// </summary>
    internal sealed class StructLayout
    {
        public readonly INamedTypeSymbol Symbol;
        public readonly List<IFieldSymbol> Fields = new List<IFieldSymbol>();
        private readonly Dictionary<IFieldSymbol, int> _indices = new Dictionary<IFieldSymbol, int>(SymbolEqualityComparer.Default);

        public StructLayout(INamedTypeSymbol symbol)
        {
            Symbol = symbol;

            foreach (IFieldSymbol field in symbol.GetMembers().OfType<IFieldSymbol>())
            {
                if (field.IsStatic || field.IsConst)
                    continue;

                _indices[field] = Fields.Count;
                Fields.Add(field);
            }
        }

        public int GetIndex(IFieldSymbol field)
        {
            if (_indices.TryGetValue(field, out int index))
                return index;

            // Auto property backing fields are sometimes surfaced through the associated symbol.
            if (field.AssociatedSymbol is IPropertySymbol property)
            {
                foreach (IFieldSymbol candidate in Fields)
                {
                    if (candidate.AssociatedSymbol == property)
                        return _indices[candidate];
                }
            }

            // A constructed generic struct exposes fields that are distinct symbols from the ones the
            // generic method body was bound against. Layout order is still the same.
            foreach (IFieldSymbol candidate in Fields)
            {
                if (candidate.Name == field.Name)
                    return _indices[candidate];
            }

            return -1;
        }

        public override string ToString() => Symbol.Name + " (" + Fields.Count + " fields)";
    }
}
