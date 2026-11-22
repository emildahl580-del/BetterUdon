using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BetterUdon.Editor
{
    /// <summary>
    /// A deliberately small delegate subset: <c>System.Action</c> bound to a parameterless method on
    /// this behaviour. The delegate slot stores a method ID (0 means null); invoking it switches on
    /// that ID and inlines the target method. Lambdas, closures, events, multi-subscriber invocation
    /// lists and cross-behaviour delegates are not implemented yet.
    /// </summary>
    internal sealed partial class BehaviourCodeGenerator
    {
        private readonly Dictionary<IMethodSymbol, int> _delegateMethodIds =
            new Dictionary<IMethodSymbol, int>(SymbolEqualityComparer.Default);
        private readonly List<IMethodSymbol> _delegateMethods = new List<IMethodSymbol>();

        private static bool IsActionType(ITypeSymbol type)
        {
            return type is INamedTypeSymbol named &&
                   named.TypeKind == TypeKind.Delegate &&
                   named.ConstructedFrom?.ToDisplayString() == "System.Action";
        }

        private static bool IsActionInvoke(IMethodSymbol method)
        {
            return method != null &&
                   method.Name == "Invoke" &&
                   method.ContainingType is INamedTypeSymbol named &&
                   named.TypeKind == TypeKind.Delegate &&
                   named.ConstructedFrom?.ToDisplayString() == "System.Action";
        }

        private bool TryEmitDelegateAssignment(LValue target, ExpressionSyntax value, SyntaxNode node)
        {
            if (target == null || !IsActionType(target.Type))
                return false;

            if (value.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.NullLiteralExpression) ||
                !(GetSymbolInfo(value).Symbol is IMethodSymbol))
                return false;

            if (!TryGetDelegateMethod(value, node, out IMethodSymbol method))
                return true;

            int id = GetDelegateMethodId(method);
            Copy(IntConstant(id), target.Get(false));
            return true;
        }

        private bool TryEmitDelegateLocalInitializer(Slot slot, ITypeSymbol type, ExpressionSyntax value, SyntaxNode node)
        {
            if (!IsActionType(type))
                return false;

            if (value.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.NullLiteralExpression) ||
                !(GetSymbolInfo(value).Symbol is IMethodSymbol))
                return false;

            if (!TryGetDelegateMethod(value, node, out IMethodSymbol method))
                return true;

            Copy(IntConstant(GetDelegateMethodId(method)), slot);
            return true;
        }

        private bool TryGetDelegateMethod(ExpressionSyntax value, SyntaxNode node, out IMethodSymbol method)
        {
            method = GetSymbolInfo(value).Symbol as IMethodSymbol;

            if (method == null)
            {
                // null is a valid delegate value; let the normal assignment path handle it.
                return false;
            }

            if (method.MethodKind != MethodKind.Ordinary || method.IsGenericMethod ||
                method.Parameters.Length != 0 || !method.ReturnsVoid)
            {
                AddError($"Delegate target '{method.Name}' must be a parameterless, non-generic void method.",
                    node);
                return false;
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
            if (invoke.Parameters.Length != 0)
            {
                AddError("Only parameterless System.Action delegates are supported so far.", invocation);
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

            string endLabel = NewLabel("delegateEnd");
            string dispatchLabel = NewLabel("delegateDispatch");

            Slot isNull = EmitBinaryOperator(id, IntConstant(0), _intType, "op_Equality", _boolType, invocation);
            _asm.JumpIfFalse(isNull, dispatchLabel);
            _asm.Jump(endLabel);
            _asm.MarkLabel(dispatchLabel);

            foreach (IMethodSymbol method in _delegateMethods.ToList())
            {
                Slot matches = EmitBinaryOperator(id, IntConstant(GetDelegateMethodId(method)),
                    _intType, "op_Equality", _boolType, invocation);
                string nextLabel = NewLabel("delegateNext");
                _asm.JumpIfFalse(matches, nextLabel);

                InlineMethod(method, method.IsStatic ? null : _thisSlot, new List<Slot>(), invocation);
                _asm.Jump(endLabel);

                _asm.MarkLabel(nextLabel);
            }

            _asm.MarkLabel(endLabel);
            return null;
        }
    }
}
