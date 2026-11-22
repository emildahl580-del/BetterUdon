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

        private sealed class PropertyDispatchTarget
        {
            public INamedTypeSymbol Type;
            public IPropertySymbol Property;
        }

        private List<INamedTypeSymbol> _dispatchBehaviourTypes;
        private readonly Dictionary<IMethodSymbol, List<DispatchTarget>> _dispatchTargets =
            new Dictionary<IMethodSymbol, List<DispatchTarget>>(SymbolEqualityComparer.Default);
        private readonly Dictionary<IPropertySymbol, List<PropertyDispatchTarget>> _propertyDispatchTargets =
            new Dictionary<IPropertySymbol, List<PropertyDispatchTarget>>(SymbolEqualityComparer.Default);

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

        private bool NeedsClosedWorldPropertyDispatch(IPropertySymbol property, ExpressionSyntax receiver)
        {
            if (property == null || property.IsStatic ||
                !(property.ContainingType is INamedTypeSymbol containing))
                return false;

            if (containing.TypeKind == TypeKind.Struct)
                return false;

            if (receiver is BaseExpressionSyntax)
                return false;

            bool polymorphic = containing.TypeKind == TypeKind.Interface ||
                               ((property.IsVirtual || property.IsAbstract || property.IsOverride) &&
                                !property.IsSealed);

            if (!polymorphic)
                return false;

            return property.DeclaringSyntaxReferences.Length > 0 &&
                   !UdonTypeSystem.IsExternAssembly(property.ContainingAssembly);
        }

        private Slot EmitClosedWorldPropertyGet(IPropertySymbol property, Slot receiver, SyntaxNode node,
            IReadOnlyList<Slot> indexArguments = null)
        {
            List<PropertyDispatchTarget> targets = GetPropertyDispatchTargets(property);

            if (targets.Count == 0)
            {
                AddError($"No concrete BetterUdon implementation of property '{property.Name}' was found for " +
                         "closed-world dispatch.", node);
                return NewSlot(property.Type, "bad");
            }

            Slot result = NewSlot(property.Type, "dispatchProperty");
            EmitDefaultValue(result, property.Type);
            string endLabel = NewLabel("propDispatchEnd");

            Slot isNull = EmitBinaryOperator(receiver, ConstantSlot(_objectType, null), _objectType,
                "op_Equality", _boolType, node);
            string notNull = NewLabel("propDispatchNotNull");
            _asm.JumpIfFalse(isNull, notNull);
            _asm.Jump(endLabel);
            _asm.MarkLabel(notNull);

            Slot marker = EmitGetProgramVariable(receiver, TypeMarkerSymbol, "property dispatch type");

            foreach (PropertyDispatchTarget target in targets)
            {
                Slot expected = ConstantSlot(_stringType, target.Type.ToDisplayString());
                Slot matches = EmitBinaryOperator(marker, expected, _stringType, "op_Equality", _boolType, node);
                string nextLabel = NewLabel("propDispatchNext");
                _asm.JumpIfFalse(matches, nextLabel);

                Slot value = null;

                if (target.Property.GetMethod != null && GetBackingField(target.Property) != null &&
                    target.Property.GetMethod.DeclaringSyntaxReferences.Length == 0)
                {
                    value = EmitReadBehaviourMember(receiver, target.Property.Name, target.Property.Type, node);
                }
                else if (target.Property.GetMethod != null &&
                         target.Property.GetMethod.DeclaringSyntaxReferences.Length > 0)
                {
                    value = InlineRemoteMethod(target.Property.GetMethod, receiver,
                        indexArguments != null ? new List<Slot>(indexArguments) : new List<Slot>(), node);
                }
                else if (target.Property.GetMethod != null)
                {
                    value = EmitReadBehaviourMember(receiver, target.Property.Name, target.Property.Type, node);
                }

                if (value != null)
                    Copy(ConvertTo(value, result.Type), result);

                _asm.Jump(endLabel);
                _asm.MarkLabel(nextLabel);
            }

            _asm.MarkLabel(endLabel);
            return result;
        }

        private void EmitClosedWorldPropertySet(IPropertySymbol property, Slot receiver, Slot value, SyntaxNode node,
            IReadOnlyList<Slot> indexArguments = null)
        {
            List<PropertyDispatchTarget> targets = GetPropertyDispatchTargets(property);

            if (targets.Count == 0)
            {
                AddError($"No concrete BetterUdon implementation of property '{property.Name}' was found for " +
                         "closed-world dispatch.", node);
                return;
            }

            string endLabel = NewLabel("propSetDispatchEnd");

            Slot isNull = EmitBinaryOperator(receiver, ConstantSlot(_objectType, null), _objectType,
                "op_Equality", _boolType, node);
            string notNull = NewLabel("propSetDispatchNotNull");
            _asm.JumpIfFalse(isNull, notNull);
            _asm.Jump(endLabel);
            _asm.MarkLabel(notNull);

            Slot marker = EmitGetProgramVariable(receiver, TypeMarkerSymbol, "property dispatch type");

            foreach (PropertyDispatchTarget target in targets)
            {
                Slot expected = ConstantSlot(_stringType, target.Type.ToDisplayString());
                Slot matches = EmitBinaryOperator(marker, expected, _stringType, "op_Equality", _boolType, node);
                string nextLabel = NewLabel("propSetDispatchNext");
                _asm.JumpIfFalse(matches, nextLabel);

                if (target.Property.SetMethod != null && GetBackingField(target.Property) != null &&
                    target.Property.SetMethod.DeclaringSyntaxReferences.Length == 0)
                {
                    EmitWriteBehaviourMember(receiver, target.Property.Name,
                        ConvertTo(value, target.Property.Type));
                }
                else if (target.Property.SetMethod != null &&
                         target.Property.SetMethod.DeclaringSyntaxReferences.Length > 0)
                {
                    List<Slot> setterArguments = new List<Slot>();
                    if (indexArguments != null)
                        setterArguments.AddRange(indexArguments);
                    setterArguments.Add(value);

                    InlineRemoteMethod(target.Property.SetMethod, receiver, setterArguments, node);
                }
                else if (target.Property.SetMethod != null)
                {
                    EmitWriteBehaviourMember(receiver, target.Property.Name,
                        ConvertTo(value, target.Property.Type));
                }

                _asm.Jump(endLabel);
                _asm.MarkLabel(nextLabel);
            }

            _asm.MarkLabel(endLabel);
        }

        private List<PropertyDispatchTarget> GetPropertyDispatchTargets(IPropertySymbol property)
        {
            if (_propertyDispatchTargets.TryGetValue(property, out List<PropertyDispatchTarget> cached))
                return cached;

            List<PropertyDispatchTarget> targets = new List<PropertyDispatchTarget>();

            foreach (INamedTypeSymbol type in GetDispatchBehaviourTypes())
            {
                IPropertySymbol implementation = FindPropertyImplementation(type, property);

                if (implementation == null || implementation.IsAbstract)
                    continue;

                bool hasGetter = implementation.GetMethod != null &&
                                 (implementation.GetMethod.DeclaringSyntaxReferences.Length > 0 ||
                                  GetBackingField(implementation) != null);
                bool hasSetter = implementation.SetMethod != null &&
                                 (implementation.SetMethod.DeclaringSyntaxReferences.Length > 0 ||
                                  GetBackingField(implementation) != null);

                if (!hasGetter && !hasSetter)
                    continue;

                targets.Add(new PropertyDispatchTarget { Type = type, Property = implementation });
            }

            _propertyDispatchTargets[property] = targets;
            return targets;
        }

        private IPropertySymbol FindPropertyImplementation(INamedTypeSymbol type, IPropertySymbol property)
        {
            if (property.ContainingType.TypeKind == TypeKind.Interface)
            {
                IPropertySymbol implementation =
                    type.FindImplementationForInterfaceMember(property) as IPropertySymbol;

                if (implementation == null || implementation == property)
                    return null;

                return FindMostDerivedPropertyOverride(type, implementation) ?? implementation;
            }

            IPropertySymbol mostDerived = FindMostDerivedPropertyOverride(type, property);
            if (mostDerived != null)
                return mostDerived;

            return property.IsAbstract ? null : property;
        }

        private static IPropertySymbol FindMostDerivedPropertyOverride(INamedTypeSymbol type, IPropertySymbol property)
        {
            for (INamedTypeSymbol current = type; current != null; current = current.BaseType)
            {
                foreach (IPropertySymbol candidate in current.GetMembers(property.Name).OfType<IPropertySymbol>())
                {
                    if (!candidate.IsOverride)
                        continue;

                    if (SymbolEqualityComparer.Default.Equals(candidate.Type, property.Type) &&
                        candidate.Parameters.Length == property.Parameters.Length)
                    {
                        bool sameParameters = true;
                        for (int i = 0; i < candidate.Parameters.Length; i++)
                        {
                            if (!SymbolEqualityComparer.Default.Equals(candidate.Parameters[i].Type,
                                    property.Parameters[i].Type))
                            {
                                sameParameters = false;
                                break;
                            }
                        }

                        if (sameParameters)
                            return candidate;
                    }
                }
            }

            return null;
        }

        private Slot EmitTypeTest(BinaryExpressionSyntax binary, bool asExpression)
        {
            ITypeSymbol targetType = Substitute(GetTypeInfo(binary.Right).Type);
            ITypeSymbol sourceType = GetType(binary.Left);
            Slot value = EmitExpression(binary.Left);

            if (targetType == null)
            {
                AddError($"Unable to resolve the type in '{binary}'.", binary);
                return asExpression ? NewSlot(_objectType, "bad") : NewSlot(_boolType, "bad");
            }

            Slot result = asExpression ? NewSlot(targetType, "asType") : NewSlot(_boolType, "isType");

            if (asExpression)
                EmitDefaultValue(result, targetType);
            else
                Copy(ConstantSlot(_boolType, false), result);

            if (value == null)
                return result;

            if (targetType.SpecialType == SpecialType.System_Object)
            {
                if (asExpression)
                    Copy(ConvertTo(value, targetType), result);
                else
                    Copy(EmitBinaryOperator(value, ConstantSlot(_objectType, null), _objectType,
                        "op_Inequality", _boolType, binary), result);

                return result;
            }

            if (UdonTypeSystem.IsUserStruct(targetType))
            {
                if (asExpression)
                {
                    AddError("'as' cannot be used with a struct type.", binary);
                    return result;
                }

                bool same = SymbolEqualityComparer.Default.Equals(sourceType, targetType);
                Copy(ConstantSlot(_boolType, same), result);
                return result;
            }

            bool isBehaviourType = targetType.TypeKind == TypeKind.Interface ||
                                   DerivesFromBetterUdon(targetType);

            if (!isBehaviourType)
            {
                if (asExpression && targetType.IsValueType)
                {
                    AddError("'as' cannot be used with a value type.", binary);
                    return result;
                }

                bool same = IsAssignableTo(sourceType, targetType);
                if (same)
                    Copy(asExpression ? ConvertTo(value, targetType) : ConstantSlot(_boolType, true), result);

                return result;
            }

            string endLabel = NewLabel("typeTestEnd");
            string notNull = NewLabel("typeTestNotNull");
            Slot isNull = EmitBinaryOperator(value, ConstantSlot(_objectType, null), _objectType,
                "op_Equality", _boolType, binary);
            _asm.JumpIfFalse(isNull, notNull);
            _asm.Jump(endLabel);
            _asm.MarkLabel(notNull);

            Slot marker = EmitGetProgramVariable(value, TypeMarkerSymbol, "type test");
            bool emitted = false;

            foreach (INamedTypeSymbol candidate in GetDispatchBehaviourTypes())
            {
                if (!IsAssignableTo(candidate, targetType))
                    continue;

                Slot expected = ConstantSlot(_stringType, candidate.ToDisplayString());
                Slot matches = EmitBinaryOperator(marker, expected, _stringType, "op_Equality", _boolType, binary);
                string next = NewLabel("typeTestNext");
                _asm.JumpIfFalse(matches, next);

                if (asExpression)
                    Copy(ConvertTo(value, targetType), result);
                else
                    Copy(ConstantSlot(_boolType, true), result);

                _asm.Jump(endLabel);
                _asm.MarkLabel(next);
                emitted = true;
            }

            if (!emitted && sourceType != null && IsAssignableTo(sourceType, targetType))
            {
                if (asExpression)
                    Copy(ConvertTo(value, targetType), result);
                else
                    Copy(ConstantSlot(_boolType, true), result);
            }

            _asm.MarkLabel(endLabel);
            return result;
        }

        private static bool IsAssignableTo(ITypeSymbol type, ITypeSymbol target)
        {
            if (type == null || target == null)
                return false;

            if (SymbolEqualityComparer.Default.Equals(type, target))
                return true;

            for (INamedTypeSymbol current = (type as INamedTypeSymbol)?.BaseType; current != null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current, target))
                    return true;
            }

            if (target.TypeKind == TypeKind.Interface && type is INamedTypeSymbol named)
            {
                foreach (INamedTypeSymbol implemented in named.AllInterfaces)
                {
                    if (SymbolEqualityComparer.Default.Equals(implemented, target))
                        return true;
                }
            }

            return false;
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
