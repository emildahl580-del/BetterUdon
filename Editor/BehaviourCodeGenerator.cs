using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BetterUdon.Editor
{
    public sealed class CompileDiagnostic
    {
        public string Message;
        public int Line;
        public int Column;

        public override string ToString() => $"({Line},{Column}): {Message}";
    }

    /// <summary>
    /// Lowers a single BetterUdonBehaviour class to Udon assembly.
    ///
    /// Values live on the Udon heap. User defined structs are lowered to <c>object[]</c>s of their
    /// fields, which gives them real value semantics on a heap that can only hold references to
    /// objects: every copy is an explicit clone, and every field access goes through the array.
    /// </summary>
    internal sealed partial class BehaviourCodeGenerator
    {
        private readonly SemanticModel _model;
        private readonly INamedTypeSymbol _behaviourType;
        private readonly AssemblyBuilder _asm = new AssemblyBuilder();
        private readonly List<CompileDiagnostic> _diagnostics = new List<CompileDiagnostic>();
        private readonly Dictionary<ISymbol, Slot> _fieldSlots = new Dictionary<ISymbol, Slot>(SymbolEqualityComparer.Default);
        private readonly Dictionary<ITypeSymbol, StructLayout> _structLayouts = new Dictionary<ITypeSymbol, StructLayout>(SymbolEqualityComparer.Default);
        private readonly Dictionary<string, Slot> _constantSlots = new Dictionary<string, Slot>();
        private readonly List<Dictionary<ISymbol, Slot>> _scopes = new List<Dictionary<ISymbol, Slot>>();
        private readonly Stack<LoopContext> _loops = new Stack<LoopContext>();
        private readonly Stack<ReturnContext> _returns = new Stack<ReturnContext>();
        private readonly HashSet<IMethodSymbol> _methodsInProgress = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        private readonly HashSet<string> _exportedEntryPoints = new HashSet<string>();

        private Slot _udonThisSlot;
        private Slot _thisSlot;
        private bool _thisIsStruct;
        private int _slotCounter;
        private int _labelCounter;

        private sealed class LoopContext
        {
            public string BreakLabel;
            public string ContinueLabel;
        }

        private sealed class ReturnContext
        {
            public Slot ReturnSlot;
            public string EndLabel;
        }

        public IReadOnlyList<CompileDiagnostic> Diagnostics => _diagnostics;

        public BehaviourCodeGenerator(SemanticModel model, INamedTypeSymbol behaviourType)
        {
            _model = model;
            _behaviourType = behaviourType;
        }

        public string TypeName => _behaviourType.ToDisplayString();

        public IReadOnlyCollection<string> ExportedEntryPoints => _exportedEntryPoints;

        /// <summary>True when at least one field is marked with <c>[BetterUdonSynced]</c>.</summary>
        public bool HasSyncedVariables { get; private set; }

        /// <summary>
        /// Forced sync mode from <c>[BetterUdonBehaviourSyncMode]</c>: 0 Any, 1 None, 2 NoVariableSync,
        /// 3 Continuous, 4 Manual.
        /// </summary>
        public int SyncModeOverride { get; private set; }

        /// <summary>
        /// True when a synced field asks for interpolation, which requires continuous synchronisation
        /// instead of the manual mode that plain synced variables use.
        /// </summary>
        public bool SyncNeedsContinuous { get; private set; }

        private readonly List<IFieldSymbol> _syncedArrayFields = new List<IFieldSymbol>();
        private IFieldSymbol _interpolatedSyncField;

        private sealed class SyncedCallback
        {
            public IFieldSymbol Field;
            public IMethodSymbol Method;
            public IPropertySymbol Property;
        }

        private readonly List<SyncedCallback> _syncedCallbacks = new List<SyncedCallback>();

        /// <summary>
        /// Structs that were used by the compiled program, used by the inspector to draw struct fields.
        /// </summary>
        public IReadOnlyDictionary<ITypeSymbol, StructLayout> StructLayouts => _structLayouts;

        public string Compile()
        {
            CreateRootSlots();
            CreateFieldSlots();
            ReadSyncModeOverride();
            ValidateSyncModeOverride();

            List<IMethodSymbol> methods = _behaviourType.GetMembers().OfType<IMethodSymbol>()
                .Where(m => !m.IsStatic && m.MethodKind == MethodKind.Ordinary && !m.IsImplicitlyDeclared)
                .ToList();

            // Recursive methods are compiled as blocks before anything can jump to them.
            PrepareRecursiveMethods(methods);
            EmitRecursiveMethodBlocks(methods);

            foreach (IMethodSymbol method in methods)
            {
                if (IsCompilerIntrinsic(method))
                    continue;

                if (IsNetworkCallable(method))
                {
                    if (IsRecursiveMethod(method))
                    {
                        AddError($"'{method.Name}' cannot be both [NetworkCallable] and [RecursiveMethod].",
                            method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax());
                        continue;
                    }

                    TryEmitNetworkCallableEntryPoint(method);
                    continue;
                }

                if (UdonRegistry.Instance.TryGetEvent(method.Name, out UdonEventDefinition eventDefinition))
                {
                    EmitEventEntryPoint(eventDefinition, method);
                }
                else if (method.DeclaredAccessibility == Accessibility.Public &&
                         method.Parameters.Length == 0 &&
                         method.ReturnsVoid &&
                         !method.IsGenericMethod)
                {
                    EmitCustomEventEntryPoint(method);
                }
            }

            // Synced structs travel through mirrors that are packed when the client serialises, and
            // OnChange callbacks run when values arrive, so those entry points always have to exist.
            if (HasSyncedStructs || _syncedCallbacks.Count > 0)
            {
                if (HasSyncedStructs && !_exportedEntryPoints.Contains("_onPreSerialization"))
                {
                    _asm.MarkEntryPoint("_onPreSerialization");
                    _exportedEntryPoints.Add("_onPreSerialization");
                    EmitPackSyncedStructs();
                    Halt();
                }

                if (!_exportedEntryPoints.Contains("_onDeserialization"))
                {
                    _asm.MarkEntryPoint("_onDeserialization");
                    _exportedEntryPoints.Add("_onDeserialization");
                    EmitUnpackSyncedStructs();
                    EmitSyncedCallbacks();
                    Halt();
                }

                if (HasSyncedStructs && _interpolatedSyncField != null)
                {
                    AddError($"'{_interpolatedSyncField.Name}' asks for interpolation, but this behaviour also " +
                             "synchronises a struct. Structs use manual sync, which cannot interpolate values.",
                             _interpolatedSyncField.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax());
                }
            }

            if (_exportedEntryPoints.Count == 0)
            {
                _asm.MarkEntryPoint("_onEnable");
                Halt();
                _exportedEntryPoints.Add("_onEnable");
            }

            // Continuous sync cannot carry arrays.
            if (SyncNeedsContinuous)
            {
                foreach (IFieldSymbol field in _syncedArrayFields)
                {
                    AddError($"'{field.Name}' is an array, and Udon cannot synchronise arrays with continuous sync. " +
                             "Use manual sync for arrays.",
                             field.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax());
                }
            }

            // Any structs that are only reachable through fields still need layouts computed so that the
            // inspector can display them.
            foreach (KeyValuePair<ISymbol, Slot> pair in _fieldSlots)
                RegisterStructLayouts(pair.Key is IFieldSymbol field ? field.Type : null);

            return _asm.BuildAssembly();
        }

        private void RegisterStructLayouts(ITypeSymbol type)
        {
            while (type is IArrayTypeSymbol array)
                type = array.ElementType;

            if (!UdonTypeSystem.IsUserStruct(type) || _structLayouts.ContainsKey(type))
                return;

            StructLayout layout = GetStructLayout(type);
            if (layout == null)
                return;

            foreach (IFieldSymbol field in layout.Fields)
                RegisterStructLayouts(field.Type);
        }

        /// <summary>
        /// All heap symbols that need an explicit initial value after assembly. This includes the
        /// compiler generated constants, which would otherwise read as the type's default value.
        /// </summary>
        public Dictionary<string, object> ComputeHeapInitialValues()
        {
            Dictionary<string, object> values = new Dictionary<string, object>();

            foreach (Slot slot in _asm.Slots)
            {
                if (slot.InitialIsThis || slot.InitialValue == null)
                    continue;

                values[slot.Name] = slot.InitialValue;
            }

            return values;
        }

        public IReadOnlyList<Slot> Slots => _asm.Slots;

        /// <summary>
        /// Heap size the assembler needs: one slot per declared symbol plus one per unique extern.
        /// </summary>
        public uint HeapSize => _asm.GetHeapSize();

        #region Setup

        private void CreateRootSlots()
        {
            // "this" is resolved by Udon at load time. Only GameObject, Transform, UdonBehaviour and
            // System.Object are supported reference types, so the receiver is declared as the behaviour
            // type (which also satisfies externs declared on IUdonEventReceiver through casting).
            _udonThisSlot = _asm.AddSlot("__this", "VRCUdonUdonBehaviour", null);
            _udonThisSlot.InitialIsThis = true;

            _thisSlot = _udonThisSlot;
            _thisIsStruct = false;

            _asm.AddSlot("__gameObject", "UnityEngineGameObject", null).InitialIsThis = true;
            _asm.AddSlot("__transform", "UnityEngineTransform", null).InitialIsThis = true;

            // Lets GetComponent<T>() on another object check that the behaviour it found is really T.
            Slot typeMarker = _asm.AddSlot(TypeMarkerSymbol, "SystemString", _stringType);
            typeMarker.InitialValue = _behaviourType.ToDisplayString();
        }

        private void CreateFieldSlots()
        {
            foreach (IFieldSymbol field in GetSerializableFields(_behaviourType))
            {
                string syncMode = GetSyncMode(field);
                bool structSync = syncMode != null && IsSyncedStructField(field);
                string storageType = UdonTypeSystem.GetStorageTypeName(field.Type);
                bool exported = IsExported(field);

                if (syncMode != null)
                {
                    ValidateSyncedField(field, syncMode);
                    HasSyncedVariables = true;

                    if (syncMode != "none")
                    {
                        SyncNeedsContinuous = true;
                        _interpolatedSyncField = field;
                    }

                    if (field.Type is IArrayTypeSymbol && !structSync)
                        _syncedArrayFields.Add(field);

                    // Udon serialises a synced value by its symbol type, so a synced field cannot use
                    // the SystemObject slot that strings and other reference types normally get. Structs
                    // keep the object[] slot: their values travel through the mirrors instead.
                    if (!structSync)
                        storageType = UdonTypeSystem.GetUdonTypeName(field.Type);
                }

                Slot slot = _asm.AddSlot(GetSlotName(field), storageType, field.Type);
                slot.Exported = exported;
                slot.SyncMode = structSync ? null : syncMode;
                slot.InitialValue = EvaluateFieldInitializer(field);

                _fieldSlots.Add(field, slot);

                if (slot.Exported)
                    _asm.AddExport(slot.Name);

                if (structSync)
                {
                    if (!exported)
                        AddError($"'{field.Name}' is marked [BetterUdonSynced] but is not a public variable. " +
                                 "Synced fields must be public and must not be hidden.", field.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax());

                    TryCreateStructMirror(field, slot, syncMode);
                }

                string onChange = GetChangeCallback(field);

                if (!string.IsNullOrEmpty(onChange))
                {
                    if (syncMode == null)
                        AddError($"'{field.Name}' sets OnChange but is not synced. Add [BetterUdonSynced].", field.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax());
                    else
                        RegisterChangeCallback(field, onChange);
                }
            }
        }

        private static string GetChangeCallback(IFieldSymbol field)
        {
            AttributeData attribute = field.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.ToDisplayString() == "BetterUdon.BetterUdonSyncedAttribute");

            if (attribute == null)
                return null;

            foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
            {
                if (argument.Key == "OnChange" && argument.Value.Value is string name && !string.IsNullOrEmpty(name))
                    return name;
            }

            return null;
        }

        /// <summary>
        /// Resolves the method or property setter that <c>OnChange</c> names, mirroring UdonSharp's
        /// <c>FieldChangeCallback</c>.
        /// </summary>
        private void RegisterChangeCallback(IFieldSymbol field, string name)
        {
            SyntaxNode location = field.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
            IPropertySymbol property = _behaviourType.GetMembers(name).OfType<IPropertySymbol>().FirstOrDefault();

            if (property != null)
            {
                if (property.Type is null || !SymbolEqualityComparer.Default.Equals(property.Type, field.Type))
                {
                    AddError($"The OnChange property '{name}' must have the same type as '{field.Name}'.", location);
                    return;
                }

                if (!AccessorHasBody(property.SetMethod))
                {
                    AddError($"The OnChange property '{name}' needs a setter body.", location);
                    return;
                }

                _syncedCallbacks.Add(new SyncedCallback { Field = field, Property = property });
                return;
            }

            IMethodSymbol method = _behaviourType.GetMembers(name).OfType<IMethodSymbol>()
                .FirstOrDefault(m => m.Parameters.Length == 0 && !m.IsStatic);

            if (method == null)
            {
                AddError($"OnChange '{name}' was not found on '{_behaviourType.Name}'. It must be a parameterless method or a property with a setter.", location);
                return;
            }

            _syncedCallbacks.Add(new SyncedCallback { Field = field, Method = method });
        }

        private static IEnumerable<IFieldSymbol> GetSerializableFields(INamedTypeSymbol type)
        {
            List<INamedTypeSymbol> hierarchy = new List<INamedTypeSymbol>();
            for (INamedTypeSymbol current = type; current != null; current = current.BaseType)
                hierarchy.Insert(0, current);

            foreach (INamedTypeSymbol current in hierarchy)
            {
                string currentName = current.ToDisplayString();
                if (currentName == "BetterUdon.BetterUdonBehaviour" ||
                    current.SpecialType == SpecialType.System_Object ||
                    currentName.StartsWith("UnityEngine.", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (IFieldSymbol field in current.GetMembers().OfType<IFieldSymbol>())
                {
                    if (field.IsStatic || field.IsConst)
                        continue;

                    // Auto property backing fields are implicitly declared; keep them so that
                    // properties declared on the behaviour become heap variables.
                    if (field.IsImplicitlyDeclared && !(field.AssociatedSymbol is IPropertySymbol))
                        continue;

                    yield return field;
                }
            }
        }

        /// <summary>
        /// The heap symbol name for a field. Auto property backing fields are exposed under the
        /// property's name so they can be used as public variables.
        /// </summary>
        private static string GetSlotName(IFieldSymbol field)
        {
            return field.AssociatedSymbol is IPropertySymbol property ? property.Name : field.Name;
        }

        private static bool IsExported(IFieldSymbol field)
        {
            if (HasAttribute(field, "BetterUdon.BetterUdonNoExport"))
                return false;

            // Auto property backing fields are not Unity serialised, so they cannot be edited through
            // the component's inspector. They still work as heap variables.
            if (field.AssociatedSymbol != null)
                return false;

            // List<T> lives on the heap in a compiler specific layout, so it cannot round-trip through
            // the inspector either.
            if (IsListType(field.Type, out _))
                return false;

            if (field.DeclaredAccessibility != Accessibility.Public)
                return false;

            if (HasAttribute(field, "UnityEngine.HideInInspector"))
                return false;

            return true;
        }

        private static string GetSyncMode(IFieldSymbol field)
        {
            AttributeData attribute = field.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.ToDisplayString() == "BetterUdon.BetterUdonSyncedAttribute");

            if (attribute == null)
                return null;

            int mode = 0;
            if (attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is int value)
                mode = value;

            switch (mode)
            {
                case 1: return "linear";
                case 2: return "smooth";
                default: return "none";
            }
        }

        /// <summary>
        /// Udon only synchronises exported variables and only supports a fixed set of types; both are
        /// checked here so the problem is a compile error instead of a silent value that never
        /// arrives (or a world build that refuses to upload).
        /// </summary>
        private void ValidateSyncedField(IFieldSymbol field, string syncMode)
        {
            SyntaxNode location = field.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();

            if (!IsExported(field))
            {
                AddError($"'{field.Name}' is marked [BetterUdonSynced] but is not a public variable. " +
                         "Synced fields must be public and must not be hidden, auto properties or List<T>.", location);
                return;
            }

            // Structs are handled by their mirrors; the leaves are validated while the mirror is built.
            if (IsSyncedStructField(field))
            {
                if (syncMode != "none")
                {
                    AddError($"'{field.Name}' is a struct, and Udon can only interpolate single values. " +
                             "Use [BetterUdonSynced] without an interpolation mode.", location);
                }

                return;
            }

            Type type = UdonTypeSystem.ToSystemType(field.Type);

            if (type == null)
            {
                AddError($"'{field.Name}' has type '{field.Type.ToDisplayString()}', which could not be resolved.", location);
                return;
            }

            // User enums are lowered to their underlying integer, so that is what gets synced.
            if (field.Type.TypeKind == TypeKind.Enum && UdonTypeSystem.IsUserEnum(field.Type))
            {
                type = Enum.GetUnderlyingType(type);
            }
            else if (field.Type is IArrayTypeSymbol arrayType &&
                     arrayType.ElementType.TypeKind == TypeKind.Enum &&
                     UdonTypeSystem.IsUserEnum(arrayType.ElementType))
            {
                type = Enum.GetUnderlyingType(type.GetElementType()).MakeArrayType();
            }

            if (!VRC.Udon.UdonNetworkTypes.CanSync(type))
            {
                AddError($"'{field.Name}' has type '{field.Type.ToDisplayString()}', which Udon cannot synchronise.", location);
                return;
            }

            if (syncMode == "linear" && !VRC.Udon.UdonNetworkTypes.CanSyncLinear(type))
            {
                AddError($"'{field.Name}' cannot use linear interpolation: '{field.Type.ToDisplayString()}' is not supported.", location);
            }
            else if (syncMode == "smooth" && !VRC.Udon.UdonNetworkTypes.CanSyncSmooth(type))
            {
                AddError($"'{field.Name}' cannot use smooth interpolation: '{field.Type.ToDisplayString()}' is not supported.", location);
            }
        }

        /// <summary>
        /// Reads <c>[BetterUdonBehaviourSyncMode]</c> from the behaviour or a base behaviour.
        /// </summary>
        private void ReadSyncModeOverride()
        {
            for (INamedTypeSymbol current = _behaviourType; current != null; current = current.BaseType)
            {
                AttributeData attribute = current.GetAttributes().FirstOrDefault(a =>
                    a.AttributeClass?.ToDisplayString() == "BetterUdon.BetterUdonBehaviourSyncModeAttribute");

                if (attribute != null && attribute.ConstructorArguments.Length > 0 &&
                    attribute.ConstructorArguments[0].Value is int value)
                {
                    SyncModeOverride = value;
                    return;
                }
            }
        }

        /// <summary>
        /// Checks that the forced mode is compatible with the synced fields, mirroring UdonSharp's
        /// validation.
        /// </summary>
        private void ValidateSyncModeOverride()
        {
            SyntaxNode location = _behaviourType.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();

            switch (SyncModeOverride)
            {
                case 1: // None
                    if (HasSyncedVariables)
                        AddError("This behaviour is [BetterUdonBehaviourSyncMode(None)], so it cannot have synced variables.", location);
                    SyncNeedsContinuous = false;
                    break;

                case 2: // NoVariableSync
                    if (HasSyncedVariables)
                        AddError("This behaviour is [BetterUdonBehaviourSyncMode(NoVariableSync)], so it cannot have synced variables.", location);
                    // Network events still need a sync mode.
                    SyncNeedsContinuous = true;
                    break;

                case 3: // Continuous
                    if (HasSyncedStructs)
                        AddError("Structs need manual sync; a Continuous behaviour cannot synchronise them.", location);
                    SyncNeedsContinuous = true;
                    break;

                case 4: // Manual
                    if (_interpolatedSyncField != null)
                    {
                        AddError($"'{_interpolatedSyncField.Name}' asks for interpolation, which needs Continuous sync. " +
                                 "Switch the behaviour to Continuous or drop the interpolation mode.",
                                 _interpolatedSyncField.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax());
                    }

                    SyncNeedsContinuous = false;
                    break;
            }
        }

        private static bool HasAttribute(ISymbol symbol, string attributeTypeName)
        {
            return symbol.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == attributeTypeName);
        }

        private Slot NewSlot(ITypeSymbol type, string hint = null)
        {
            string name = "__" + UdonTypeSystem.Sanitize(hint ?? "tmp") + "_" + (_slotCounter++);
            return _asm.AddSlot(name, UdonTypeSystem.GetStorageTypeName(type), type);
        }

        private ITypeSymbol MakeArrayType(ITypeSymbol elementType)
        {
            return _model.Compilation.CreateArrayTypeSymbol(elementType);
        }

        private Slot NewObjectArraySlot(int size, ITypeSymbol type = null)
        {
            Slot slot = NewSlot(type, "struct");
            CreateObjectArray(slot, size);
            return slot;
        }

        private string NewLabel(string hint)
        {
            return "__" + UdonTypeSystem.Sanitize(hint) + "_" + (_labelCounter++);
        }

        #endregion

        #region Constants and conversions

        private Slot ConstantSlot(ITypeSymbol type, object value, string udonType = null)
        {
            udonType = udonType ?? UdonTypeSystem.GetStorageTypeName(type);
            string key = udonType + "|" + DescribeConstant(value);

            if (_constantSlots.TryGetValue(key, out Slot existing))
                return existing;

            Slot slot = _asm.AddSlot("__const_" + (_constantSlots.Count), udonType, type);
            slot.InitialValue = value;
            _constantSlots.Add(key, slot);

            return slot;
        }

        private static string DescribeConstant(object value)
        {
            if (value == null)
                return "null";

            if (value is float f)
                return "f" + f.ToString("R", CultureInfo.InvariantCulture);

            if (value is double d)
                return "d" + d.ToString("R", CultureInfo.InvariantCulture);

            if (value is Array array)
            {
                // ToString() on arrays is not unique, and struct constants are object[]s, so the
                // contents have to describe the value.
                System.Text.StringBuilder builder = new System.Text.StringBuilder();
                builder.Append(value.GetType().Name).Append('[');

                foreach (object item in array)
                    builder.Append(DescribeConstant(item)).Append(',');

                return builder.Append(']').ToString();
            }

            return value.GetType().Name + ":" + Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private Slot EmitConstant(Slot target, object rawValue, ITypeSymbol type)
        {
            object value = MaterializeConstant(rawValue, type);
            Slot constant = ConstantSlot(type, value);

            if (target != null && target != constant)
                _asm.Copy(constant, target);

            return target ?? constant;
        }

        private object MaterializeConstant(object rawValue, ITypeSymbol type)
        {
            if (rawValue == null)
                return null;

            if (type != null && type.TypeKind == TypeKind.Enum)
            {
                // User enums are stored as their underlying integer, so the constant must not be boxed
                // as an enum value.
                if (UdonTypeSystem.IsUserEnum(type))
                    return rawValue;

                Type enumType = UdonTypeSystem.ToSystemType(type);
                if (enumType != null)
                    return Enum.ToObject(enumType, rawValue);

                return rawValue;
            }

            if (type != null && type.SpecialType == SpecialType.System_Char && rawValue is int charValue)
                return (char)charValue;

            return rawValue;
        }

        /// <summary>
        /// Converts a value so it can be used where <paramref name="targetType"/> is expected.
        /// </summary>
        private Slot ConvertTo(Slot value, ITypeSymbol targetType, bool cloneStructs = true)
        {
            if (value == null || targetType == null)
                return value;

            ITypeSymbol sourceType = value.Type;

            if (sourceType != null && SymbolEqualityComparer.Default.Equals(sourceType, targetType))
                return cloneStructs ? MaybeCloneStruct(value) : value;

            if (UdonTypeSystem.IsUserStruct(targetType) && sourceType != null && UdonTypeSystem.IsUserStruct(sourceType))
                return cloneStructs ? MaybeCloneStruct(value) : value;

            if (IsIntegerLike(sourceType) && targetType.TypeKind == TypeKind.Enum)
                return Rebox(value, targetType);

            if (sourceType != null && sourceType.TypeKind == TypeKind.Enum && IsIntegerLike(targetType))
                return Rebox(value, targetType);

            if (sourceType != null && sourceType.TypeKind == TypeKind.Enum && targetType.TypeKind == TypeKind.Enum)
                return Rebox(value, targetType);

            if (IsReferenceType(sourceType) && IsReferenceType(targetType))
                return value;

            if (sourceType != null &&
                (sourceType.SpecialType == SpecialType.System_Object || targetType.SpecialType == SpecialType.System_Object))
                return value;

            if (IsIntegral(sourceType) && IsIntegral(targetType))
                return EmitConversion(value, sourceType, targetType);

            if (IsIntegral(sourceType) && IsFloating(targetType))
                return EmitConversion(value, sourceType, targetType);

            if (IsFloating(sourceType) && IsFloating(targetType))
                return EmitConversion(value, sourceType, targetType);

            if (IsFloating(sourceType) && IsIntegral(targetType))
                return EmitConversion(value, sourceType, targetType);

            if (sourceType != null && sourceType.SpecialType == SpecialType.System_Char && IsIntegral(targetType))
                return EmitConversion(value, sourceType, targetType);

            if (IsIntegral(sourceType) && targetType.SpecialType == SpecialType.System_Char)
                return EmitConversion(value, sourceType, targetType);

            if (targetType.SpecialType == SpecialType.System_String && sourceType != null &&
                sourceType.SpecialType != SpecialType.System_String)
                return EmitToString(value, sourceType, targetType);

            return value;
        }

        private Slot Rebox(Slot value, ITypeSymbol targetType)
        {
            Slot result = NewSlot(targetType);
            _asm.Copy(value, result);
            return result;
        }

        private Slot EmitConversion(Slot value, ITypeSymbol sourceType, ITypeSymbol targetType)
        {
            Slot result = NewSlot(targetType, "conv");
            string source = UdonTypeSystem.GetUdonTypeName(sourceType);
            string target = UdonTypeSystem.GetUdonTypeName(targetType);
            string signature = $"SystemConvert.__To{GetClrName(targetType)}__{source}__{target}";

            _asm.Push(value);
            _asm.Push(result);
            Extern(signature, "numeric conversion");

            return result;
        }

        private Slot EmitToString(Slot value, ITypeSymbol sourceType, ITypeSymbol targetType)
        {
            Slot result = NewSlot(targetType, "str");
            _asm.Push(value);
            _asm.Push(result);
            Extern("SystemConvert.__ToString__SystemObject__SystemString", "ToString conversion");
            return result;
        }

        private static string GetClrName(ITypeSymbol type)
        {
            switch (type.SpecialType)
            {
                case SpecialType.System_Boolean: return "Boolean";
                case SpecialType.System_Byte: return "Byte";
                case SpecialType.System_SByte: return "SByte";
                case SpecialType.System_Int16: return "Int16";
                case SpecialType.System_UInt16: return "UInt16";
                case SpecialType.System_Int32: return "Int32";
                case SpecialType.System_UInt32: return "UInt32";
                case SpecialType.System_Int64: return "Int64";
                case SpecialType.System_UInt64: return "UInt64";
                case SpecialType.System_Single: return "Single";
                case SpecialType.System_Double: return "Double";
                case SpecialType.System_Char: return "Char";
                case SpecialType.System_String: return "String";
                case SpecialType.System_Object: return "Object";
                default: return UdonTypeSystem.GetUdonTypeName(type);
            }
        }

        private static bool IsIntegral(ITypeSymbol type)
        {
            if (type == null)
                return false;

            switch (type.SpecialType)
            {
                case SpecialType.System_Byte:
                case SpecialType.System_SByte:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                    return true;
            }

            return type.TypeKind == TypeKind.Enum;
        }

        private static bool IsIntegerLike(ITypeSymbol type) => IsIntegral(type);

        private static bool IsFloating(ITypeSymbol type)
        {
            return type != null &&
                   (type.SpecialType == SpecialType.System_Single || type.SpecialType == SpecialType.System_Double);
        }

        private static bool IsReferenceType(ITypeSymbol type)
        {
            return type != null && (type.IsReferenceType || type.TypeKind == TypeKind.Interface || type.TypeKind == TypeKind.Array);
        }

        #endregion

        #region Diagnostics

        private void ValidateExtern(string signature, string context)
        {
            if (UdonRegistry.Instance.IsAvailable && !UdonRegistry.Instance.HasExtern(signature))
                AddError($"{context}: Udon does not expose the extern '{signature}'.");
        }

        private void AddError(string message, SyntaxNode node = null)
        {
            FileLinePositionSpan span = default;
            if (node != null)
                span = node.GetLocation().GetLineSpan();

            int line = span.IsValid ? span.StartLinePosition.Line + 1 : 0;
            int column = span.IsValid ? span.StartLinePosition.Character + 1 : 0;

            foreach (CompileDiagnostic existing in _diagnostics)
            {
                if (existing.Line == line && existing.Column == column && existing.Message == message)
                    return;
            }

            _diagnostics.Add(new CompileDiagnostic
            {
                Message = message,
                Line = line,
                Column = column,
            });
        }

        #endregion
    }
}
