using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BetterUdon.Editor
{
    /// <summary>
    /// Network sync for user structs.
    ///
    /// Udon's serialiser only understands a fixed set of types: primitives, <c>string</c>, the
    /// Unity vector/quaternion/colour types and arrays of those. A user struct is an
    /// <c>object[]</c> of its fields, so it cannot be a synced variable itself. Instead the compiler
    /// declares a hidden synced variable per leaf field ("mirror"): one scalar per field for a struct,
    /// one array per field plus a length for a struct array. The struct is packed into its mirrors just
    /// before the client serialises (<c>OnPreSerialization</c>) and unpacked when values arrive
    /// (<c>OnDeserialization</c>), so user code only ever writes plain C#.
    /// </summary>
    internal sealed partial class BehaviourCodeGenerator
    {
        private sealed class SyncLeaf
        {
            public string Name;
            public ITypeSymbol Type;              // scalar type of the field
            public ITypeSymbol SlotType;          // same as Type, or Type[] for a struct array
            public List<int> Path = new List<int>(); // indices through nested struct layouts
            public Slot Slot;
        }

        private sealed class SyncedStructMirror
        {
            public IFieldSymbol Field;
            public Slot FieldSlot;
            public ITypeSymbol StructType;
            public bool IsArray;
            public ITypeSymbol ArrayType;
            public Slot LengthSlot;
            public readonly List<SyncLeaf> Leaves = new List<SyncLeaf>();
        }

        private readonly List<SyncedStructMirror> _syncMirrors = new List<SyncedStructMirror>();

        public bool HasSyncedStructs => _syncMirrors.Count > 0;

        /// <summary>True when the field needs mirror variables instead of being synced directly.</summary>
        private static bool IsSyncedStructField(IFieldSymbol field)
        {
            ITypeSymbol type = field.Type is IArrayTypeSymbol array ? array.ElementType : field.Type;
            return UdonTypeSystem.IsUserStruct(type);
        }

        /// <summary>
        /// Declares the mirrored synced variables for a struct field. Returns false for fields that can
        /// be synced directly.
        /// </summary>
        private bool TryCreateStructMirror(IFieldSymbol field, Slot fieldSlot, string syncMode)
        {
            if (!IsSyncedStructField(field))
                return false;

            SyntaxNode location = field.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
            IArrayTypeSymbol arrayType = field.Type as IArrayTypeSymbol;
            ITypeSymbol structType = arrayType != null ? arrayType.ElementType : field.Type;

            if (syncMode != "none")
            {
                AddError($"'{field.Name}' is a struct, and Udon can only interpolate single values. " +
                         "Use [BetterUdonSynced] without an interpolation mode.", location);
                return true;
            }

            if (!UdonTypeSystem.IsUserStruct(structType))
                return false;

            SyncedStructMirror mirror = new SyncedStructMirror
            {
                Field = field,
                FieldSlot = fieldSlot,
                StructType = structType,
                IsArray = arrayType != null,
                ArrayType = arrayType,
            };

            if (mirror.IsArray)
                mirror.LengthSlot = AddSyncMirrorSlot("__sync_" + field.Name + "_length", _intType);

            CollectSyncLeaves(structType, mirror, "__sync_" + field.Name, new List<int>(), location);
            _syncMirrors.Add(mirror);
            return true;
        }

        private void CollectSyncLeaves(ITypeSymbol structType, SyncedStructMirror mirror, string prefix,
            List<int> path, SyntaxNode location)
        {
            StructLayout layout = GetStructLayout(structType);

            if (layout == null)
            {
                AddError($"Could not work out the layout of '{structType.ToDisplayString()}'.", location);
                return;
            }

            for (int i = 0; i < layout.Fields.Count; i++)
            {
                IFieldSymbol field = layout.Fields[i];
                string name = prefix + "_" + field.Name;

                if (UdonTypeSystem.IsUserStruct(field.Type))
                {
                    path.Add(i);
                    CollectSyncLeaves(field.Type, mirror, name, path, location);
                    path.RemoveAt(path.Count - 1);
                    continue;
                }

                ITypeSymbol leafType = GetSyncLeafType(field.Type);

                if (leafType == null)
                {
                    AddError($"'{structType.Name}.{field.Name}' has type '{field.Type.ToDisplayString()}', " +
                             "which Udon cannot synchronise.", location);
                    continue;
                }

                SyncLeaf leaf = new SyncLeaf
                {
                    Name = name,
                    Type = leafType,
                    SlotType = mirror.IsArray ? _model.Compilation.CreateArrayTypeSymbol(leafType) : leafType,
                    Path = new List<int>(path) { i },
                };

                leaf.Slot = AddSyncMirrorSlot(name, leaf.SlotType);
                mirror.Leaves.Add(leaf);
            }
        }

        private ITypeSymbol GetSyncLeafType(ITypeSymbol type)
        {
            if (type.TypeKind == TypeKind.Enum && UdonTypeSystem.IsUserEnum(type))
                type = ((INamedTypeSymbol)type).EnumUnderlyingType;

            Type systemType = UdonTypeSystem.ToSystemType(type);

            if (systemType == null || !VRC.Udon.UdonNetworkTypes.CanSync(systemType))
                return null;

            return type;
        }

        private Slot AddSyncMirrorSlot(string name, ITypeSymbol type)
        {
            Slot slot = _asm.AddSlot(name, UdonTypeSystem.GetUdonTypeName(type), type);
            slot.Exported = true;
            slot.SyncMode = "none";
            _asm.AddExport(name);
            return slot;
        }

        #region Packing

        /// <summary>Copies every synced struct into its mirror variables.</summary>
        private void EmitPackSyncedStructs()
        {
            foreach (SyncedStructMirror mirror in _syncMirrors)
            {
                if (mirror.IsArray)
                    EmitPackArray(mirror);
                else
                    EmitPackStruct(mirror);
            }
        }

        private void EmitPackStruct(SyncedStructMirror mirror)
        {
            foreach (SyncLeaf leaf in mirror.Leaves)
            {
                Slot value = NewSlot(leaf.Type, "syncValue");
                EmitReadStructPath(mirror.FieldSlot, leaf.Path, mirror.StructType, value);
                Copy(ConvertTo(value, leaf.Type), leaf.Slot);
            }
        }

        private void EmitPackArray(SyncedStructMirror mirror)
        {
            Slot length = NewSlot(_intType, "syncLength");
            Copy(IntConstant(0), length);

            // A null array packs as an empty one.
            Slot isNull = EmitBinaryOperator(mirror.FieldSlot, ConstantSlot(_objectType, null), _objectType,
                "op_Equality", _boolType, null);

            string notNull = NewLabel("syncPackNotNull");
            string afterLength = NewLabel("syncPackAfterLength");

            _asm.JumpIfFalse(isNull, notNull);
            _asm.Jump(afterLength);

            _asm.MarkLabel(notNull);
            Copy(ArrayLength(mirror.FieldSlot), length);
            _asm.MarkLabel(afterLength);

            Copy(length, mirror.LengthSlot);

            // The mirrors start empty, and a shorter array than the previous pack would leave stale
            // elements behind, so every pack allocates the mirrors at exactly the current length.
            foreach (SyncLeaf leaf in mirror.Leaves)
            {
                _asm.Push(length);
                _asm.Push(leaf.Slot);
                Extern(UdonExternSignatures.ArrayConstructor(leaf.SlotType), "sync mirror allocation");
            }

            Slot index = NewSlot(_intType, "syncIndex");
            Copy(IntConstant(0), index);

            string condition = NewLabel("syncPackCondition");
            string end = NewLabel("syncPackEnd");

            _asm.MarkLabel(condition);
            Slot isLess = EmitBinaryOperator(index, length, _intType, "op_LessThan", _boolType, null);
            _asm.JumpIfFalse(isLess, end);

            Slot element = new ArrayElementLValue(this, mirror.FieldSlot, index, mirror.ArrayType, mirror.StructType).Get(false);
            element = EnsureStructElement(element, mirror.StructType, mirror.FieldSlot, index, mirror.ArrayType);

            foreach (SyncLeaf leaf in mirror.Leaves)
            {
                Slot value = NewSlot(leaf.Type, "syncValue");
                EmitReadStructPath(element, leaf.Path, mirror.StructType, value);
                new ArrayElementLValue(this, leaf.Slot, index, leaf.SlotType, leaf.Type).Set(value);
            }

            Slot next = EmitBinaryOperator(index, IntConstant(1), _intType, "op_Addition", _intType, null);
            Copy(next, index);
            _asm.Jump(condition);
            _asm.MarkLabel(end);
        }

        /// <summary>
        /// Struct elements are allocated by the compiler, but a public variable set in the inspector can
        /// contain nulls. Replace a null element with a default struct so the field reads below work.
        /// </summary>
        private Slot EnsureStructElement(Slot element, ITypeSymbol structType, Slot array, Slot index, ITypeSymbol arrayType)
        {
            Slot isNull = EmitBinaryOperator(element, ConstantSlot(_objectType, null), _objectType,
                "op_Equality", _boolType, null);

            string notNull = NewLabel("syncElemNotNull");
            string after = NewLabel("syncElemAfter");

            _asm.JumpIfFalse(isNull, notNull);

            EmitDefaultValue(element, structType);
            new ArrayElementLValue(this, array, index, arrayType, structType).Set(element);
            _asm.Jump(after);

            _asm.MarkLabel(notNull);
            _asm.MarkLabel(after);

            return element;
        }

        #endregion

        #region Unpacking

        /// <summary>Copies the mirror variables back into every synced struct.</summary>
        private void EmitUnpackSyncedStructs()
        {
            foreach (SyncedStructMirror mirror in _syncMirrors)
            {
                if (mirror.IsArray)
                    EmitUnpackArray(mirror);
                else
                    EmitUnpackStruct(mirror);
            }
        }

        private void EmitUnpackStruct(SyncedStructMirror mirror)
        {
            Slot fresh = NewSlot(mirror.StructType, "syncUnpack");
            EmitDefaultValue(fresh, mirror.StructType);
            Copy(fresh, mirror.FieldSlot);

            foreach (SyncLeaf leaf in mirror.Leaves)
            {
                Slot value = NewSlot(leaf.Type, "syncValue");
                Copy(leaf.Slot, value);
                EmitWriteStructPath(mirror.FieldSlot, leaf.Path, mirror.StructType, value);
            }
        }

        private void EmitUnpackArray(SyncedStructMirror mirror)
        {
            Slot length = NewSlot(_intType, "syncLength");
            Copy(mirror.LengthSlot, length);

            // A negative length would fail the array allocation.
            Slot isNegative = EmitBinaryOperator(length, IntConstant(0), _intType, "op_LessThan", _boolType, null);
            string notNegative = NewLabel("syncUnpackNotNegative");
            _asm.JumpIfFalse(isNegative, notNegative);
            Copy(IntConstant(0), length);
            _asm.MarkLabel(notNegative);

            // Never read past the end of a mirror: a missing or short array simply contributes no
            // elements instead of failing at runtime.
            foreach (SyncLeaf leaf in mirror.Leaves)
                ClampLengthToArray(length, leaf.Slot);

            Slot fresh = NewSlot(mirror.ArrayType, "syncUnpackArray");
            _asm.Push(length);
            _asm.Push(fresh);
            Extern(UdonExternSignatures.ArrayConstructor(mirror.ArrayType), "sync array allocation");
            FillDefaultStructs(fresh, mirror.ArrayType, length, mirror.StructType, null);
            Copy(fresh, mirror.FieldSlot);

            Slot index = NewSlot(_intType, "syncIndex");
            Copy(IntConstant(0), index);

            string condition = NewLabel("syncUnpackCondition");
            string end = NewLabel("syncUnpackEnd");

            _asm.MarkLabel(condition);
            Slot isLess = EmitBinaryOperator(index, length, _intType, "op_LessThan", _boolType, null);
            _asm.JumpIfFalse(isLess, end);

            Slot element = new ArrayElementLValue(this, fresh, index, mirror.ArrayType, mirror.StructType).Get(false);

            foreach (SyncLeaf leaf in mirror.Leaves)
            {
                Slot value = new ArrayElementLValue(this, leaf.Slot, index, leaf.SlotType, leaf.Type).Get(false);
                EmitWriteStructPath(element, leaf.Path, mirror.StructType, value);
            }

            Slot next = EmitBinaryOperator(index, IntConstant(1), _intType, "op_Addition", _intType, null);
            Copy(next, index);
            _asm.Jump(condition);
            _asm.MarkLabel(end);
        }

        /// <summary>
        /// Lowers <paramref name="length"/> to the length of <paramref name="array"/>, or to zero when
        /// the array is still null.
        /// </summary>
        private void ClampLengthToArray(Slot length, Slot array)
        {
            Slot arrayLength = NewSlot(_intType, "syncMirrorLength");
            Copy(IntConstant(0), arrayLength);

            Slot isNull = EmitBinaryOperator(array, ConstantSlot(_objectType, null), _objectType,
                "op_Equality", _boolType, null);

            string hasArray = NewLabel("syncMirrorHasArray");
            string afterRead = NewLabel("syncMirrorAfterRead");

            _asm.JumpIfFalse(isNull, hasArray);
            _asm.Jump(afterRead);

            _asm.MarkLabel(hasArray);
            Copy(ArrayLength(array), arrayLength);
            _asm.MarkLabel(afterRead);

            Slot isShorter = EmitBinaryOperator(arrayLength, length, _intType, "op_LessThan", _boolType, null);
            string notShorter = NewLabel("syncMirrorNotShorter");
            _asm.JumpIfFalse(isShorter, notShorter);
            Copy(arrayLength, length);
            _asm.MarkLabel(notShorter);
        }

        #endregion

        #region Struct field paths

        private void EmitReadStructPath(Slot root, List<int> path, ITypeSymbol rootType, Slot target)
        {
            Slot container = root;
            ITypeSymbol containerType = rootType;

            for (int i = 0; i < path.Count - 1; i++)
            {
                StructLayout layout = GetStructLayout(containerType);
                if (layout == null)
                    return;

                ITypeSymbol fieldType = layout.Fields[path[i]].Type;
                Slot next = NewSlot(fieldType, "syncPath");
                PushObjectArrayIndex(container, path[i], next);
                container = next;
                containerType = fieldType;
            }

            PushObjectArrayIndex(container, path[path.Count - 1], target);
        }

        private void EmitWriteStructPath(Slot root, List<int> path, ITypeSymbol rootType, Slot value)
        {
            Slot container = root;
            ITypeSymbol containerType = rootType;

            for (int i = 0; i < path.Count - 1; i++)
            {
                StructLayout layout = GetStructLayout(containerType);
                if (layout == null)
                    return;

                ITypeSymbol fieldType = layout.Fields[path[i]].Type;
                Slot next = NewSlot(fieldType, "syncPath");
                PushObjectArrayIndex(container, path[i], next);
                container = next;
                containerType = fieldType;
            }

            StoreStructField(container, path[path.Count - 1], value);
        }

        #endregion
    }
}
