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

        private bool TryEmitDelegateAssignment(LValue target, ExpressionSyntax value, SyntaxNode node)
        {
            if (target == null || !TryGetDelegateInvoke(target.Type, out IMethodSymbol invoke))
                return false;

            if (value.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.NullLiteralExpression) ||
                !(GetSymbolInfo(value).Symbol is IMethodSymbol))
                return false;

            if (!TryGetDelegateMethod(value, invoke, node, out IMethodSymbol method))
                return true;

            Copy(IntConstant(GetDelegateMethodId(method)), target.Get(false));
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

            Copy(IntConstant(GetDelegateMethodId(method)), slot);
            return true;
        }

        private bool TryGetDelegateMethod(ExpressionSyntax value, IMethodSymbol invoke, SyntaxNode node,
            out IMethodSymbol method)
        {
            method = GetSymbolInfo(value).Symbol as IMethodSymbol;

            if (method == null)
                return false;

            if (method.MethodKind != MethodKind.Ordinary || method.IsGenericMethod ||
                method.Parameters.Length != invoke.Parameters.Length ||
                !SymbolEqualityComparer.Default.Equals(method.ReturnType, invoke.ReturnType))
            {
                AddError($"Delegate target '{method.Name}' does not match the delegate signature.", node);
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

        private int GetDelegateMethodId(IMethodSymbol method)
        {
            if (_delegateMethodIds.TryGetValue(method, out int id))
                return id;

            id = _delegateMethods.Count + 1;
            _delegateMethodIds[method] = id;
            _delegateMethods.Add(method);
            return id;
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

            Slot receiver = EmitExpression(receiverSyntax);
            if (receiver == null)
                return null;

            Slot id = NewSlot(_intType, "delegateId");
            Copy(receiver, id);

            Slot result = signature.ReturnsVoid ? null : NewSlot(signature.ReturnType, "delegateResult");
            if (result != null)
                EmitDefaultValue(result, signature.ReturnType);

            string endLabel = NewLabel("delegateEnd");
            string dispatchLabel = NewLabel("delegateDispatch");

            Slot isNull = EmitBinaryOperator(id, IntConstant(0), _intType, "op_Equality", _boolType, invocation);
            _asm.JumpIfFalse(isNull, dispatchLabel);
            _asm.Jump(endLabel);
            _asm.MarkLabel(dispatchLabel);

            foreach (IMethodSymbol method in _delegateMethods.ToList())
            {
                if (method.Parameters.Length != signature.Parameters.Length ||
                    !SymbolEqualityComparer.Default.Equals(method.ReturnType, signature.ReturnType))
                    continue;

                Slot matches = EmitBinaryOperator(id, IntConstant(GetDelegateMethodId(method)),
                    _intType, "op_Equality", _boolType, invocation);
                string nextLabel = NewLabel("delegateNext");
                _asm.JumpIfFalse(matches, nextLabel);

                Slot value = InlineMethod(method, method.IsStatic ? null : _thisSlot, arguments, invocation);

                if (result != null && value != null)
                    Copy(ConvertTo(value, result.Type), result);

                _asm.Jump(endLabel);
                _asm.MarkLabel(nextLabel);
            }

            _asm.MarkLabel(endLabel);
            return result;
        }
    }
}
