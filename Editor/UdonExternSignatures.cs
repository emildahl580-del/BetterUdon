using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using VRC.Udon.Editor;
using VRC.Udon.EditorBindings;
using VRC.Udon.Graph;

namespace BetterUdon.Editor
{
    /// <summary>
    /// Knows how to build Udon extern signatures from C# symbols, and exposes the list of externs that
    /// the currently installed Udon runtime actually supports.
    /// </summary>
    internal static class UdonExternSignatures
    {
        public static string GetArrayTypeName(ITypeSymbol arrayType)
        {
            if (arrayType is IArrayTypeSymbol array)
            {
                ITypeSymbol elementType = array.ElementType;
                Type systemType = UdonTypeSystem.ToSystemType(elementType);

                if (systemType != null && typeof(UnityEngine.Object).IsAssignableFrom(systemType))
                    return "SystemObjectArray";

                if (elementType is IArrayTypeSymbol || UdonTypeSystem.IsUserStruct(elementType))
                    return "SystemObjectArray";
            }

            return UdonTypeSystem.GetUdonTypeName(arrayType);
        }

        public static string GetArrayElementTypeName(ITypeSymbol arrayType)
        {
            if (arrayType is IArrayTypeSymbol array)
            {
                ITypeSymbol elementType = array.ElementType;
                Type systemType = UdonTypeSystem.ToSystemType(elementType);

                if (systemType != null && typeof(UnityEngine.Object).IsAssignableFrom(systemType))
                    return "SystemObject";

                if (elementType is IArrayTypeSymbol || UdonTypeSystem.IsUserStruct(elementType))
                    return "SystemObject";
            }

            return UdonTypeSystem.GetUdonTypeName(arrayType is IArrayTypeSymbol a ? a.ElementType : arrayType);
        }

        public static string ArrayConstructor(ITypeSymbol arrayType)
        {
            string arrayTypeName = GetArrayTypeName(arrayType);
            return $"{arrayTypeName}.__ctor__SystemInt32__{arrayTypeName}";
        }

        public static string ArrayGet(ITypeSymbol arrayType)
        {
            return $"{GetArrayTypeName(arrayType)}.__Get__SystemInt32__{GetArrayElementTypeName(arrayType)}";
        }

        public static string ArraySet(ITypeSymbol arrayType)
        {
            return $"{GetArrayTypeName(arrayType)}.__Set__SystemInt32_{GetArrayElementTypeName(arrayType)}__SystemVoid";
        }

        public static string GetContainingTypeName(ITypeSymbol type)
        {
            return UdonTypeSystem.GetUdonTypeName(RemapBaseType(type));
        }

        public static ITypeSymbol RemapBaseType(ITypeSymbol type)
        {
            return type;
        }

        public static string Method(IMethodSymbol method)
        {
            string typeName = GetContainingTypeName(method.ContainingType);
            bool isConstructor = method.MethodKind == MethodKind.Constructor || method.MethodKind == MethodKind.StaticConstructor;

            string methodName = isConstructor ? "__ctor" : "__" + method.Name.Trim('_').TrimStart('.');

            string paramStr = string.Empty;
            if (method.Parameters.Length > 0)
            {
                paramStr = "__";
                for (int i = 0; i < method.Parameters.Length; i++)
                {
                    if (i > 0)
                        paramStr += "_";

                    paramStr += UdonTypeSystem.GetUdonTypeName(method.Parameters[i].Type);

                    if (method.Parameters[i].RefKind != RefKind.None)
                        paramStr += "Ref";
                }
            }
            else if (isConstructor)
            {
                paramStr = "__";
            }

            string returnStr = isConstructor
                ? "__" + UdonTypeSystem.GetUdonTypeName(method.ContainingType)
                : "__" + UdonTypeSystem.GetUdonTypeName(method.ReturnType);

            return $"{typeName}.{methodName}{paramStr}{returnStr}";
        }

        public static string FieldGet(IFieldSymbol field)
        {
            string typeName = GetContainingTypeName(field.ContainingType);
            string fieldName = field.Name.Trim('_');
            return $"{typeName}.__get_{fieldName}__{UdonTypeSystem.GetUdonTypeName(field.Type)}";
        }

        public static string FieldSet(IFieldSymbol field)
        {
            string typeName = GetContainingTypeName(field.ContainingType);
            string fieldName = field.Name.Trim('_');
            return $"{typeName}.__set_{fieldName}__{UdonTypeSystem.GetUdonTypeName(field.Type)}";
        }

        public static string PropertyGet(IPropertySymbol property)
        {
            string typeName = GetContainingTypeName(property.ContainingType);
            return $"{typeName}.__get_{property.Name}__{UdonTypeSystem.GetUdonTypeName(property.Type)}";
        }

        public static string PropertySet(IPropertySymbol property)
        {
            string typeName = GetContainingTypeName(property.ContainingType);

            // Property setters are registered with an explicit void return.
            return $"{typeName}.__set_{property.Name}__{UdonTypeSystem.GetUdonTypeName(property.Type)}__SystemVoid";
        }

        /// <summary>
        /// Builds the extern for a C# operator. Udon only has binary operators, so unary operators
        /// repeat the operand.
        /// </summary>
        public static string BuiltinOperator(ITypeSymbol containingType, string roslynName, ITypeSymbol operandType, ITypeSymbol returnType, bool unary)
        {
            if (containingType == null || operandType == null || returnType == null)
                return null;

            // String concatenation is special cased by Udon to go through SystemString.Concat.
            if (containingType.SpecialType == SpecialType.System_String && roslynName == "op_Addition")
                return "SystemString.__Concat__SystemObject_SystemObject__SystemString";

            string udonName = TranslateOperatorName(roslynName, returnType);
            if (udonName == null)
                return null;

            string typeName = GetContainingTypeName(containingType);
            string operandName = UdonTypeSystem.GetUdonTypeName(operandType);

            // Udon exposes unary operators with a single operand, e.g.
            // SystemInt32.__op_UnaryMinus__SystemInt32__SystemInt32
            string paramStr = unary ? $"__{operandName}" : $"__{operandName}_{operandName}";
            string returnStr = "__" + UdonTypeSystem.GetUdonTypeName(returnType);

            return $"{typeName}.__{udonName}{paramStr}{returnStr}";
        }

        public static string Operator(IMethodSymbol operatorMethod)
        {
            return BuiltinOperator(
                operatorMethod.ContainingType,
                operatorMethod.Name,
                operatorMethod.Parameters.Length > 0 ? operatorMethod.Parameters[0].Type : operatorMethod.ReturnType,
                operatorMethod.ReturnType,
                operatorMethod.Parameters.Length == 1);
        }

        private static string TranslateOperatorName(string roslynName, ITypeSymbol returnType)
        {
            switch (roslynName)
            {
                case "op_Addition": return "op_Addition";
                case "op_Subtraction": return "op_Subtraction";
                case "op_Multiply": return "op_Multiplication";
                case "op_Division": return "op_Division";
                case "op_Modulus": return "op_Remainder";
                case "op_LeftShift": return "op_LeftShift";
                case "op_RightShift": return "op_RightShift";
                case "op_BitwiseAnd": return "op_LogicalAnd";
                case "op_BitwiseOr": return "op_LogicalOr";
                case "op_ExclusiveOr": return "op_LogicalXor";
                case "op_Equality": return "op_Equality";
                case "op_Inequality": return "op_Inequality";
                case "op_GreaterThan": return "op_GreaterThan";
                case "op_GreaterThanOrEqual": return "op_GreaterThanOrEqual";
                case "op_LessThan": return "op_LessThan";
                case "op_LessThanOrEqual": return "op_LessThanOrEqual";
                case "op_OnesComplement": return "op_OnesComplement";
                case "op_LogicalNot": return "op_UnaryNegation";
                case "op_UnaryNegation":
                    return returnType.SpecialType == SpecialType.System_Boolean ? "op_UnaryNegation" : "op_UnaryMinus";
                default: return null;
            }
        }
    }

    internal sealed class UdonEventDefinition
    {
        public string UdonName;
        public string[] ArgumentSymbols;
        public ITypeSymbol[] ArgumentTypes;
    }

    /// <summary>
    /// Lazily built view over the Udon node definitions, used to validate externs and to discover
    /// event entry point names.
    /// </summary>
    internal sealed class UdonRegistry
    {
        private static UdonRegistry _instance;

        public static UdonRegistry Instance => _instance ?? (_instance = new UdonRegistry());

        private readonly HashSet<string> _definitions = new HashSet<string>();
        private readonly Dictionary<string, UdonEventDefinition> _events = new Dictionary<string, UdonEventDefinition>();

        public bool IsAvailable => _definitions.Count > 0;

        public IEnumerable<string> Definitions => _definitions;

        public bool HasExtern(string signature) => _definitions.Count == 0 || _definitions.Contains(signature);

        public bool TryGetEvent(string methodName, out UdonEventDefinition definition)
        {
            return _events.TryGetValue(methodName, out definition);
        }

        private UdonRegistry()
        {
            try
            {
                UdonEditorManager.Instance.GetNodeRegistries();
                UdonEditorInterface editorInterface = new UdonEditorInterface();

                foreach (UdonNodeDefinition definition in editorInterface.GetNodeDefinitions())
                {
                    if (!string.IsNullOrEmpty(definition.fullName))
                        _definitions.Add(definition.fullName);
                }

                foreach (UdonNodeDefinition definition in editorInterface.GetNodeDefinitions("Event_"))
                {
                    if (definition.fullName == "Event_Custom")
                        continue;

                    string eventName = definition.fullName.Substring("Event_".Length);
                    if (_events.ContainsKey(eventName))
                        continue;

                    char[] lowered = eventName.ToCharArray();
                    lowered[0] = char.ToLowerInvariant(lowered[0]);
                    string loweredName = new string(lowered);

                    var outputs = definition.Outputs.ToList();
                    UdonEventDefinition eventDefinition = new UdonEventDefinition
                    {
                        UdonName = "_" + loweredName,
                        ArgumentSymbols = new string[outputs.Count],
                        ArgumentTypes = new ITypeSymbol[outputs.Count],
                    };

                    for (int i = 0; i < outputs.Count; i++)
                    {
                        string outputName = outputs[i].name;
                        string capitalized = outputName.Length > 0
                            ? char.ToUpperInvariant(outputName[0]) + outputName.Substring(1)
                            : string.Empty;

                        eventDefinition.ArgumentSymbols[i] = loweredName + capitalized;
                    }

                    _events.Add(eventName, eventDefinition);
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[BetterUdon] Failed to read the Udon node definitions, extern validation is disabled.\n{e}");
            }
        }
    }
}
