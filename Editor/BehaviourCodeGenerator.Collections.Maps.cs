using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BetterUdon.Editor
{
    /// <summary>
    /// Dictionary&lt;K,V&gt; implemented as three heap arrays:
    ///
    ///   [0] = K[] keys
    ///   [1] = V[] values
    ///   [2] = int count
    ///
    /// Lookup is linear. That is slower than the BCL hash table but keeps equality and hashing
    /// monomorphised and correct for primitives and user structs without adding a runtime service.
    /// </summary>
    internal sealed partial class BehaviourCodeGenerator
    {
        private const int DictionaryKeysIndex = 0;
        private const int DictionaryValuesIndex = 1;
        private const int DictionaryCountIndex = 2;

        private static bool TryGetDictionaryTypes(ITypeSymbol type, out ITypeSymbol keyType, out ITypeSymbol valueType)
        {
            keyType = null;
            valueType = null;

            if (!(type is INamedTypeSymbol named) || !named.IsGenericType || named.TypeArguments.Length != 2)
                return false;

            if (named.ConstructedFrom?.ToDisplayString() != "System.Collections.Generic.Dictionary<TKey, TValue>")
                return false;

            keyType = named.TypeArguments[0];
            valueType = named.TypeArguments[1];
            return true;
        }

        private Slot CreateDictionary(ITypeSymbol keyType, ITypeSymbol valueType, Slot capacity)
        {
            Slot dictionary = NewSlot(null, "dictionary");
            CreateObjectArray(dictionary, 3);

            Slot keys = NewSlot(MakeArrayType(keyType), "dictionaryKeys");
            CreateArray(keys, MakeArrayType(keyType), capacity);
            StoreStructField(dictionary, DictionaryKeysIndex, keys);

            Slot values = NewSlot(MakeArrayType(valueType), "dictionaryValues");
            CreateArray(values, MakeArrayType(valueType), capacity);
            StoreStructField(dictionary, DictionaryValuesIndex, values);

            StoreStructField(dictionary, DictionaryCountIndex, IntConstant(0));
            return dictionary;
        }

        private Slot DictionaryKeys(Slot dictionary)
        {
            Slot keys = NewSlot(null, "dictionaryKeys");
            PushObjectArrayIndex(dictionary, DictionaryKeysIndex, keys);
            return keys;
        }

        private Slot DictionaryValues(Slot dictionary)
        {
            Slot values = NewSlot(null, "dictionaryValues");
            PushObjectArrayIndex(dictionary, DictionaryValuesIndex, values);
            return values;
        }

        private Slot DictionaryCount(Slot dictionary)
        {
            Slot count = NewSlot(_intType, "dictionaryCount");
            PushObjectArrayIndex(dictionary, DictionaryCountIndex, count);
            return count;
        }

        private void SetDictionaryCount(Slot dictionary, Slot count)
        {
            StoreStructField(dictionary, DictionaryCountIndex, count);
        }

        private void SetDictionaryKeys(Slot dictionary, Slot keys)
        {
            StoreStructField(dictionary, DictionaryKeysIndex, keys);
        }

        private void SetDictionaryValues(Slot dictionary, Slot values)
        {
            StoreStructField(dictionary, DictionaryValuesIndex, values);
        }

        private void EmitEnsureDictionaryCapacity(Slot dictionary, ITypeSymbol keyType, ITypeSymbol valueType, Slot requiredCount)
        {
            ITypeSymbol keysType = MakeArrayType(keyType);
            ITypeSymbol valuesType = MakeArrayType(valueType);

            Slot keys = DictionaryKeys(dictionary);
            Slot values = DictionaryValues(dictionary);
            Slot capacity = ArrayLength(keys);

            string noGrowLabel = NewLabel("dictGrowEnd");
            Slot needsGrow = EmitBinaryOperator(capacity, requiredCount, _intType, "op_LessThan", _boolType, null);
            _asm.JumpIfFalse(needsGrow, noGrowLabel);

            Slot doubled = EmitBinaryOperator(capacity, IntConstant(2), _intType, "op_Multiply", _intType, null);
            Slot belowFour = EmitBinaryOperator(doubled, IntConstant(4), _intType, "op_LessThan", _boolType, null);
            Slot newCapacity = NewSlot(_intType, "dictCapacity");
            Copy(IntConstant(4), newCapacity);

            string useDoubled = NewLabel("dictUseDoubled");
            string capacityChosen = NewLabel("dictCapacityChosen");
            _asm.JumpIfFalse(belowFour, useDoubled);
            _asm.Jump(capacityChosen);
            _asm.MarkLabel(useDoubled);
            Copy(doubled, newCapacity);
            _asm.MarkLabel(capacityChosen);

            Slot tooSmall = EmitBinaryOperator(newCapacity, requiredCount, _intType, "op_LessThan", _boolType, null);
            string capacityReady = NewLabel("dictCapacityReady");
            _asm.JumpIfFalse(tooSmall, capacityReady);
            Copy(requiredCount, newCapacity);
            _asm.MarkLabel(capacityReady);

            Slot grownKeys = NewSlot(keysType, "dictKeysGrown");
            CreateArray(grownKeys, keysType, newCapacity);
            Slot grownValues = NewSlot(valuesType, "dictValuesGrown");
            CreateArray(grownValues, valuesType, newCapacity);

            Slot count = DictionaryCount(dictionary);
            Slot index = NewSlot(_intType, "i");
            Copy(IntConstant(0), index);

            string loopLabel = NewLabel("dictCopyLoop");
            string loopEnd = NewLabel("dictCopyEnd");
            _asm.MarkLabel(loopLabel);
            Slot inRange = EmitBinaryOperator(index, count, _intType, "op_LessThan", _boolType, null);
            _asm.JumpIfFalse(inRange, loopEnd);

            ArrayElementLValue sourceKey = new ArrayElementLValue(this, keys, index, keysType, keyType);
            ArrayElementLValue targetKey = new ArrayElementLValue(this, grownKeys, index, keysType, keyType);
            targetKey.Set(sourceKey.Get(true));

            ArrayElementLValue sourceValue = new ArrayElementLValue(this, values, index, valuesType, valueType);
            ArrayElementLValue targetValue = new ArrayElementLValue(this, grownValues, index, valuesType, valueType);
            targetValue.Set(sourceValue.Get(true));

            Copy(EmitBinaryOperator(index, IntConstant(1), _intType, "op_Addition", _intType, null), index);
            _asm.Jump(loopLabel);
            _asm.MarkLabel(loopEnd);

            SetDictionaryKeys(dictionary, grownKeys);
            SetDictionaryValues(dictionary, grownValues);
            _asm.MarkLabel(noGrowLabel);
        }

        private Slot EmitDictionaryFindIndex(Slot dictionary, ITypeSymbol keyType, Slot key, SyntaxNode node)
        {
            Slot result = NewSlot(_intType, "dictIndex");
            Copy(IntConstant(-1), result);

            Slot keys = DictionaryKeys(dictionary);
            Slot count = DictionaryCount(dictionary);
            Slot index = NewSlot(_intType, "i");
            Copy(IntConstant(0), index);

            ITypeSymbol keysType = MakeArrayType(keyType);
            string loopLabel = NewLabel("dictFindLoop");
            string nextLabel = NewLabel("dictFindNext");
            string endLabel = NewLabel("dictFindEnd");

            _asm.MarkLabel(loopLabel);
            Slot inRange = EmitBinaryOperator(index, count, _intType, "op_LessThan", _boolType, node);
            _asm.JumpIfFalse(inRange, endLabel);

            ArrayElementLValue keyElement = new ArrayElementLValue(this, keys, index, keysType, keyType);
            Slot equal = EmitValueEquality(keyElement.Get(true), key, keyType, node);
            _asm.JumpIfFalse(equal, nextLabel);

            Copy(index, result);
            _asm.Jump(endLabel);

            _asm.MarkLabel(nextLabel);
            Copy(EmitBinaryOperator(index, IntConstant(1), _intType, "op_Addition", _intType, node), index);
            _asm.Jump(loopLabel);

            _asm.MarkLabel(endLabel);
            return result;
        }

        private Slot EmitDictionaryGet(Slot dictionary, ITypeSymbol keyType, ITypeSymbol valueType, Slot key, SyntaxNode node)
        {
            Slot index = EmitDictionaryFindIndex(dictionary, keyType, key, node);
            Slot result = NewSlot(valueType, "dictValue");
            EmitDefaultValue(result, valueType);

            string missingLabel = NewLabel("dictMissing");
            string endLabel = NewLabel("dictGetEnd");

            Slot found = EmitBinaryOperator(index, IntConstant(0), _intType, "op_GreaterThanOrEqual", _boolType, node);
            _asm.JumpIfFalse(found, missingLabel);

            Slot values = DictionaryValues(dictionary);
            ArrayElementLValue value = new ArrayElementLValue(this, values, index, MakeArrayType(valueType), valueType);
            Copy(value.Get(true), result);
            _asm.Jump(endLabel);

            _asm.MarkLabel(missingLabel);
            _asm.MarkLabel(endLabel);
            return result;
        }

        private void EmitDictionarySet(Slot dictionary, ITypeSymbol keyType, ITypeSymbol valueType, Slot key, Slot value, SyntaxNode node)
        {
            Slot index = EmitDictionaryFindIndex(dictionary, keyType, key, node);

            string addLabel = NewLabel("dictAdd");
            string endLabel = NewLabel("dictSetEnd");

            Slot found = EmitBinaryOperator(index, IntConstant(0), _intType, "op_GreaterThanOrEqual", _boolType, node);
            _asm.JumpIfFalse(found, addLabel);

            Slot values = DictionaryValues(dictionary);
            ArrayElementLValue existing = new ArrayElementLValue(this, values, index, MakeArrayType(valueType), valueType);
            existing.Set(value);
            _asm.Jump(endLabel);

            _asm.MarkLabel(addLabel);

            Slot count = DictionaryCount(dictionary);
            Slot required = EmitBinaryOperator(count, IntConstant(1), _intType, "op_Addition", _intType, node);
            EmitEnsureDictionaryCapacity(dictionary, keyType, valueType, required);

            Slot keys = DictionaryKeys(dictionary);
            ArrayElementLValue newKey = new ArrayElementLValue(this, keys, count, MakeArrayType(keyType), keyType);
            newKey.Set(key);

            Slot newValues = DictionaryValues(dictionary);
            ArrayElementLValue newValue = new ArrayElementLValue(this, newValues, count, MakeArrayType(valueType), valueType);
            newValue.Set(value);

            SetDictionaryCount(dictionary, required);

            _asm.MarkLabel(endLabel);
        }

        private bool TryEmitDictionaryMethod(IMethodSymbol method, InvocationExpressionSyntax invocation, out Slot result)
        {
            result = null;

            if (method.IsStatic ||
                !TryGetDictionaryTypes(method.ContainingType, out ITypeSymbol keyType, out ITypeSymbol valueType))
                return false;

            if (!(invocation.Expression is MemberAccessExpressionSyntax memberAccess))
                return false;

            Slot dictionary = EmitExpression(memberAccess.Expression);
            if (dictionary == null)
                return false;

            List<Slot> arguments = invocation.ArgumentList.Arguments
                .Select(argument => EmitExpression(argument.Expression))
                .ToList();

            switch (method.Name)
            {
                case "Add":
                case "set_Item":
                    EmitDictionarySet(dictionary, keyType, valueType,
                        ConvertTo(arguments[0], keyType), ConvertTo(arguments[1], valueType), invocation);
                    return true;

                case "get_Item":
                    result = EmitDictionaryGet(dictionary, keyType, valueType,
                        ConvertTo(arguments[0], keyType), invocation);
                    return true;

                case "ContainsKey":
                    result = NewSlot(_boolType, "containsKey");
                    {
                        Slot index = EmitDictionaryFindIndex(dictionary, keyType, ConvertTo(arguments[0], keyType), invocation);
                        Copy(EmitBinaryOperator(index, IntConstant(0), _intType, "op_GreaterThanOrEqual",
                            _boolType, invocation), result);
                    }
                    return true;

                case "Remove":
                {
                    Slot index = EmitDictionaryFindIndex(dictionary, keyType, ConvertTo(arguments[0], keyType), invocation);
                    result = NewSlot(_boolType, "removedKey");
                    Copy(ConstantSlot(_boolType, false), result);

                    string missingLabel = NewLabel("dictRemoveMissing");
                    string endLabel = NewLabel("dictRemoveEnd");
                    Slot found = EmitBinaryOperator(index, IntConstant(0), _intType, "op_GreaterThanOrEqual",
                        _boolType, invocation);
                    _asm.JumpIfFalse(found, missingLabel);

                    Slot keys = DictionaryKeys(dictionary);
                    Slot values = DictionaryValues(dictionary);
                    Slot count = DictionaryCount(dictionary);
                    Slot last = EmitBinaryOperator(count, IntConstant(1), _intType, "op_Subtraction", _intType, invocation);

                    Slot shift = NewSlot(_intType, "i");
                    Copy(index, shift);

                    string loopLabel = NewLabel("dictRemoveLoop");
                    string loopEnd = NewLabel("dictRemoveLoopEnd");
                    _asm.MarkLabel(loopLabel);
                    Slot shifting = EmitBinaryOperator(shift, last, _intType, "op_LessThan", _boolType, invocation);
                    _asm.JumpIfFalse(shifting, loopEnd);

                    Slot next = EmitBinaryOperator(shift, IntConstant(1), _intType, "op_Addition", _intType, invocation);
                    ArrayElementLValue keyTarget = new ArrayElementLValue(this, keys, shift, MakeArrayType(keyType), keyType);
                    keyTarget.Set(new ArrayElementLValue(this, keys, next, MakeArrayType(keyType), keyType).Get(true));
                    ArrayElementLValue valueTarget = new ArrayElementLValue(this, values, shift, MakeArrayType(valueType), valueType);
                    valueTarget.Set(new ArrayElementLValue(this, values, next, MakeArrayType(valueType), valueType).Get(true));

                    Copy(next, shift);
                    _asm.Jump(loopLabel);
                    _asm.MarkLabel(loopEnd);

                    SetDictionaryCount(dictionary, last);
                    Copy(ConstantSlot(_boolType, true), result);
                    _asm.Jump(endLabel);

                    _asm.MarkLabel(missingLabel);
                    _asm.MarkLabel(endLabel);
                    return true;
                }

                case "Clear":
                    SetDictionaryCount(dictionary, IntConstant(0));
                    return true;

                default:
                    return false;
            }
        }

        private Slot EmitDictionaryProperty(SyntaxNode access, IPropertySymbol property, Slot dictionary)
        {
            if (property.Name == "Count")
                return DictionaryCount(dictionary);

            AddError($"Dictionary.{property.Name} is not supported by the BetterUdon compiler.", access);
            return NewSlot(property.Type, "bad");
        }
    }
}
