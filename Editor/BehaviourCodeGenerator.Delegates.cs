using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BetterUdon.Editor
{
    /// <summary>
    /// A deliberately small delegate subset: project method groups bound to Action/Func-style delegates
    /// on this behaviour. The delegate slot stores a method ID (0 means null); invoking it switches on
    /// that ID and inlines the target method. Lambdas, closures, events, multi-subscriber invocation
    /// lists and cross-behaviour delegates are not implemented yet.
    /// </summary>
    internal sealed partial class BehaviourCodeGenerator
    {
        private readonly Dictionary<IMethodSymbol, int> _delegateMethodIds =
            new Dictionary<IMethodSymbol, int>(SymbolEqualityComparer.Default);
        private readonly List<IMethodSymbol> _delegateMethods = new List<IMethodSymbol>();

        private static bool TryGetDelegateInvoke(ITypeSymbol type, out IMethodSymbol invoke)
        {
            invoke = (type as INamedTypeSymbol)?.DelegateInvokeMethod;
            return invoke != null;
        }

        private static bool IsDelegateInvoke(IMethodSymbol method)
        {
            return method != null &&
                   method.Name == "Invoke" &&
                   method.ContainingType is INamedTypeSymbol named &&
                   named.TypeKind == TypeKind.Delegate &&
                   named.DelegateInvokeMethod != null;
        }

        private void CreateEventSlots()
        {
            for (INamedTypeSymbol current = _behaviourType; current != null; current = current.BaseType)
            {
                if (current.ToDisplayString() == "BetterUdon.BetterUdonBehaviour")
                    continue;

                foreach (IEventSymbol eventSymbol in current.GetMembers().OfType<IEventSymbol>())
                {
                    if (eventSymbol.IsStatic || _fieldSlots.ContainsKey(eventSymbol))
                        continue;

                    Slot slot = _asm.AddSlot(eventSymbol.Name, "SystemObject", eventSymbol.Type);
                    _fieldSlots[eventSymbol] = slot;
                }
            }
        }

        private bool TryEmitDelegateAssignment(LValue target, ExpressionSyntax value, SyntaxNode node)
        {
            if (target == null || !TryGetDelegateInvoke(target.Type, out IMethodSymbol invoke))
                return false;

            if (value.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.NullLiteralExpression) ||
                !(GetSymbolInfo(value).Symbol is IMethodSymbol))
                return false;

            if (!TryGetDelegateMethod(value, invoke, node, out IMethodSymbol method))
                return true;

            Copy(CreateDelegateArray(GetDelegateMethodId(method)), target.Get(false));
            return true;
        }

        private bool TryEmitDelegateLocalInitializer(Slot slot, ITypeSymbol type, ExpressionSyntax value, SyntaxNode node)
        {
            if (!TryGetDelegateInvoke(type, out IMethodSymbol invoke))
                return false;

            if (value.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.NullLiteralExpression) ||
                !(GetSymbolInfo(value).Symbol is IMethodSymbol))
                return false;

            if (!TryGetDelegateMethod(value, invoke, node, out IMethodSymbol method))
                return true;

            Copy(CreateDelegateArray(GetDelegateMethodId(method)), slot);
            return true;
        }

        private bool TryGetDelegateMethod(ExpressionSyntax value, IMethodSymbol invoke, SyntaxNode node,
            out IMethodSymbol method)
        {
            method = GetSymbolInfo(value).Symbol as IMethodSymbol;

            if (method == null)
                return false;

            bool isLambda = method.MethodKind == MethodKind.AnonymousFunction;

            if ((!isLambda && method.MethodKind != MethodKind.Ordinary) || method.IsGenericMethod ||
                method.Parameters.Length != invoke.Parameters.Length ||
                !SymbolEqualityComparer.Default.Equals(method.ReturnType, invoke.ReturnType))
            {
                AddError($"Delegate target '{method.Name}' does not match the delegate signature.", node);
                return false;
            }

            if (isLambda && LambdaCaptures(method))
            {
                AddError("Capturing lambdas are not supported yet. A lambda may use fields and 'this', " +
                         "but not outer locals or parameters.", node);
                return false;
            }

            for (int i = 0; i < invoke.Parameters.Length; i++)
            {
                IParameterSymbol expected = invoke.Parameters[i];
                IParameterSymbol actual = method.Parameters[i];

                if (expected.RefKind != RefKind.None || actual.RefKind != RefKind.None ||
                    !SymbolEqualityComparer.Default.Equals(expected.Type, actual.Type))
                {
                    AddError($"Delegate target '{method.Name}' does not match parameter '{expected.Name}'.", node);
                    return false;
                }
            }

            if (method.IsStatic)
                return true;

            if (method.ContainingType == null || !DerivesFromBetterUdon(method.ContainingType) ||
                !IsOwnOrBaseType(method.ContainingType))
            {
                AddError($"Delegate target '{method.Name}' must belong to this behaviour. " +
                         "Cross-behaviour delegates are not supported yet.", node);
                return false;
            }

            return true;
        }

        private bool LambdaCaptures(IMethodSymbol lambda)
        {
            SyntaxNode node = lambda.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
            if (node == null)
                return false;

            foreach (IdentifierNameSyntax identifier in node.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                ISymbol symbol = GetSymbolInfo(identifier).Symbol;

                if (symbol is ILocalSymbol local &&
                    !SymbolEqualityComparer.Default.Equals(local.ContainingSymbol, lambda))
                    return true;

                if (symbol is IParameterSymbol parameter &&
                    !SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, lambda))
                    return true;
            }

            return false;
        }

        private int GetDelegateMethodId(IMethodSymbol method)
        {
            if (_delegateMethodIds.TryGetValue(method, out int id))
                return id;

            id = _delegateMethods.Count + 1;
            _delegateMethodIds[method] = id;
            _delegateMethods.Add(method);
            return id;
        }

        private Slot CreateDelegateArray(params int[] methodIds)
        {
            ITypeSymbol arrayType = MakeArrayType(_intType);
            Slot array = NewSlot(arrayType, "delegateIds");
            CreateArray(array, arrayType, IntConstant(methodIds.Length));

            for (int i = 0; i < methodIds.Length; i++)
            {
                ArrayElementLValue element = new ArrayElementLValue(this, array, IntConstant(i), arrayType, _intType);
                element.Set(IntConstant(methodIds[i]));
            }

            return array;
        }

        private Slot CopyDelegateArray(Slot source, int extraId = -1)
        {
            Slot length = ArrayLength(source);
            Slot newLength = extraId >= 0
                ? EmitBinaryOperator(length, IntConstant(1), _intType, "op_Addition", _intType, null)
                : length;

            ITypeSymbol arrayType = MakeArrayType(_intType);
            Slot copy = NewSlot(arrayType, "delegateIds");
            CreateArray(copy, arrayType, newLength);

            Slot index = NewSlot(_intType, "i");
            Copy(IntConstant(0), index);
            string loopLabel = NewLabel("delegateCopy");
            string endLabel = NewLabel("delegateCopyEnd");
            _asm.MarkLabel(loopLabel);

            Slot inRange = EmitBinaryOperator(index, length, _intType, "op_LessThan", _boolType, null);
            _asm.JumpIfFalse(inRange, endLabel);

            ArrayElementLValue sourceElement = new ArrayElementLValue(this, source, index, arrayType, _intType);
            ArrayElementLValue targetElement = new ArrayElementLValue(this, copy, index, arrayType, _intType);
            targetElement.Set(sourceElement.Get(false));

            Copy(EmitBinaryOperator(index, IntConstant(1), _intType, "op_Addition", _intType, null), index);
            _asm.Jump(loopLabel);
            _asm.MarkLabel(endLabel);

            if (extraId >= 0)
            {
                ArrayElementLValue last = new ArrayElementLValue(this, copy, length, arrayType, _intType);
                last.Set(IntConstant(extraId));
            }

            return copy;
        }

        private bool TryEmitDelegateAdd(LValue target, ExpressionSyntax value, bool add, SyntaxNode node)
        {
            if (target == null || !TryGetDelegateInvoke(target.Type, out IMethodSymbol invoke))
                return false;

            if (value.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.NullLiteralExpression) ||
                !(GetSymbolInfo(value).Symbol is IMethodSymbol))
                return false;

            if (!TryGetDelegateMethod(value, invoke, node, out IMethodSymbol method))
                return true;

            int id = GetDelegateMethodId(method);
            Slot current = target.Get(false);
            string endLabel = NewLabel("delegateUpdateEnd");
            Slot isNull = EmitBinaryOperator(current, ConstantSlot(_objectType, null), _objectType,
                "op_Equality", _boolType, node);
            string notNullLabel = NewLabel("delegateUpdateNotNull");

            _asm.JumpIfFalse(isNull, notNullLabel);

            if (add)
                Copy(CreateDelegateArray(id), target.Get(false));

            _asm.Jump(endLabel);
            _asm.MarkLabel(notNullLabel);

            if (add)
            {
                Copy(CopyDelegateArray(current, id), target.Get(false));
            }
            else
            {
                Slot length = ArrayLength(current);
                Slot index = EmitDictionaryFindInt(current, id, node);
                string missingLabel = NewLabel("delegateRemoveMissing");
                string foundLabel = NewLabel("delegateRemoveFound");
                Slot found = EmitBinaryOperator(index, IntConstant(0), _intType, "op_GreaterThanOrEqual",
                    _boolType, node);
                _asm.JumpIfFalse(found, missingLabel);
                _asm.Jump(foundLabel);
                _asm.MarkLabel(missingLabel);
                _asm.Jump(endLabel);
                _asm.MarkLabel(foundLabel);

                Slot one = IntConstant(1);
                Slot shorter = EmitBinaryOperator(length, one, _intType, "op_Subtraction", _intType, node);
                Slot isEmpty = EmitBinaryOperator(shorter, IntConstant(0), _intType, "op_LessThanOrEqual",
                    _boolType, node);
                string notEmptyLabel = NewLabel("delegateRemoveNotEmpty");
                _asm.JumpIfFalse(isEmpty, notEmptyLabel);
                Copy(ConstantSlot(_objectType, null), target.Get(false));
                _asm.Jump(endLabel);
                _asm.MarkLabel(notEmptyLabel);

                ITypeSymbol arrayType = MakeArrayType(_intType);
                Slot copy = NewSlot(arrayType, "delegateIds");
                CreateArray(copy, arrayType, shorter);
                Slot sourceIndex = NewSlot(_intType, "src");
                Slot targetIndex = NewSlot(_intType, "dst");
                Copy(IntConstant(0), sourceIndex);
                Copy(IntConstant(0), targetIndex);

                string loopLabel = NewLabel("delegateRemoveLoop");
                string loopEnd = NewLabel("delegateRemoveEnd");
                _asm.MarkLabel(loopLabel);
                Slot inRange = EmitBinaryOperator(sourceIndex, length, _intType, "op_LessThan", _boolType, node);
                _asm.JumpIfFalse(inRange, loopEnd);
                Slot skip = EmitBinaryOperator(sourceIndex, index, _intType, "op_Equality", _boolType, node);
                string copyLabel = NewLabel("delegateRemoveCopy");
                string nextLabel = NewLabel("delegateRemoveNext");
                _asm.JumpIfFalse(skip, copyLabel);
                Copy(EmitBinaryOperator(sourceIndex, IntConstant(1), _intType, "op_Addition", _intType, node), sourceIndex);
                _asm.Jump(nextLabel);
                _asm.MarkLabel(copyLabel);

                ArrayElementLValue sourceElement = new ArrayElementLValue(this, current, sourceIndex, arrayType, _intType);
                ArrayElementLValue targetElement = new ArrayElementLValue(this, copy, targetIndex, arrayType, _intType);
                targetElement.Set(sourceElement.Get(false));
                Copy(EmitBinaryOperator(targetIndex, IntConstant(1), _intType, "op_Addition", _intType, node), targetIndex);
                Copy(EmitBinaryOperator(sourceIndex, IntConstant(1), _intType, "op_Addition", _intType, node), sourceIndex);

                _asm.MarkLabel(nextLabel);
                _asm.Jump(loopLabel);
                _asm.MarkLabel(loopEnd);
                Copy(copy, target.Get(false));
            }

            _asm.MarkLabel(endLabel);
            return true;
        }

        private Slot EmitDictionaryFindInt(Slot array, int value, SyntaxNode node)
        {
            ITypeSymbol arrayType = MakeArrayType(_intType);
            Slot result = NewSlot(_intType, "findId");
            Copy(IntConstant(-1), result);
            Slot length = ArrayLength(array);
            Slot index = NewSlot(_intType, "i");
            Copy(IntConstant(0), index);

            string loopLabel = NewLabel("findIdLoop");
            string nextLabel = NewLabel("findIdNext");
            string endLabel = NewLabel("findIdEnd");
            _asm.MarkLabel(loopLabel);
            Slot inRange = EmitBinaryOperator(index, length, _intType, "op_LessThan", _boolType, node);
            _asm.JumpIfFalse(inRange, endLabel);

            ArrayElementLValue element = new ArrayElementLValue(this, array, index, arrayType, _intType);
            Slot equal = EmitBinaryOperator(element.Get(false), IntConstant(value), _intType,
                "op_Equality", _boolType, node);
            _asm.JumpIfFalse(equal, nextLabel);
            Copy(index, result);
            _asm.Jump(endLabel);

            _asm.MarkLabel(nextLabel);
            Copy(EmitBinaryOperator(index, IntConstant(1), _intType, "op_Addition", _intType, node), index);
            _asm.Jump(loopLabel);
            _asm.MarkLabel(endLabel);
            return result;
        }

        private Slot EmitDelegateInvocation(IMethodSymbol invoke, InvocationExpressionSyntax invocation)
        {
            if (!TryGetDelegateInvoke(invoke.ContainingType, out IMethodSymbol signature))
                return null;

            List<Slot> arguments = invocation.ArgumentList.Arguments
                .Select(argument => EmitExpression(argument.Expression))
                .ToList();

            if (arguments.Count != signature.Parameters.Length)
            {
                AddError($"Delegate invocation expects {signature.Parameters.Length} argument(s).", invocation);
                return null;
            }

            ExpressionSyntax receiverSyntax = invocation.Expression is MemberAccessExpressionSyntax memberAccess
                ? memberAccess.Expression
                : invocation.Expression;

            Slot ids = EmitExpression(receiverSyntax);
            if (ids == null)
                return null;

            Slot result = signature.ReturnsVoid ? null : NewSlot(signature.ReturnType, "delegateResult");
            if (result != null)
                EmitDefaultValue(result, signature.ReturnType);

            string endLabel = NewLabel("delegateEnd");
            string notNullLabel = NewLabel("delegateNotNull");
            Slot isNull = EmitBinaryOperator(ids, ConstantSlot(_objectType, null), _objectType,
                "op_Equality", _boolType, invocation);
            _asm.JumpIfFalse(isNull, notNullLabel);
            _asm.Jump(endLabel);
            _asm.MarkLabel(notNullLabel);

            ITypeSymbol idsType = MakeArrayType(_intType);
            Slot length = ArrayLength(ids);
            Slot index = NewSlot(_intType, "delegateIndex");
            Copy(IntConstant(0), index);

            string loopLabel = NewLabel("delegateLoop");
            string incrementLabel = NewLabel("delegateIncrement");
            _asm.MarkLabel(loopLabel);
            Slot inRange = EmitBinaryOperator(index, length, _intType, "op_LessThan", _boolType, invocation);
            _asm.JumpIfFalse(inRange, endLabel);

            ArrayElementLValue idElement = new ArrayElementLValue(this, ids, index, idsType, _intType);
            Slot id = idElement.Get(false);
            string nextLabel = NewLabel("delegateNext");

            foreach (IMethodSymbol method in _delegateMethods.ToList())
            {
                if (method.Parameters.Length != signature.Parameters.Length ||
                    !SymbolEqualityComparer.Default.Equals(method.ReturnType, signature.ReturnType))
                    continue;

                Slot matches = EmitBinaryOperator(id, IntConstant(GetDelegateMethodId(method)),
                    _intType, "op_Equality", _boolType, invocation);
                string notThisLabel = NewLabel("delegateNotThis");
                _asm.JumpIfFalse(matches, notThisLabel);

                Slot value = InlineMethod(method, method.IsStatic ? null : _thisSlot, arguments, invocation);

                if (result != null && value != null)
                    Copy(ConvertTo(value, result.Type), result);

                _asm.Jump(nextLabel);
                _asm.MarkLabel(notThisLabel);
            }

            _asm.MarkLabel(nextLabel);
            _asm.MarkLabel(incrementLabel);
            Copy(EmitBinaryOperator(index, IntConstant(1), _intType, "op_Addition", _intType, invocation), index);
            _asm.Jump(loopLabel);
            _asm.MarkLabel(endLabel);
            return result;
        }
    }
}
