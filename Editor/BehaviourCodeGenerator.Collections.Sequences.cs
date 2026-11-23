using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BetterUdon.Editor
{
    /// <summary>
    /// Queue&lt;T&gt; and Stack&lt;T&gt; implemented on the same two-element array representation as
    /// List&lt;T&gt;: <c>[ T[] items, int count ]</c>.
    ///
    /// Queue.Dequeue shifts the live elements down; Stack uses the tail as its top. Both are reference
    /// types, like the BCL versions.
    /// </summary>
    internal sealed partial class BehaviourCodeGenerator
    {
        private static bool TryGetQueueElementType(ITypeSymbol type, out ITypeSymbol elementType)
        {
            return TryGetSingleTypeArgument(type, "System.Collections.Generic.Queue<T>", out elementType);
        }

        private static bool TryGetStackElementType(ITypeSymbol type, out ITypeSymbol elementType)
        {
            return TryGetSingleTypeArgument(type, "System.Collections.Generic.Stack<T>", out elementType);
        }

        private static bool TryGetHashSetElementType(ITypeSymbol type, out ITypeSymbol elementType)
        {
            return TryGetSingleTypeArgument(type, "System.Collections.Generic.HashSet<T>", out elementType);
        }

        private static bool TryGetSingleTypeArgument(ITypeSymbol type, string definition, out ITypeSymbol elementType)
        {
            elementType = null;

            if (!(type is INamedTypeSymbol named) || !named.IsGenericType || named.TypeArguments.Length != 1)
                return false;

            if (named.ConstructedFrom?.ToDisplayString() != definition)
                return false;

            elementType = named.TypeArguments[0];
            return true;
        }

        private bool TryEmitQueueMethod(IMethodSymbol method, InvocationExpressionSyntax invocation, out Slot result)
        {
            return TryEmitSequenceMethod(method, invocation, true, out result);
        }

        private bool TryEmitStackMethod(IMethodSymbol method, InvocationExpressionSyntax invocation, out Slot result)
        {
            return TryEmitSequenceMethod(method, invocation, false, out result);
        }

        private bool TryEmitHashSetMethod(IMethodSymbol method, InvocationExpressionSyntax invocation, out Slot result)
        {
            result = null;

            if (method.IsStatic || !TryGetHashSetElementType(method.ContainingType, out ITypeSymbol elementType))
                return false;

            if (!(invocation.Expression is MemberAccessExpressionSyntax memberAccess))
                return false;

            Slot set = EmitExpression(memberAccess.Expression);
            if (set == null)
                return false;

            List<Slot> arguments = EmitCollectionArguments(invocation, method);

            switch (method.Name)
            {
                case "Add":
                {
                    Slot index = EmitListIndexOf(set, elementType, arguments[0], invocation);
                    result = NewSlot(_boolType, "added");
                    Copy(ConstantSlot(_boolType, false), result);

                    string addLabel = NewLabel("hashAdd");
                    string endLabel = NewLabel("hashAddEnd");
                    Slot exists = EmitBinaryOperator(index, IntConstant(0), _intType, "op_GreaterThanOrEqual",
                        _boolType, invocation);
                    _asm.JumpIfFalse(exists, addLabel);

                    _asm.Jump(endLabel);
                    _asm.MarkLabel(addLabel);

                    // Add the new element at the current count.
                    Slot count = ListCount(set);
                    Slot required = EmitBinaryOperator(count, IntConstant(1), _intType, "op_Addition",
                        _intType, invocation);
                    EmitEnsureCapacity(set, elementType, required);
                    ListElementSet(set, elementType, count, arguments[0]);
                    SetListCount(set, required);
                    Copy(ConstantSlot(_boolType, true), result);

                    _asm.MarkLabel(endLabel);
                    return true;
                }

                case "Remove":
                {
                    Slot index = EmitListIndexOf(set, elementType, arguments[0], invocation);
                    result = NewSlot(_boolType, "removedSet");
                    Copy(ConstantSlot(_boolType, false), result);

                    string missingLabel = NewLabel("hashMissing");
                    string endLabel = NewLabel("hashRemoveEnd");
                    Slot found = EmitBinaryOperator(index, IntConstant(0), _intType, "op_GreaterThanOrEqual",
                        _boolType, invocation);
                    _asm.JumpIfFalse(found, missingLabel);

                    Slot count = ListCount(set);
                    Slot last = EmitBinaryOperator(count, IntConstant(1), _intType, "op_Subtraction",
                        _intType, invocation);
                    Slot shift = NewSlot(_intType, "i");
                    Copy(index, shift);

                    string loopLabel = NewLabel("hashShift");
                    string loopEnd = NewLabel("hashShiftEnd");
                    _asm.MarkLabel(loopLabel);
                    Slot shifting = EmitBinaryOperator(shift, last, _intType, "op_LessThan", _boolType, invocation);
                    _asm.JumpIfFalse(shifting, loopEnd);

                    Slot next = EmitBinaryOperator(shift, IntConstant(1), _intType, "op_Addition",
                        _intType, invocation);
                    ListElementSet(set, elementType, shift, ListElementGet(set, elementType, next));
                    Copy(next, shift);
                    _asm.Jump(loopLabel);
                    _asm.MarkLabel(loopEnd);

                    SetListCount(set, last);
                    Copy(ConstantSlot(_boolType, true), result);
                    _asm.Jump(endLabel);

                    _asm.MarkLabel(missingLabel);
                    _asm.MarkLabel(endLabel);
                    return true;
                }

                case "Contains":
                    result = EmitSequenceContains(set, elementType, arguments[0], invocation);
                    return true;

                case "Clear":
                    SetListCount(set, IntConstant(0));
                    return true;

                case "ToArray":
                    result = EmitSequenceToArray(set, elementType, invocation);
                    return true;

                case "TrimExcess":
                    EmitSequenceTrimExcess(set, elementType);
                    return true;

                case "CopyTo":
                    return false;

                default:
                    return false;
            }
        }

        private bool TryEmitSequenceMethod(IMethodSymbol method, InvocationExpressionSyntax invocation, bool queue, out Slot result)
        {
            result = null;

            ITypeSymbol elementType;
            bool isQueue = TryGetQueueElementType(method.ContainingType, out elementType);
            bool isStack = !isQueue && TryGetStackElementType(method.ContainingType, out elementType);

            if (method.IsStatic || (queue ? !isQueue : !isStack))
                return false;

            if (!(invocation.Expression is MemberAccessExpressionSyntax memberAccess))
                return false;

            Slot collection = EmitExpression(memberAccess.Expression);
            if (collection == null)
                return false;

            List<Slot> arguments = EmitCollectionArguments(invocation, method);

            switch (method.Name)
            {
                case "Enqueue":
                    if (!isQueue)
                        return false;
                    EmitSequencePush(collection, elementType, arguments[0], invocation);
                    return true;

                case "Push":
                    if (!isStack)
                        return false;
                    EmitSequencePush(collection, elementType, arguments[0], invocation);
                    return true;

                case "Dequeue":
                    if (!isQueue)
                        return false;
                    result = EmitSequenceRemoveFirst(collection, elementType, invocation);
                    return true;

                case "Pop":
                    if (!isStack)
                        return false;
                    result = EmitSequencePop(collection, elementType, invocation);
                    return true;

                case "Peek":
                    result = NewSlot(elementType, "peek");
                    EmitDefaultValue(result, elementType);

                    CopySequencePeek(collection, elementType, result, isQueue, invocation);
                    return true;

                case "Clear":
                    SetListCount(collection, IntConstant(0));
                    return true;

                case "Contains":
                    result = EmitSequenceContains(collection, elementType, arguments[0], invocation);
                    return true;

                case "ToArray":
                    result = EmitSequenceToArray(collection, elementType, invocation);
                    return true;

                case "TrimExcess":
                    EmitSequenceTrimExcess(collection, elementType);
                    return true;

                default:
                    return false;
            }
        }

        private void EmitSequenceTrimExcess(Slot collection, ITypeSymbol elementType)
        {
            Slot count = ListCount(collection);
            Slot capacity = ArrayLength(ListItems(collection));
            Slot hasExcess = EmitBinaryOperator(capacity, count, _intType, "op_GreaterThan", _boolType, null);
            string endLabel = NewLabel("trimEnd");
            _asm.JumpIfFalse(hasExcess, endLabel);
            EmitListSetCapacity(collection, elementType, count);
            _asm.MarkLabel(endLabel);
        }

        private Slot EmitSequenceProperty(SyntaxNode access, IPropertySymbol property, Slot collection, ITypeSymbol elementType)
        {
            if (property.Name == "Count")
                return ListCount(collection);

            if (property.Name == "Capacity")
                return ArrayLength(ListItems(collection));

            AddError($"{property.ContainingType?.Name}.{property.Name} is not supported by the BetterUdon compiler.", access);
            return NewSlot(property.Type, "bad");
        }

        private void EmitSequencePush(Slot collection, ITypeSymbol elementType, Slot value, SyntaxNode node)
        {
            Slot count = ListCount(collection);
            Slot required = EmitBinaryOperator(count, IntConstant(1), _intType, "op_Addition", _intType, node);
            EmitEnsureCapacity(collection, elementType, required);
            ListElementSet(collection, elementType, count, value);
            SetListCount(collection, required);
        }

        private Slot EmitSequenceRemoveFirst(Slot collection, ITypeSymbol elementType, SyntaxNode node)
        {
            Slot count = ListCount(collection);
            Slot result = NewSlot(elementType, "dequeue");
            EmitDefaultValue(result, elementType);

            string emptyLabel = NewLabel("dequeueEmpty");
            string endLabel = NewLabel("dequeueEnd");

            Slot hasValue = EmitBinaryOperator(count, IntConstant(0), _intType, "op_GreaterThan", _boolType, node);
            _asm.JumpIfFalse(hasValue, emptyLabel);

            Copy(ListElementGet(collection, elementType, IntConstant(0)), result);

            Slot index = NewSlot(_intType, "i");
            Copy(IntConstant(0), index);
            Slot last = EmitBinaryOperator(count, IntConstant(1), _intType, "op_Subtraction", _intType, node);

            string loopLabel = NewLabel("queueShift");
            string loopEnd = NewLabel("queueShiftEnd");
            _asm.MarkLabel(loopLabel);

            Slot shifting = EmitBinaryOperator(index, last, _intType, "op_LessThan", _boolType, node);
            _asm.JumpIfFalse(shifting, loopEnd);

            Slot next = EmitBinaryOperator(index, IntConstant(1), _intType, "op_Addition", _intType, node);
            ListElementSet(collection, elementType, index,
                ListElementGet(collection, elementType, next));

            Copy(next, index);
            _asm.Jump(loopLabel);
            _asm.MarkLabel(loopEnd);

            SetListCount(collection, last);
            _asm.Jump(endLabel);

            _asm.MarkLabel(emptyLabel);
            _asm.MarkLabel(endLabel);
            return result;
        }

        private Slot EmitSequencePop(Slot collection, ITypeSymbol elementType, SyntaxNode node)
        {
            Slot count = ListCount(collection);
            Slot result = NewSlot(elementType, "pop");
            EmitDefaultValue(result, elementType);

            string emptyLabel = NewLabel("popEmpty");
            string endLabel = NewLabel("popEnd");

            Slot hasValue = EmitBinaryOperator(count, IntConstant(0), _intType, "op_GreaterThan", _boolType, node);
            _asm.JumpIfFalse(hasValue, emptyLabel);

            Slot last = EmitBinaryOperator(count, IntConstant(1), _intType, "op_Subtraction", _intType, node);
            Copy(ListElementGet(collection, elementType, last), result);
            SetListCount(collection, last);
            _asm.Jump(endLabel);

            _asm.MarkLabel(emptyLabel);
            _asm.MarkLabel(endLabel);
            return result;
        }

        private void CopySequencePeek(Slot collection, ITypeSymbol elementType, Slot result, bool queue, SyntaxNode node)
        {
            Slot count = ListCount(collection);
            string emptyLabel = NewLabel("peekEmpty");
            string endLabel = NewLabel("peekEnd");

            Slot hasValue = EmitBinaryOperator(count, IntConstant(0), _intType, "op_GreaterThan", _boolType, node);
            _asm.JumpIfFalse(hasValue, emptyLabel);

            Slot index = queue
                ? IntConstant(0)
                : EmitBinaryOperator(count, IntConstant(1), _intType, "op_Subtraction", _intType, node);

            Copy(ListElementGet(collection, elementType, index), result);
            _asm.Jump(endLabel);
            _asm.MarkLabel(emptyLabel);
            _asm.MarkLabel(endLabel);
        }

        private Slot EmitSequenceContains(Slot collection, ITypeSymbol elementType, Slot value, SyntaxNode node)
        {
            Slot index = EmitListIndexOf(collection, elementType, value, node);
            Slot result = NewSlot(_boolType, "contains");
            Copy(EmitBinaryOperator(index, IntConstant(0), _intType, "op_GreaterThanOrEqual", _boolType, node), result);
            return result;
        }

        private Slot EmitSequenceToArray(Slot collection, ITypeSymbol elementType, SyntaxNode node)
        {
            ITypeSymbol arrayType = MakeArrayType(elementType);
            Slot count = ListCount(collection);
            Slot result = NewSlot(arrayType, "toArray");
            CreateArray(result, arrayType, count);

            Slot index = NewSlot(_intType, "i");
            Copy(IntConstant(0), index);

            string loopLabel = NewLabel("toArrayLoop");
            string loopEnd = NewLabel("toArrayEnd");
            _asm.MarkLabel(loopLabel);

            Slot inRange = EmitBinaryOperator(index, count, _intType, "op_LessThan", _boolType, node);
            _asm.JumpIfFalse(inRange, loopEnd);

            ArrayElementLValue target = new ArrayElementLValue(this, result, index, arrayType, elementType);
            target.Set(ListElementGet(collection, elementType, index));

            Copy(EmitBinaryOperator(index, IntConstant(1), _intType, "op_Addition", _intType, node), index);
            _asm.Jump(loopLabel);
            _asm.MarkLabel(loopEnd);
            return result;
        }
    }
}
