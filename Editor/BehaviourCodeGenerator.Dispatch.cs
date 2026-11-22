using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BetterUdon.Editor
{
    /// <summary>
    /// Closed-world dispatch for virtual and interface calls.
    ///
    /// Every BetterUdon behaviour in the project is visible to the compiler, so a polymorphic call can
    /// be turned into a type-marker switch. Each branch inlines the concrete implementation with the
    /// receiver bound as <c>this</c>, which keeps the call synchronous and preserves return values.
    /// </summary>
    internal sealed partial class BehaviourCodeGenerator
    {
        private sealed class DispatchTarget
        {
            public INamedTypeSymbol Type;
            public IMethodSymbol Method;
        }

        private List<INamedTypeSymbol> _dispatchBehaviourTypes;
        private readonly Dictionary<IMethodSymbol, List<DispatchTarget>> _dispatchTargets =
            new Dictionary<IMethodSymbol, List<DispatchTarget>>(SymbolEqualityComparer.Default);

        private bool NeedsClosedWorldDispatch(IMethodSymbol method, InvocationExpressionSyntax invocation)
        {
            if (method == null || method.IsStatic || method.IsGenericMethod)
                return false;

            if (!(method.ContainingType is INamedTypeSymbol containing))
                return false;

            if (containing.TypeKind == TypeKind.Struct)
                return false;

            // base.Method() is a statically bound call, not virtual dispatch.
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Expression is BaseExpressionSyntax)
                return false;

            bool polymorphic = containing.TypeKind == TypeKind.Interface ||
                               ((method.IsVirtual || method.IsAbstract || method.IsOverride) && !method.IsSealed);

            if (!polymorphic)
                return false;

            // Only project-declared members can be dispatched; BCL virtuals still take the normal
            // unsupported-extern path.
            return method.DeclaringSyntaxReferences.Length > 0 &&
                   !UdonTypeSystem.IsExternAssembly(method.ContainingAssembly);
        }

        private Slot EmitClosedWorldDispatch(IMethodSymbol method, Slot instance, List<Slot> arguments, SyntaxNode node)
        {
            if (method.Parameters.Any(p => p.RefKind != RefKind.None))
            {
                AddError($"'{method.Name}' is virtual or an interface method and has ref/out/in parameters. " +
                         "Closed-world dispatch currently requires value parameters.", node);
                return method.ReturnsVoid ? null : NewSlot(method.ReturnType, "bad");
            }

            List<DispatchTarget> targets = GetDispatchTargets(method);

            if (targets.Count == 0)
            {
                AddError($"No concrete BetterUdon implementation of '{method.Name}' was found for closed-world " +
                         "dispatch. Mark the receiver's type as a BetterUdon behaviour or add an implementation.",
                    node);
                return method.ReturnsVoid ? null : NewSlot(method.ReturnType, "bad");
            }

            Slot result = method.ReturnsVoid ? null : NewSlot(method.ReturnType, "dispatch");
            string endLabel = NewLabel("dispatchEnd");

            // A null receiver behaves like a null reference call: no branch runs.
            Slot isNull = EmitBinaryOperator(instance, ConstantSlot(_objectType, null), _objectType,
                "op_Equality", _boolType, node);
            string notNull = NewLabel("dispatchNotNull");
            _asm.JumpIfFalse(isNull, notNull);
            _asm.Jump(endLabel);
            _asm.MarkLabel(notNull);

            Slot marker = EmitGetProgramVariable(instance, TypeMarkerSymbol, "dispatch type");

            foreach (DispatchTarget target in targets)
            {
                Slot expected = ConstantSlot(_stringType, target.Type.ToDisplayString());
                Slot matches = EmitBinaryOperator(marker, expected, _stringType, "op_Equality", _boolType, node);
                string nextLabel = NewLabel("dispatchNext");

                _asm.JumpIfFalse(matches, nextLabel);

                Slot branchResult = InlineRemoteMethod(target.Method, instance, arguments, node);

                if (result != null && branchResult != null)
                    Copy(ConvertTo(branchResult, result.Type), result);

                _asm.Jump(endLabel);
                _asm.MarkLabel(nextLabel);
            }

            _asm.MarkLabel(endLabel);
            return result;
        }

        private List<DispatchTarget> GetDispatchTargets(IMethodSymbol method)
        {
            if (_dispatchTargets.TryGetValue(method, out List<DispatchTarget> cached))
                return cached;

            List<DispatchTarget> targets = new List<DispatchTarget>();

            foreach (INamedTypeSymbol type in GetDispatchBehaviourTypes())
            {
                IMethodSymbol implementation = FindDispatchImplementation(type, method);

                if (implementation == null || implementation.IsAbstract ||
                    implementation.DeclaringSyntaxReferences.Length == 0)
                    continue;

                targets.Add(new DispatchTarget { Type = type, Method = implementation });
            }

            _dispatchTargets[method] = targets;
            return targets;
        }

        private IMethodSymbol FindDispatchImplementation(INamedTypeSymbol type, IMethodSymbol method)
        {
            if (method.ContainingType.TypeKind == TypeKind.Interface)
            {
                IMethodSymbol implementation = type.FindImplementationForInterfaceMember(method) as IMethodSymbol;
                if (implementation == null || implementation == method)
                    return null;

                // FindImplementationForInterfaceMember can point at the base implementation even when
                // a derived type overrides it. Pick the most-derived override for this exact type.
                return FindMostDerivedOverride(type, implementation) ?? implementation;
            }

            IMethodSymbol mostDerived = FindMostDerivedOverride(type, method);
            if (mostDerived != null)
                return mostDerived;

            return method.IsAbstract ? null : method;
        }

        private static IMethodSymbol FindMostDerivedOverride(INamedTypeSymbol type, IMethodSymbol method)
        {
            for (INamedTypeSymbol current = type; current != null; current = current.BaseType)
            {
                foreach (IMethodSymbol candidate in current.GetMembers(method.Name).OfType<IMethodSymbol>())
                {
                    if (candidate.MethodKind != MethodKind.Ordinary || !candidate.IsOverride)
                        continue;

                    if (HasSameSignature(candidate, method))
                        return candidate;
                }
            }

            return null;
        }

        private static bool HasSameSignature(IMethodSymbol left, IMethodSymbol right)
        {
            if (left.Parameters.Length != right.Parameters.Length)
                return false;

            for (int i = 0; i < left.Parameters.Length; i++)
            {
                if (!SymbolEqualityComparer.Default.Equals(left.Parameters[i].Type, right.Parameters[i].Type))
                    return false;
            }

            return true;
        }

        private List<INamedTypeSymbol> GetDispatchBehaviourTypes()
        {
            if (_dispatchBehaviourTypes != null)
                return _dispatchBehaviourTypes;

            _dispatchBehaviourTypes = new List<INamedTypeSymbol>();
            CollectBehaviourTypes(_model.Compilation.Assembly.GlobalNamespace, _dispatchBehaviourTypes);
            return _dispatchBehaviourTypes;
        }

        private static void CollectBehaviourTypes(INamespaceSymbol namespaceSymbol, List<INamedTypeSymbol> types)
        {
            foreach (INamespaceOrTypeSymbol member in namespaceSymbol.GetMembers())
            {
                if (member is INamespaceSymbol childNamespace)
                {
                    CollectBehaviourTypes(childNamespace, types);
                    continue;
                }

                if (!(member is INamedTypeSymbol type))
                    continue;

                if (type.TypeKind == TypeKind.Class && !type.IsAbstract &&
                    type.DeclaringSyntaxReferences.Length > 0 && DerivesFromBetterUdon(type))
                {
                    types.Add(type);
                }

                foreach (INamedTypeSymbol nested in type.GetTypeMembers())
                {
                    if (nested.TypeKind == TypeKind.Class && !nested.IsAbstract &&
                        nested.DeclaringSyntaxReferences.Length > 0 && DerivesFromBetterUdon(nested))
                    {
                        types.Add(nested);
                    }
                }
            }
        }
    }
}
