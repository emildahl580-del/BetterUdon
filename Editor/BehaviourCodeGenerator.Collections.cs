using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BetterUdon.Editor
{
    /// <summary>
    /// Implements <c>System.Collections.Generic.List&lt;T&gt;</c> on top of plain Udon arrays.
    ///
    /// Udon exposes no collection nodes at all, so the list is represented as a two element
    /// <c>object[]</c>:
    ///
    ///   [0] = T[]  the backing store
    ///   [1] = int  the element count
    ///
    /// It is a reference type on the heap (assignment aliases, exactly like C#), and every method is
    /// emitted inline with the same growth strategy the BCL uses (double, minimum 4).
    /// </summary>
    internal sealed partial class BehaviourCodeGenerator
    {
        private const int ListItemsIndex = 0;
        private const int ListCountIndex = 1;

        private static readonly SymbolDisplayFormat _typeDisplayFormat =
            SymbolDisplayFormat.FullyQualifiedFormat.WithGenericsOptions(SymbolDisplayGenericsOptions.IncludeTypeParameters);

        #region Type detection

        private static bool IsListType(ITypeSymbol type, out ITypeSymbol elementType)
        {
            elementType = null;

            if (!(type is INamedTypeSymbol named) || !named.IsGenericType || named.TypeArguments.Length != 1)
                return false;

            if (named.ConstructedFrom?.ToDisplayString() != "System.Collections.Generic.List<T>")
                return false;

            elementType = named.TypeArguments[0];
            return true;
        }

        private bool TryGetListElementType(ITypeSymbol type, out ITypeSymbol elementType)
        {
            return IsListType(type, out elementType);
        }

        #endregion

        #region Representation helpers

        private Slot CreateList(ITypeSymbol elementType, Slot capacity)
        {
            Slot listSlot = NewSlot(null, "list");
            CreateObjectArray(listSlot, 2);

            Slot items = NewSlot(MakeArrayType(elementType), "listItems");
            CreateArray(items, MakeArrayType(elementType), capacity);
            StoreStructField(listSlot, ListItemsIndex, items);

            StoreStructField(listSlot, ListCountIndex, IntConstant(0));
            return listSlot;
        }

        private Slot ListItems(Slot list)
        {
            Slot items = NewSlot(null, "listItems");
            PushObjectArrayIndex(list, ListItemsIndex, items);
            return items;
        }

        private Slot ListCount(Slot list)
        {
            Slot count = NewSlot(_intType, "listCount");
            PushObjectArrayIndex(list, ListCountIndex, count);
            return count;
        }

        private void SetListItems(Slot list, Slot items)
        {
            StoreStructField(list, ListItemsIndex, items);
        }

        private void SetListCount(Slot list, Slot count)
        {
            StoreStructField(list, ListCountIndex, count);
        }

        #endregion

        #region Grow / copy

        /// <summary>
        /// Makes sure the backing array can hold <paramref name="requiredCount"/> elements.
        /// </summary>
        private void EmitEnsureCapacity(Slot list, ITypeSymbol elementType, Slot requiredCount)
        {
            ITypeSymbol arrayType = MakeArrayType(elementType);
            Slot items = ListItems(list);
            Slot capacity = ArrayLength(items);

            string noGrowLabel = NewLabel("growEnd");
            Slot needsGrow = EmitBinaryOperator(capacity, requiredCount, _intType, "op_LessThan", _boolType, null);
            _asm.JumpIfFalse(needsGrow, noGrowLabel);

            // newCapacity = max(4, capacity * 2); if still too small use requiredCount.
            Slot doubled = EmitBinaryOperator(capacity, IntConstant(2), _intType, "op_Multiply", _intType, null);
            Slot newCapacity = EmitBinaryOperator(doubled, IntConstant(4), _intType, "op_LessThan", _boolType, null);

            string useDoubled = NewLabel("useDoubled");
            string haveCapacity = NewLabel("haveCapacity");
            _asm.JumpIfFalse(newCapacity, useDoubled);
            Copy(IntConstant(4), newCapacity);
            _asm.Jump(haveCapacity);
            _asm.MarkLabel(useDoubled);
            Copy(doubled, newCapacity);
            _asm.MarkLabel(haveCapacity);

            Slot tooSmall = EmitBinaryOperator(newCapacity, requiredCount, _intType, "op_LessThan", _boolType, null);
            string capacityReady = NewLabel("capacityReady");
            _asm.JumpIfFalse(tooSmall, capacityReady);
            Copy(requiredCount, newCapacity);
            _asm.MarkLabel(capacityReady);

            Slot grown = NewSlot(arrayType, "listGrown");
            CreateArray(grown, arrayType, newCapacity);

            // Copy the live elements across.
            Slot count = ListCount(list);
            Slot index = NewSlot(_intType, "i");
            Copy(IntConstant(0), index);

            string loopLabel = NewLabel("copyLoop");
            string loopEnd = NewLabel("copyEnd");
            _asm.MarkLabel(loopLabel);

            Slot inRange = EmitBinaryOperator(index, count, _intType, "op_LessThan", _boolType, null);
            _asm.JumpIfFalse(inRange, loopEnd);

            ArrayElementLValue source = new ArrayElementLValue(this, items, index, arrayType, elementType);
            ArrayElementLValue target = new ArrayElementLValue(this, grown, index, arrayType, elementType);
            target.Set(source.Get(true));

            Copy(EmitBinaryOperator(index, IntConstant(1), _intType, "op_Addition", _intType, null), index);
            _asm.Jump(loopLabel);
            _asm.MarkLabel(loopEnd);

            SetListItems(list, grown);
            _asm.MarkLabel(noGrowLabel);
        }

        private Slot ListElementGet(Slot list, ITypeSymbol elementType, Slot index)
        {
            ITypeSymbol arrayType = MakeArrayType(elementType);
            ArrayElementLValue element = new ArrayElementLValue(this, ListItems(list), index, arrayType, elementType);
            return element.Get(true);
        }

        private void ListElementSet(Slot list, ITypeSymbol elementType, Slot index, Slot value)
        {
            ITypeSymbol arrayType = MakeArrayType(elementType);
            ArrayElementLValue element = new ArrayElementLValue(this, ListItems(list), index, arrayType, elementType);
            element.Set(value);
        }

        #endregion

        #region Equality

        private Slot EmitValueEquality(Slot left, Slot right, ITypeSymbol type, SyntaxNode node)
        {
            if (UdonTypeSystem.IsUserStruct(type))
                return EmitStructEquality(left, right, type, false, node);

            if (type != null && (type.IsReferenceType || type.TypeKind == TypeKind.Interface))
                return EmitBinaryOperator(left, right, _objectType, "op_Equality", _boolType, node);

            return EmitBinaryOperator(left, right, type, "op_Equality", _boolType, node);
        }

        /// <summary>
        /// Emits a linear search and leaves the index in the returned slot, or -1.
        /// </summary>
        private Slot EmitListIndexOf(Slot list, ITypeSymbol elementType, Slot value, SyntaxNode node)
        {
            Slot result = NewSlot(_intType, "indexOf");
            Copy(IntConstant(-1), result);

            Slot count = ListCount(list);
            Slot index = NewSlot(_intType, "i");
            Copy(IntConstant(0), index);

            string loopLabel = NewLabel("findLoop");
            string notEqualLabel = NewLabel("findNext");
            string endLabel = NewLabel("findEnd");

            _asm.MarkLabel(loopLabel);
            Slot inRange = EmitBinaryOperator(index, count, _intType, "op_LessThan", _boolType, node);
            _asm.JumpIfFalse(inRange, endLabel);

            Slot element = ListElementGet(list, elementType, index);
            Slot equal = EmitValueEquality(element, value, elementType, node);

            _asm.JumpIfFalse(equal, notEqualLabel);
            Copy(index, result);
            _asm.Jump(endLabel);

            _asm.MarkLabel(notEqualLabel);
            Copy(EmitBinaryOperator(index, IntConstant(1), _intType, "op_Addition", _intType, node), index);
            _asm.Jump(loopLabel);

            _asm.MarkLabel(endLabel);
            return result;
        }

        #endregion

        #region Methods

        /// <summary>
        /// Lowers a call to a <c>List&lt;T&gt;</c> member. Returns false when the call is not a list
        /// operation so the normal extern path can handle it.
        /// </summary>
        private bool TryEmitListMethod(IMethodSymbol method, InvocationExpressionSyntax invocation, out Slot result)
        {
            result = null;

            if (method.IsStatic || !TryGetListElementType(method.ContainingType, out ITypeSymbol elementType))
                return false;

            if (!(invocation.Expression is MemberAccessExpressionSyntax memberAccess))
                return false;

            Slot list = EmitExpression(memberAccess.Expression);

            if (list == null)
                return false;

            List<Slot> arguments = invocation.ArgumentList.Arguments
                .Select(argument => EmitExpression(argument.Expression))
                .ToList();

            switch (method.Name)
            {
                case "Add":
                {
                    Slot count = ListCount(list);
                    Slot required = EmitBinaryOperator(count, IntConstant(1), _intType, "op_Addition", _intType, invocation);
                    EmitEnsureCapacity(list, elementType, required);
                    ListElementSet(list, elementType, count, arguments[0]);
                    SetListCount(list, required);
                    return true;
                }

                case "Insert":
                {
                    Slot count = ListCount(list);
                    Slot required = EmitBinaryOperator(count, IntConstant(1), _intType, "op_Addition", _intType, invocation);
                    EmitEnsureCapacity(list, elementType, required);

                    Slot index = ConvertTo(arguments[0], _intType);
                    Slot shift = NewSlot(_intType, "i");
                    Copy(count, shift);

                    string loopLabel = NewLabel("shiftLoop");
                    string loopEnd = NewLabel("shiftEnd");
                    _asm.MarkLabel(loopLabel);

                    Slot shifting = EmitBinaryOperator(shift, index, _intType, "op_GreaterThan", _boolType, invocation);
                    _asm.JumpIfFalse(shifting, loopEnd);

                    Slot previous = EmitBinaryOperator(shift, IntConstant(1), _intType, "op_Subtraction", _intType, invocation);
                    ListElementSet(list, elementType, shift, ListElementGet(list, elementType, previous));
                    Copy(previous, shift);
                    _asm.Jump(loopLabel);
                    _asm.MarkLabel(loopEnd);

                    ListElementSet(list, elementType, index, arguments[1]);
                    SetListCount(list, required);
                    return true;
                }

                case "RemoveAt":
                {
                    Slot count = ListCount(list);
                    Slot index = ConvertTo(arguments[0], _intType);
                    Slot last = EmitBinaryOperator(count, IntConstant(1), _intType, "op_Subtraction", _intType, invocation);

                    Slot shift = NewSlot(_intType, "i");
                    Copy(index, shift);

                    string loopLabel = NewLabel("shiftDownLoop");
                    string loopEnd = NewLabel("shiftDownEnd");
                    _asm.MarkLabel(loopLabel);

                    Slot shifting = EmitBinaryOperator(shift, last, _intType, "op_LessThan", _boolType, invocation);
                    _asm.JumpIfFalse(shifting, loopEnd);

                    Slot next = EmitBinaryOperator(shift, IntConstant(1), _intType, "op_Addition", _intType, invocation);
                    ListElementSet(list, elementType, shift, ListElementGet(list, elementType, next));
                    Copy(next, shift);
                    _asm.Jump(loopLabel);
                    _asm.MarkLabel(loopEnd);

                    SetListCount(list, last);
                    return true;
                }

                case "Remove":
                {
                    Slot index = EmitListIndexOf(list, elementType, arguments[0], invocation);
                    Slot found = EmitBinaryOperator(index, IntConstant(0), _intType, "op_GreaterThanOrEqual", _boolType, invocation);

                    string skipLabel = NewLabel("removeSkip");
                    result = NewSlot(_boolType, "removed");
                    Copy(ConstantSlot(_boolType, false), result);

                    _asm.JumpIfFalse(found, skipLabel);

                    Slot count = ListCount(list);
                    Slot last = EmitBinaryOperator(count, IntConstant(1), _intType, "op_Subtraction", _intType, invocation);

                    Slot shift = NewSlot(_intType, "i");
                    Copy(index, shift);

                    string loopLabel = NewLabel("removeLoop");
                    string loopEnd = NewLabel("removeEnd");
                    _asm.MarkLabel(loopLabel);

                    Slot shifting = EmitBinaryOperator(shift, last, _intType, "op_LessThan", _boolType, invocation);
                    _asm.JumpIfFalse(shifting, loopEnd);

                    Slot next = EmitBinaryOperator(shift, IntConstant(1), _intType, "op_Addition", _intType, invocation);
                    ListElementSet(list, elementType, shift, ListElementGet(list, elementType, next));
                    Copy(next, shift);
                    _asm.Jump(loopLabel);
                    _asm.MarkLabel(loopEnd);

                    SetListCount(list, last);
                    Copy(ConstantSlot(_boolType, true), result);

                    _asm.MarkLabel(skipLabel);
                    return true;
                }

                case "Clear":
                    SetListCount(list, IntConstant(0));
                    return true;

                case "Contains":
                    result = NewSlot(_boolType, "contains");
                    Copy(EmitBinaryOperator(EmitListIndexOf(list, elementType, arguments[0], invocation),
                        IntConstant(0), _intType, "op_GreaterThanOrEqual", _boolType, invocation), result);
                    return true;

                case "IndexOf":
                    result = EmitListIndexOf(list, elementType, arguments[0], invocation);
                    return true;

                case "ToArray":
                {
                    ITypeSymbol arrayType = MakeArrayType(elementType);
                    Slot count = ListCount(list);
                    Slot array = NewSlot(arrayType, "toArray");
                    CreateArray(array, arrayType, count);

                    Slot items = ListItems(list);
                    Slot index = NewSlot(_intType, "i");
                    Copy(IntConstant(0), index);

                    string loopLabel = NewLabel("toArrayLoop");
                    string loopEnd = NewLabel("toArrayEnd");
                    _asm.MarkLabel(loopLabel);

                    Slot inRange = EmitBinaryOperator(index, count, _intType, "op_LessThan", _boolType, invocation);
                    _asm.JumpIfFalse(inRange, loopEnd);

                    ArrayElementLValue source = new ArrayElementLValue(this, items, index, arrayType, elementType);
                    ArrayElementLValue target = new ArrayElementLValue(this, array, index, arrayType, elementType);
                    target.Set(source.Get(true));

                    Copy(EmitBinaryOperator(index, IntConstant(1), _intType, "op_Addition", _intType, invocation), index);
                    _asm.Jump(loopLabel);
                    _asm.MarkLabel(loopEnd);

                    result = array;
                    return true;
                }

                case "AddRange":
                {
                    // Accepts an array; a List source is not supported yet.
                    if (!(arguments[0].Type is IArrayTypeSymbol sourceArray) ||
                        !SymbolEqualityComparer.Default.Equals(sourceArray.ElementType, elementType))
                    {
                        AddError("List<T>.AddRange only supports an array of the element type.", invocation);
                        return true;
                    }

                    Slot source = arguments[0];
                    Slot sourceLength = ArrayLength(source);
                    Slot count = ListCount(list);
                    Slot required = EmitBinaryOperator(count, sourceLength, _intType, "op_Addition", _intType, invocation);
                    EmitEnsureCapacity(list, elementType, required);

                    Slot index = NewSlot(_intType, "i");
                    Copy(IntConstant(0), index);

                    string loopLabel = NewLabel("addRangeLoop");
                    string loopEnd = NewLabel("addRangeEnd");
                    _asm.MarkLabel(loopLabel);

                    Slot inRange = EmitBinaryOperator(index, sourceLength, _intType, "op_LessThan", _boolType, invocation);
                    _asm.JumpIfFalse(inRange, loopEnd);

                    ArrayElementLValue sourceElement = new ArrayElementLValue(this, source, index, sourceArray, elementType);
                    Slot destinationIndex = EmitBinaryOperator(count, index, _intType, "op_Addition", _intType, invocation);
                    ListElementSet(list, elementType, destinationIndex, sourceElement.Get(true));

                    Copy(EmitBinaryOperator(index, IntConstant(1), _intType, "op_Addition", _intType, invocation), index);
                    _asm.Jump(loopLabel);
                    _asm.MarkLabel(loopEnd);

                    SetListCount(list, required);
                    return true;
                }

                case "ClearCapacity":
                    return false;

                default:
                    AddError($"List<T>.{method.Name} is not supported by the BetterUdon compiler.", invocation);
                    return true;
            }
        }

        #endregion

        #region Expressions

        private Slot EmitListProperty(SyntaxNode access, IPropertySymbol property, Slot list, ITypeSymbol elementType)
        {
            switch (property.Name)
            {
                case "Count":
                    return ListCount(list);

                case "Capacity":
                    return ArrayLength(ListItems(list));

                default:
                    AddError($"List<T>.{property.Name} is not supported by the BetterUdon compiler.", access);
                    return NewSlot(_intType, "bad");
            }
        }


        #endregion
    }
}
