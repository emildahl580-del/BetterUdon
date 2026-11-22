using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BetterUdon.Editor
{
    internal sealed partial class BehaviourCodeGenerator
    {
        #region Types

        private ITypeSymbol _intType => _model.Compilation.GetSpecialType(SpecialType.System_Int32);
        private ITypeSymbol _boolType => _model.Compilation.GetSpecialType(SpecialType.System_Boolean);
        private ITypeSymbol _objectType => _model.Compilation.GetSpecialType(SpecialType.System_Object);
        private ITypeSymbol _stringType => _model.Compilation.GetSpecialType(SpecialType.System_String);

        private Slot ArrayLength(Slot collection)
        {
            Slot result = NewSlot(_intType, "length");
            _asm.Push(collection);
            _asm.Push(result);
            Extern("SystemArray.__get_Length__SystemInt32", "array length");
            return result;
        }

        private Slot PropertyAccess(Slot collection, ITypeSymbol collectionType, string propertyName, ITypeSymbol resultType, SyntaxNode node)
        {
            if (propertyName == "Length" && collectionType is IArrayTypeSymbol)
                return ArrayLength(collection);

            AddError($"Property '{propertyName}' is not readable by the BetterUdon compiler.", node);
            return NewSlot(resultType, "bad");
        }

        #endregion

        #region Expressions

        private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
        {
            while (expression is ParenthesizedExpressionSyntax parenthesized)
                expression = parenthesized.Expression;

            return expression;
        }

        private Slot EmitExpression(ExpressionSyntax expression)
        {
            if (expression == null)
                return null;

            expression = Unwrap(expression);

            // nameof(...) is an invocation node but a compile time string.
            if (expression is InvocationExpressionSyntax nameOf &&
                nameOf.Expression is IdentifierNameSyntax nameOfIdentifier &&
                nameOfIdentifier.Identifier.ValueText == "nameof")
            {
                Optional<object> nameOfValue = GetConstantValue(nameOf);
                object text = nameOfValue.HasValue ? nameOfValue.Value : nameOfIdentifier.Identifier.ValueText;
                return EmitConstant(null, text, _stringType);
            }

            if (expression is DefaultExpressionSyntax defaultExpression)
            {
                ITypeSymbol defaultType = GetType(defaultExpression) ?? GetTypeInfo(defaultExpression.Type).Type;
                Slot slot = NewSlot(defaultType, "default");
                EmitDefaultValue(slot, defaultType);
                return slot;
            }

            if (expression is ThisExpressionSyntax || expression is BaseExpressionSyntax)
                return MaybeCloneStruct(_thisSlot);

            if (!(expression is InvocationExpressionSyntax) &&
                !(expression is ObjectCreationExpressionSyntax) &&
                !(expression is ImplicitObjectCreationExpressionSyntax) &&
                !(expression is AssignmentExpressionSyntax) &&
                !(expression is ConditionalExpressionSyntax))
            {
                Optional<object> constantValue = GetConstantValue(expression);
                if (constantValue.HasValue)
                {
                    ITypeSymbol constantType = GetType(expression);
                    return EmitConstant(null, constantValue.Value, constantType);
                }
            }

            switch (expression)
            {
                case IdentifierNameSyntax identifier:
                    return EmitIdentifier(identifier);

                case MemberAccessExpressionSyntax memberAccess:
                    return EmitMemberAccess(memberAccess);

                case InvocationExpressionSyntax invocation:
                    return EmitInvocation(invocation);

                case BinaryExpressionSyntax binary:
                    return EmitBinary(binary);

                case PrefixUnaryExpressionSyntax prefix:
                    return EmitUnary(prefix, prefix.Operand, true);

                case PostfixUnaryExpressionSyntax postfix:
                    return EmitUnary(postfix, postfix.Operand, false);

                case AssignmentExpressionSyntax assignment:
                    return EmitAssignment(assignment);

                case ObjectCreationExpressionSyntax objectCreation:
                    return EmitObjectCreation(objectCreation, objectCreation.ArgumentList, objectCreation.Initializer);
                case ImplicitObjectCreationExpressionSyntax implicitCreation:
                    return EmitObjectCreation(implicitCreation, implicitCreation.ArgumentList, implicitCreation.Initializer);

                case ArrayCreationExpressionSyntax arrayCreation:
                    return EmitArrayCreation(arrayCreation.Initializer, arrayCreation);

                case ImplicitArrayCreationExpressionSyntax implicitArray:
                    return EmitArrayCreation(implicitArray.Initializer, implicitArray);

                case ElementAccessExpressionSyntax elementAccess:
                    return EmitLValue(elementAccess)?.Get(true);

                case CastExpressionSyntax cast:
                {
                    Slot value = EmitExpression(cast.Expression);
                    ITypeSymbol targetType = GetType(cast);
                    return ConvertTo(value, targetType);
                }

                case ConditionalExpressionSyntax conditional:
                    return EmitConditional(conditional);

                case InterpolatedStringExpressionSyntax interpolated:
                    return EmitInterpolatedString(interpolated);

                case TypeOfExpressionSyntax typeOf:
                    return EmitTypeOf(typeOf);

                case IsPatternExpressionSyntax isPattern:
                    return EmitIsPattern(isPattern);

                case ConditionalAccessExpressionSyntax conditionalAccess:
                    return EmitConditionalAccess(conditionalAccess);
            }

            AddError($"Unsupported expression '{expression.Kind()}'.", expression);
            return NewSlot(GetType(expression) ?? _objectType, "bad");
        }

        private Slot EmitIdentifier(IdentifierNameSyntax identifier)
        {
            ISymbol symbol = GetSymbolInfo(identifier).Symbol;

            if (symbol is ILocalSymbol || symbol is IParameterSymbol)
            {
                Slot local = FindLocal(symbol);
                if (local != null)
                    return MaybeCloneStruct(local);

                AddError($"Unable to resolve the local variable '{identifier.Identifier.ValueText}'.", identifier);
                return NewSlot(GetType(identifier) ?? _objectType, "bad");
            }

            if (symbol is IFieldSymbol field)
            {
                // Inside an inlined body 'this' is the other behaviour, so its fields live in its heap.
                if (IsRemoteMemberOf(field))
                    return EmitReadBehaviourField(_remoteReceiver, field, identifier);

                if (_fieldSlots.TryGetValue(field, out Slot fieldSlot))
                    return MaybeCloneStruct(fieldSlot);

                if (TryGetStructThisField(field, out int thisFieldIndex))
                {
                    Slot result = NewSlot(field.Type, "field");
                    PushObjectArrayIndex(_thisSlot, thisFieldIndex, result);
                    return MaybeCloneStruct(result);
                }

                // A bare identifier can only refer to a member of 'this'.
                Slot instance = field.IsStatic ? null : _thisSlot;
                return ReadExternMember(instance, UdonExternSignatures.FieldGet(field), field.Type, identifier);
            }

            if (symbol is IPropertySymbol property)
            {
                if (!property.IsStatic && IsRemoteMemberOf(property))
                    return EmitRemotePropertyRead(property, identifier);

                if (!property.IsStatic && TryGetBehaviourPropertySlot(property, out Slot propertyValueSlot))
                    return MaybeCloneStruct(propertyValueSlot);

                // Implicit 'this' property with an explicit accessor body.
                if (GetBackingField(property) == null && AccessorHasBody(property.GetMethod))
                {
                    Slot accessorInstance = property.IsStatic ? null : _thisSlot;
                    return MaybeCloneStruct(InlineMethod(property.GetMethod, accessorInstance, new List<Slot>(), identifier));
                }

                // Inherited Unity members such as transform, gameObject and enabled.
                Slot instance = property.IsStatic ? null : _thisSlot;
                return ReadExternMember(instance, UdonExternSignatures.PropertyGet(property), property.Type, identifier);
            }

            AddError($"Unsupported identifier '{identifier.Identifier.ValueText}'.", identifier);
            return NewSlot(GetType(identifier) ?? _objectType, "bad");
        }

        private Slot ReadExternMember(Slot instance, string signature, ITypeSymbol type, SyntaxNode node)
        {
            Slot result = NewSlot(type, "memb");
            if (instance != null)
                _asm.Push(instance);

            _asm.Push(result);
            Extern(signature, "member read");
            return result;
        }

        private Slot EmitMemberAccess(MemberAccessExpressionSyntax memberAccess)
        {
            ISymbol symbol = GetSymbolInfo(memberAccess).Symbol;
            ExpressionSyntax receiverSyntax = memberAccess.Expression;
            bool isStatic = symbol is IFieldSymbol f && f.IsStatic || symbol is IPropertySymbol p && p.IsStatic;

            if (symbol == null)
            {
                // A private member of another behaviour has no symbol here, only a candidate; name the
                // real problem instead of reporting an unsupported expression.
                ISymbol candidate = GetSymbolInfo(memberAccess).CandidateSymbols.FirstOrDefault();

                if (candidate is IFieldSymbol candidateField && DerivesFromBetterUdon(candidateField.ContainingType))
                {
                    AddError($"'{candidateField.Name}' is not accessible from another behaviour because it is not public.", memberAccess);
                    return NewSlot(candidateField.Type, "bad");
                }
            }

            if (symbol is IFieldSymbol field)
            {
                // Inside an inlined remote body every member of 'this' belongs to the receiver.
                if (IsRemoteMemberOf(field))
                    return EmitReadBehaviourField(_remoteReceiver, field, memberAccess);

                if (_fieldSlots.TryGetValue(field, out Slot fieldSlot) && IsBehaviourReceiver(receiverSyntax))
                    return MaybeCloneStruct(fieldSlot);

                if (UdonTypeSystem.IsUserStruct(field.ContainingType))
                {
                    Slot container = EmitStructContainer(receiverSyntax, field.ContainingType);
                    StructLayout layout = GetStructLayout(field.ContainingType);
                    int index = layout?.GetIndex(field) ?? -1;

                    if (index < 0)
                    {
                        AddError($"Field '{field.Name}' is not part of the struct layout.", memberAccess);
                        return NewSlot(field.Type, "bad");
                    }

                    Slot result = NewSlot(field.Type, "field");
                    PushObjectArrayIndex(container, index, result);
                    return MaybeCloneStruct(result);
                }

                // Fields of another BetterUdon behaviour live in that behaviour's own heap, so they are
                // read through its public variables instead of an extern member access.
                if (!isStatic && DerivesFromBetterUdon(field.ContainingType))
                    return EmitReadBehaviourField(EmitExpression(receiverSyntax), field, memberAccess);

                Slot instance = isStatic ? null : EmitExpression(receiverSyntax);
                return ReadExternMember(instance, UdonExternSignatures.FieldGet(field), field.Type, memberAccess);
            }

            if (symbol is IPropertySymbol property)
            {
                if (property.IsIndexer)
                {
                    AddError("Indexers are not supported, use an array or a field instead.", memberAccess);
                    return NewSlot(property.Type, "bad");
                }

                // Inside an inlined remote body, 'this.Prop' is the receiver's property.
                if (!property.IsStatic && IsRemoteMemberOf(property))
                    return EmitRemotePropertyRead(property, memberAccess);

                // Auto properties declared on the behaviour itself compile to their backing field.
                if (!property.IsStatic && TryGetBehaviourPropertySlot(property, out Slot propertySlot))
                    return MaybeCloneStruct(propertySlot);

                // A property on another behaviour is read from its heap (auto property) or by inlining
                // its accessor with that behaviour as 'this'.
                if (!property.IsStatic && !IsBehaviourReceiver(receiverSyntax) && DerivesFromBetterUdon(property.ContainingType))
                {
                    Slot receiver = EmitExpression(receiverSyntax);

                    if (GetBackingField(property) != null)
                        return EmitReadBehaviourMember(receiver, property.Name, property.Type, memberAccess);

                    if (AccessorHasBody(property.GetMethod))
                        return MaybeCloneStruct(InlineRemoteMethod(property.GetMethod, receiver, new List<Slot>(), memberAccess));

                    AddError($"'{property.Name}' is a property of another behaviour that has no body to inline.", memberAccess);
                    return NewSlot(property.Type, "bad");
                }

                // List<T>.Count / .Capacity
                if (TryGetListElementType(property.ContainingType, out ITypeSymbol listElementType))
                    return EmitListProperty(memberAccess, property, EmitExpression(receiverSyntax), listElementType);

                // Properties that declare their own accessor body are inlined like methods.
                if (GetBackingField(property) == null && AccessorHasBody(property.GetMethod))
                {
                    Slot accessorInstance = property.IsStatic ? null : EmitExpression(receiverSyntax);
                    return MaybeCloneStruct(InlineMethod(property.GetMethod, accessorInstance, new List<Slot>(), memberAccess));
                }

                if (UdonTypeSystem.IsUserStruct(property.ContainingType))
                {
                    if (!TryGetBackingField(property, out _))
                    {
                        AddError($"Struct property '{property.Name}' must be an auto property to be compiled.", memberAccess);
                        return NewSlot(property.Type, "bad");
                    }

                    IFieldSymbol backing = GetBackingField(property);
                    Slot container = EmitStructContainer(receiverSyntax, property.ContainingType);
                    StructLayout layout = GetStructLayout(property.ContainingType);
                    int index = layout?.GetIndex(backing) ?? -1;

                    Slot result = NewSlot(property.Type, "prop");
                    PushObjectArrayIndex(container, index, result);
                    return MaybeCloneStruct(result);
                }

                if (IsBehaviourReceiver(receiverSyntax) && property.Name == "enabled" && property.IsStatic == false)
                {
                    Slot instanceValue = EmitExpression(receiverSyntax);
                    return ReadExternMember(instanceValue, UdonExternSignatures.PropertyGet(property), property.Type, memberAccess);
                }

                Slot instance = isStatic ? null : EmitExpression(receiverSyntax);
                return ReadExternMember(instance, UdonExternSignatures.PropertyGet(property), property.Type, memberAccess);
            }

            AddError($"Unsupported member access '{memberAccess}'.", memberAccess);
            return NewSlot(GetType(memberAccess) ?? _objectType, "bad");
        }

        private bool IsBehaviourReceiver(ExpressionSyntax expression)
        {
            expression = Unwrap(expression);
            return expression is ThisExpressionSyntax || expression is BaseExpressionSyntax;
        }

        private Slot EmitStructContainer(ExpressionSyntax receiverSyntax, ITypeSymbol structType)
        {
            if (TryEmitLValue(receiverSyntax, out LValue receiver))
                return receiver.Get(false);

            return EmitExpression(receiverSyntax);
        }

        private Slot EmitBinary(BinaryExpressionSyntax binary)
        {
            SyntaxKind kind = binary.Kind();

            if (kind == SyntaxKind.LogicalAndExpression || kind == SyntaxKind.LogicalOrExpression)
                return EmitShortCircuit(binary, kind == SyntaxKind.LogicalAndExpression);

            if (binary.IsKind(SyntaxKind.CoalesceExpression))
                return EmitCoalesce(binary);

            Slot left = EmitExpression(binary.Left);
            Slot right = EmitExpression(binary.Right);

            if (binary.IsKind(SyntaxKind.IsExpression))
            {
                AddError("'is' expressions are not supported by the BetterUdon compiler.", binary);
                return NewSlot(_boolType, "bad");
            }

            IMethodSymbol operatorMethod = GetSymbolInfo(binary).Symbol as IMethodSymbol;
            ITypeSymbol leftType = GetType(binary.Left);

            // Structs without an operator overload compare member-wise, like C# does.
            if (operatorMethod == null &&
                UdonTypeSystem.IsUserStruct(leftType) &&
                (kind == SyntaxKind.EqualsExpression || kind == SyntaxKind.NotEqualsExpression))
            {
                return EmitStructEquality(left, right, leftType, kind == SyntaxKind.NotEqualsExpression, binary);
            }

            if (operatorMethod == null)
            {
                AddError($"Unable to resolve the operator '{binary.OperatorToken.ValueText}'.", binary);
                return NewSlot(GetType(binary) ?? _objectType, "bad");
            }

            if (UdonTypeSystem.IsUserStruct(leftType) && IsUserDefinedMethod(operatorMethod))
                return InlineMethod(operatorMethod, MaybeCloneStruct(left), new List<Slot> { right }, binary);

            string signature = UdonExternSignatures.BuiltinOperator(
                operatorMethod.ContainingType,
                operatorMethod.Name,
                operatorMethod.Parameters.Length > 0 ? operatorMethod.Parameters[0].Type : leftType,
                operatorMethod.ReturnType,
                false);

            Slot result = NewSlot(operatorMethod.ReturnType, "binop");

            // Operands must be converted to the operator's parameter types (e.g. int + float).
            ITypeSymbol leftParameterType = operatorMethod.Parameters.Length > 0 ? operatorMethod.Parameters[0].Type : leftType;
            ITypeSymbol rightParameterType = operatorMethod.Parameters.Length > 1 ? operatorMethod.Parameters[1].Type : leftParameterType;

            _asm.Push(ConvertTo(left, leftParameterType));
            _asm.Push(ConvertTo(right, rightParameterType));
            _asm.Push(result);
            Extern(signature, $"operator '{binary.OperatorToken.ValueText}'");
            return result;
        }

        private Slot EmitStructEquality(Slot left, Slot right, ITypeSymbol structType, bool negate, SyntaxNode node)
        {
            Slot result = NewSlot(_boolType, "structEq");
            Copy(ConstantSlot(_boolType, !negate), result);

            StructLayout layout = GetStructLayout(structType);

            if (layout == null || layout.Fields.Count == 0)
                return result;

            string differentLabel = NewLabel("seDiff");
            string endLabel = NewLabel("seEnd");

            foreach (IFieldSymbol field in layout.Fields)
            {
                int index = layout.GetIndex(field);

                Slot leftField = NewSlot(field.Type);
                PushObjectArrayIndex(left, index, leftField);

                Slot rightField = NewSlot(field.Type);
                PushObjectArrayIndex(right, index, rightField);

                Slot equal = UdonTypeSystem.IsUserStruct(field.Type)
                    ? EmitStructEquality(leftField, rightField, field.Type, false, node)
                    : EmitBinaryOperator(leftField, rightField, field.Type, "op_Equality", _boolType, node);

                _asm.JumpIfFalse(equal, differentLabel);
            }

            _asm.Jump(endLabel);

            _asm.MarkLabel(differentLabel);
            Copy(ConstantSlot(_boolType, negate), result);

            _asm.MarkLabel(endLabel);
            return result;
        }

        private Slot EmitBinaryOperator(Slot left, Slot right, ITypeSymbol operandType, string roslynOperator, ITypeSymbol resultType, SyntaxNode node)
        {
            string signature = UdonExternSignatures.BuiltinOperator(operandType, roslynOperator, operandType, resultType, false);

            Slot result = NewSlot(resultType, "op");
            _asm.Push(ConvertTo(left, operandType));
            _asm.Push(ConvertTo(right, operandType));
            _asm.Push(result);
            Extern(signature, $"operator '{roslynOperator}'");
            return result;
        }

        private Slot EmitShortCircuit(BinaryExpressionSyntax binary, bool isAnd)
        {
            Slot result = NewSlot(_boolType, "logic");
            string endLabel = NewLabel("scEnd");

            Slot left = EmitCondition(binary.Left);
            _asm.Copy(left, result);

            // && : if !left jump end;  || : if left jump end
            if (isAnd)
            {
                _asm.JumpIfFalse(result, endLabel);
                Slot right = EmitCondition(binary.Right);
                _asm.Copy(right, result);
            }
            else
            {
                _asm.Push(result);
                _asm.Jump(endLabel);
                Slot right = EmitCondition(binary.Right);
                _asm.Copy(right, result);
            }

            _asm.MarkLabel(endLabel);
            return result;
        }

        private Slot EmitCoalesce(BinaryExpressionSyntax binary)
        {
            Slot result = NewSlot(GetType(binary) ?? _objectType, "coal");
            string endLabel = NewLabel("coalEnd");
            string rightLabel = NewLabel("coalRight");

            Slot left = EmitExpression(binary.Left);
            Slot isNull = EmitBinaryOperator(left, ConstantSlot(_objectType, null), _objectType, "op_Equality", _boolType, binary);
            _asm.JumpIfFalse(isNull, rightLabel);

            Slot right = EmitExpression(binary.Right);
            Copy(ConvertTo(right, result.Type), result);
            _asm.Jump(endLabel);

            _asm.MarkLabel(rightLabel);
            Copy(ConvertTo(left, result.Type), result);
            _asm.MarkLabel(endLabel);

            return result;
        }

        private Slot EmitUnary(ExpressionSyntax node, ExpressionSyntax operandSyntax, bool isPrefix)
        {
            SyntaxKind kind = node.Kind();

            if (kind == SyntaxKind.UnaryPlusExpression)
                return EmitExpression(operandSyntax);

            if (kind == SyntaxKind.PreIncrementExpression || kind == SyntaxKind.PreDecrementExpression ||
                kind == SyntaxKind.PostIncrementExpression || kind == SyntaxKind.PostDecrementExpression)
            {
                return EmitIncrement(node, operandSyntax, kind, isPrefix);
            }

            Slot operand = EmitExpression(operandSyntax);
            IMethodSymbol operatorMethod = GetSymbolInfo(node).Symbol as IMethodSymbol;

            if (operatorMethod == null)
            {
                AddError($"Unable to resolve unary operator '{node}'.", node);
                return NewSlot(GetType(node) ?? _objectType, "bad");
            }

            ITypeSymbol operandType = GetType(operandSyntax) ?? operatorMethod.Parameters[0].Type;

            if (UdonTypeSystem.IsUserStruct(operandType) && IsUserDefinedMethod(operatorMethod))
                return InlineMethod(operatorMethod, MaybeCloneStruct(operand), new List<Slot>(), node);

            string signature = UdonExternSignatures.BuiltinOperator(
                operatorMethod.ContainingType, operatorMethod.Name, operandType, operatorMethod.ReturnType, true);

            Slot result = NewSlot(operatorMethod.ReturnType, "unop");
            _asm.Push(ConvertTo(operand, operandType));
            _asm.Push(result);
            Extern(signature, "unary operator");
            return result;
        }

        private Slot EmitIncrement(ExpressionSyntax node, ExpressionSyntax operandSyntax, SyntaxKind kind, bool isPrefix)
        {
            LValue target = EmitLValue(operandSyntax);
            if (target == null)
            {
                AddError("Increment target is not assignable.", node);
                return NewSlot(_intType, "bad");
            }

            Slot oldValue = target.Get(false);
            ITypeSymbol type = target.Type;
            Slot one = OneFor(type);
            string operatorName = kind == SyntaxKind.PreIncrementExpression || kind == SyntaxKind.PostIncrementExpression
                ? "op_Addition"
                : "op_Subtraction";

            Slot newValue = EmitBinaryOperator(oldValue, one, type, operatorName, type, node);

            string joinLabel = NewLabel("incJoin");
            Slot result = NewSlot(type, "inc");

            if (isPrefix)
            {
                target.Set(newValue);
                Copy(newValue, result);
            }
            else
            {
                Copy(oldValue, result);
                Slot oldCopy = NewSlot(type, "incOld");
                Copy(oldValue, oldCopy);
                target.Set(newValue);
                Copy(oldCopy, result);
            }

            _asm.MarkLabel(joinLabel);
            return result;
        }

        private Slot OneFor(ITypeSymbol type)
        {
            switch (type.SpecialType)
            {
                case SpecialType.System_Single: return ConstantSlot(type, 1f);
                case SpecialType.System_Double: return ConstantSlot(type, 1d);
                case SpecialType.System_Int64: return ConstantSlot(type, 1L);
                case SpecialType.System_UInt64: return ConstantSlot(type, 1UL);
                default: return ConstantSlot(type, 1);
            }
        }

        private Slot EmitAssignment(AssignmentExpressionSyntax assignment)
        {
            LValue target = EmitLValue(assignment.Left);

            if (target == null)
            {
                AddError($"'{assignment.Left}' is not assignable.", assignment);
                return NewSlot(GetType(assignment) ?? _objectType, "bad");
            }

            if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
            {
                Slot value = EmitExpression(assignment.Right);
                target.Set(value);
                return target.Get(true);
            }

            if (assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression))
            {
                // a ??= b  ->  if (a == null) a = b
                Slot currentValue = target.Get(false);
                string endLabel = NewLabel("coalAssignEnd");

                Slot isNull = EmitBinaryOperator(currentValue, ConstantSlot(_objectType, null), _objectType, "op_Equality", _boolType, assignment);
                _asm.JumpIfFalse(isNull, endLabel);

                Slot assigned = EmitExpression(assignment.Right);
                target.Set(assigned);

                _asm.MarkLabel(endLabel);
                return target.Get(true);
            }

            Slot current = target.Get(false);
            Slot rightValue = EmitExpression(assignment.Right);

            IMethodSymbol operatorMethod = GetSymbolInfo(assignment).Symbol as IMethodSymbol;

            // a += b is a = a + b, and compound assignment on arrays works on the element in place.
            Slot newValue;
            if (operatorMethod != null && UdonTypeSystem.IsUserStruct(operatorMethod.ContainingType))
            {
                // User defined operator; find the matching binary operator name.
                string opName = GetCompoundOperatorName(assignment.Kind());
                IMethodSymbol binaryOperator = FindUserOperator(operatorMethod.ContainingType, opName, target.Type, rightValue.Type);
                if (binaryOperator == null)
                {
                    AddError($"Unable to resolve the operator for '{assignment.OperatorToken.ValueText}'.", assignment);
                    return target.Get(true);
                }

                newValue = InlineMethod(binaryOperator, MaybeCloneStruct(current), new List<Slot> { rightValue }, assignment);
            }
            else
            {
                string opName = GetCompoundOperatorName(assignment.Kind());
                if (opName == null)
                {
                    AddError($"Unsupported compound assignment '{assignment.OperatorToken.ValueText}'.", assignment);
                    return target.Get(true);
                }

                newValue = EmitBinaryOperator(current, rightValue, target.Type, opName, target.Type, assignment);
            }

            target.Set(newValue);
            return target.Get(true);
        }

        private static string GetCompoundOperatorName(SyntaxKind kind)
        {
            switch (kind)
            {
                case SyntaxKind.AddAssignmentExpression: return "op_Addition";
                case SyntaxKind.SubtractAssignmentExpression: return "op_Subtraction";
                case SyntaxKind.MultiplyAssignmentExpression: return "op_Multiply";
                case SyntaxKind.DivideAssignmentExpression: return "op_Division";
                case SyntaxKind.ModuloAssignmentExpression: return "op_Modulus";
                case SyntaxKind.AndAssignmentExpression: return "op_BitwiseAnd";
                case SyntaxKind.OrAssignmentExpression: return "op_BitwiseOr";
                case SyntaxKind.ExclusiveOrAssignmentExpression: return "op_ExclusiveOr";
                case SyntaxKind.LeftShiftAssignmentExpression: return "op_LeftShift";
                case SyntaxKind.RightShiftAssignmentExpression: return "op_RightShift";
                default: return null;
            }
        }

        private IMethodSymbol FindUserOperator(ITypeSymbol type, string name, ITypeSymbol leftType, ITypeSymbol rightType)
        {
            if (!(type is INamedTypeSymbol named))
                return null;

            return named.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m =>
                m.Name == name && m.IsStatic && m.Parameters.Length == 2);
        }

        private Slot EmitConditional(ConditionalExpressionSyntax conditional)
        {
            ITypeSymbol resultType = GetType(conditional);
            Slot result = NewSlot(resultType, "cond");
            string elseLabel = NewLabel("condElse");
            string endLabel = NewLabel("condEnd");

            Slot condition = EmitCondition(conditional.Condition);
            _asm.JumpIfFalse(condition, elseLabel);

            Slot whenTrue = EmitExpression(conditional.WhenTrue);
            Copy(ConvertTo(whenTrue, resultType), result);
            _asm.Jump(endLabel);

            _asm.MarkLabel(elseLabel);
            Slot whenFalse = EmitExpression(conditional.WhenFalse);
            Copy(ConvertTo(whenFalse, resultType), result);

            _asm.MarkLabel(endLabel);
            return result;
        }

        /// <summary>
        /// <c>a?.b</c> and <c>a?.M()</c>. The member is only evaluated when the receiver is not null,
        /// and the result stays at its default otherwise.
        /// </summary>
        private Slot EmitConditionalAccess(ConditionalAccessExpressionSyntax conditionalAccess)
        {
            ITypeSymbol resultType = GetType(conditionalAccess);
            bool isVoid = resultType == null || resultType.SpecialType == SpecialType.System_Void;

            // 'a?.VoidMethod()' does not produce a value.
            Slot result = isVoid ? null : NewSlot(resultType, "condAccess");
            string endLabel = NewLabel("caEnd");

            Slot receiver = EmitExpression(conditionalAccess.Expression);
            Slot notNull = EmitBinaryOperator(receiver, ConstantSlot(_objectType, null), _objectType, "op_Inequality", _boolType, conditionalAccess);
            _asm.JumpIfFalse(notNull, endLabel);

            Slot value = EmitConditionalAccessValue(conditionalAccess.WhenNotNull, receiver, conditionalAccess);

            if (result != null && value != null)
                Copy(ConvertTo(value, resultType), result);

            _asm.MarkLabel(endLabel);
            return result;
        }

        private Slot EmitConditionalAccessValue(ExpressionSyntax whenNotNull, Slot receiver, ConditionalAccessExpressionSyntax conditionalAccess)
        {
            if (whenNotNull is MemberBindingExpressionSyntax memberBinding)
            {
                ISymbol symbol = GetSymbolInfo(memberBinding).Symbol;

                if (symbol is IPropertySymbol property)
                {
                    if (!property.IsStatic && TryGetBehaviourPropertySlot(property, out Slot propertySlot))
                        return MaybeCloneStruct(propertySlot);

                    return ReadExternMember(receiver, UdonExternSignatures.PropertyGet(property), property.Type, conditionalAccess);
                }

                if (symbol is IFieldSymbol field)
                {
                    if (_fieldSlots.TryGetValue(field, out Slot fieldSlot))
                        return MaybeCloneStruct(fieldSlot);

                    return ReadExternMember(receiver, UdonExternSignatures.FieldGet(field), field.Type, conditionalAccess);
                }

                AddError($"Unable to resolve '{memberBinding}' after '?.'.", conditionalAccess);
                return NewSlot(_objectType, "bad");
            }

            if (whenNotNull is InvocationExpressionSyntax invocation)
            {
                try
                {
                    _forcedReceiver = receiver;
                    _hasForcedReceiver = true;
                    return EmitInvocation(invocation);
                }
                finally
                {
                    _forcedReceiver = null;
                    _hasForcedReceiver = false;
                }
            }

            if (whenNotNull is ElementBindingExpressionSyntax)
            {
                AddError("'?[...]' is not supported by the BetterUdon compiler, use an explicit null check.", conditionalAccess);
                return NewSlot(_objectType, "bad");
            }

            AddError($"Unsupported conditional access '{whenNotNull.Kind()}'.", conditionalAccess);
            return NewSlot(_objectType, "bad");
        }

        private ITypeSymbol _systemTypeSymbolCache;

        /// <summary>
        /// <c>System.Type</c> is declared in several referenced assemblies (mscorlib, netstandard, the
        /// facade assemblies), so <c>GetTypeByMetadataName</c> can return null when it is ambiguous.
        /// Falling back to a direct lookup keeps <c>typeof</c> constants typed as <c>SystemType</c>.
        /// </summary>
        private ITypeSymbol _systemTypeSymbol
        {
            get
            {
                if (_systemTypeSymbolCache != null)
                    return _systemTypeSymbolCache;

                ITypeSymbol resolved = _model.Compilation.GetTypeByMetadataName("System.Type");

                // An ambiguous lookup yields an error type rather than null.
                if (resolved == null || resolved.TypeKind == TypeKind.Error)
                {
                    resolved = null;

                    foreach (MetadataReference reference in _model.Compilation.References)
                    {
                        if (!(_model.Compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly))
                            continue;

                        ITypeSymbol candidate = assembly.GetTypeByMetadataName("System.Type");

                        if (candidate != null && candidate.TypeKind != TypeKind.Error)
                        {
                            resolved = candidate;
                            break;
                        }
                    }
                }

                _systemTypeSymbolCache = resolved ?? _objectType;
                return _systemTypeSymbolCache;
            }
        }

        /// <summary>
        /// <c>typeof(T)</c> becomes a heap constant holding the CLR <see cref="Type"/>, which is what
        /// Udon's <c>SystemType</c> heap values are.
        /// </summary>
        private Slot EmitTypeOf(TypeOfExpressionSyntax typeOf)
        {
            ITypeSymbol type = GetTypeInfo(typeOf.Type).Type ?? GetSymbolInfo(typeOf.Type).Symbol as ITypeSymbol;

            if (type == null)
            {
                AddError("Unable to resolve the type inside typeof().", typeOf);
                return NewSlot(_objectType, "bad");
            }

            Type systemType = UdonTypeSystem.ToSystemType(type);

            if (systemType == null)
            {
                AddError($"typeof({type}) cannot be resolved to a runtime type.", typeOf);
                return NewSlot(_objectType, "bad");
            }

            return ConstantSlot(_systemTypeSymbol, systemType, "SystemType");
        }

        /// <summary>
        /// Supports <c>x is null</c> and <c>x is not null</c>. Type patterns need runtime type
        /// information that Udon does not expose.
        /// </summary>
        private Slot EmitIsPattern(IsPatternExpressionSyntax isPattern)
        {
            bool negate = false;
            PatternSyntax pattern = isPattern.Pattern;

            if (pattern is UnaryPatternSyntax unaryPattern && unaryPattern.IsKind(SyntaxKind.NotPattern))
            {
                negate = true;
                pattern = unaryPattern.Pattern;
            }

            if (pattern is ConstantPatternSyntax constantPattern && IsNullExpression(constantPattern.Expression))
            {
                Slot value = EmitExpression(isPattern.Expression);
                Slot equalsNull = EmitBinaryOperator(value, ConstantSlot(_objectType, null), _objectType, "op_Equality", _boolType, isPattern);

                if (!negate)
                    return equalsNull;

                string signature = UdonExternSignatures.BuiltinOperator(_boolType, "op_LogicalNot", _boolType, _boolType, true);
                Slot result = NewSlot(_boolType, "not");
                _asm.Push(equalsNull);
                _asm.Push(result);
                Extern(signature, "logical not");
                return result;
            }

            AddError("Only 'is null' and 'is not null' patterns are supported by the BetterUdon compiler.", isPattern);
            return NewSlot(_boolType, "bad");
        }

        private bool IsNullExpression(ExpressionSyntax expression)
        {
            if (expression == null)
                return false;

            if (expression.IsKind(SyntaxKind.NullLiteralExpression))
                return true;

            Optional<object> constant = GetConstantValue(expression);
            return constant.HasValue && constant.Value == null;
        }

        private Slot EmitInterpolatedString(InterpolatedStringExpressionSyntax interpolated)
        {
            Slot result = null;

            foreach (InterpolatedStringContentSyntax content in interpolated.Contents)
            {
                Slot part;

                if (content is InterpolatedStringTextSyntax text)
                {
                    part = ConstantSlot(_stringType, text.TextToken.ValueText);
                }
                else if (content is InterpolationSyntax interpolation)
                {
                    Slot value = EmitExpression(interpolation.Expression);
                    ITypeSymbol valueType = GetType(interpolation.Expression);
                    part = ConvertTo(value, _stringType);
                }
                else
                {
                    continue;
                }

                if (result == null)
                {
                    result = NewSlot(_stringType, "interp");
                    Copy(part, result);
                }
                else
                {
                    Slot concatenated = EmitBinaryOperator(result, part, _stringType, "op_Addition", _stringType, interpolated);
                    Copy(concatenated, result);
                }
            }

            return result ?? ConstantSlot(_stringType, string.Empty);
        }

        #endregion

        #region LValues

        private LValue EmitLValue(ExpressionSyntax expression)
        {
            if (TryEmitLValue(expression, out LValue lvalue))
                return lvalue;

            return null;
        }

        private bool TryEmitLValue(ExpressionSyntax expression, out LValue lvalue)
        {
            lvalue = null;
            expression = Unwrap(expression);

            switch (expression)
            {
                case ThisExpressionSyntax _:
                case BaseExpressionSyntax _:
                    lvalue = new SlotLValue(this, _thisSlot);
                    return true;

                case IdentifierNameSyntax identifier:
                {
                    ISymbol symbol = GetSymbolInfo(identifier).Symbol;

                    if (symbol is ILocalSymbol || symbol is IParameterSymbol)
                    {
                        Slot local = FindLocal(symbol);
                        if (local == null)
                            return false;

                        lvalue = new SlotLValue(this, local);
                        return true;
                    }

                    if (symbol is IFieldSymbol remoteField && IsRemoteMemberOf(remoteField))
                    {
                        IFieldSymbol captured = remoteField;
                        lvalue = new CallbackLValue(captured.Type,
                            clone => EmitReadBehaviourField(_remoteReceiver, captured, identifier, clone),
                            value => EmitWriteBehaviourField(_remoteReceiver, captured, value, identifier));
                        return true;
                    }

                    if (symbol is IPropertySymbol remoteProperty && !remoteProperty.IsStatic && IsRemoteMemberOf(remoteProperty))
                    {
                        lvalue = CreateRemotePropertyLValue(remoteProperty, identifier);
                        return true;
                    }

                    if (symbol is IFieldSymbol field && _fieldSlots.TryGetValue(field, out Slot fieldSlot))
                    {
                        lvalue = new SlotLValue(this, fieldSlot);
                        return true;
                    }

                    if (symbol is IFieldSymbol structField && TryGetStructThisField(structField, out int structFieldIndex))
                    {
                        lvalue = new StructFieldLValue(this, _thisSlot, structFieldIndex, structField.Type);
                        return true;
                    }

                    if (symbol is IPropertySymbol propertySymbol && !propertySymbol.IsStatic &&
                        TryGetBehaviourPropertySlot(propertySymbol, out Slot behaviourPropertySlot))
                    {
                        lvalue = new SlotLValue(this, behaviourPropertySlot);
                        return true;
                    }

                    if (symbol is IPropertySymbol accessorProperty && GetBackingField(accessorProperty) == null &&
                        (AccessorHasBody(accessorProperty.GetMethod) || AccessorHasBody(accessorProperty.SetMethod)))
                    {
                        Slot accessorReceiver = accessorProperty.IsStatic ? null : _thisSlot;
                        IPropertySymbol capturedProperty = accessorProperty;

                        lvalue = new CallbackLValue(accessorProperty.Type,
                            clone =>
                            {
                                if (!AccessorHasBody(capturedProperty.GetMethod))
                                {
                                    AddError($"'{capturedProperty.Name}' has no getter.", identifier);
                                    return NewSlot(capturedProperty.Type, "bad");
                                }

                                Slot value = InlineMethod(capturedProperty.GetMethod, accessorReceiver, new List<Slot>(), identifier);
                                return clone ? MaybeCloneStruct(value) : value;
                            },
                            newValue =>
                            {
                                if (!AccessorHasBody(capturedProperty.SetMethod))
                                {
                                    AddError($"'{capturedProperty.Name}' has no setter.", identifier);
                                    return;
                                }

                                InlineMethod(capturedProperty.SetMethod, accessorReceiver, new List<Slot> { newValue }, identifier);
                            });

                        return true;
                    }

                    // Inherited Unity members such as enabled can be assigned through this.
                    if (symbol is IPropertySymbol externProperty && externProperty.SetMethod != null)
                    {
                        Slot instance = externProperty.IsStatic ? null : _thisSlot;
                        lvalue = new ExternMemberLValue(this, instance,
                            UdonExternSignatures.PropertyGet(externProperty), UdonExternSignatures.PropertySet(externProperty), externProperty.Type);
                        return true;
                    }

                    if (symbol is IFieldSymbol externField && !externField.IsStatic)
                    {
                        lvalue = new ExternMemberLValue(this, _thisSlot,
                            UdonExternSignatures.FieldGet(externField), UdonExternSignatures.FieldSet(externField), externField.Type);
                        return true;
                    }

                    return false;
                }

                case ElementAccessExpressionSyntax elementAccess:
                {
                    ITypeSymbol containerType = GetType(elementAccess.Expression);

                    if (TryGetListElementType(containerType, out ITypeSymbol listElementType))
                    {
                        Slot listSlot = EmitExpression(elementAccess.Expression);
                        Slot indexSlot = ConvertTo(EmitExpression(elementAccess.ArgumentList.Arguments[0].Expression), _intType);

                        lvalue = new CallbackLValue(listElementType,
                            clone => ListElementGet(listSlot, listElementType, indexSlot),
                            value => ListElementSet(listSlot, listElementType, indexSlot, value));
                        return true;
                    }

                    if (!(containerType is IArrayTypeSymbol arrayElementType))
                        return false;

                    Slot array = EmitExpression(elementAccess.Expression);
                    Slot index = ConvertTo(EmitExpression(elementAccess.ArgumentList.Arguments[0].Expression), _intType);

                    lvalue = new ArrayElementLValue(this, array, index, containerType, arrayElementType.ElementType);
                    return true;
                }

                case MemberAccessExpressionSyntax memberAccess:
                {
                    ISymbol symbol = GetSymbolInfo(memberAccess).Symbol;

                    if (symbol is IFieldSymbol field)
                    {
                        if (!field.IsStatic && IsRemoteMemberOf(field))
                        {
                            IFieldSymbol captured = field;
                            lvalue = new CallbackLValue(captured.Type,
                                clone => EmitReadBehaviourField(_remoteReceiver, captured, memberAccess, clone),
                                value => EmitWriteBehaviourField(_remoteReceiver, captured, value, memberAccess));
                            return true;
                        }

                        // Only this behaviour's own instance maps to a heap slot; another instance of
                        // the same type has its own heap.
                        if (_fieldSlots.TryGetValue(field, out Slot fieldSlot) && IsBehaviourReceiver(memberAccess.Expression))
                        {
                            lvalue = new SlotLValue(this, fieldSlot);
                            return true;
                        }

                        if (UdonTypeSystem.IsUserStruct(field.ContainingType))
                        {
                            StructLayout layout = GetStructLayout(field.ContainingType);
                            int index = layout?.GetIndex(field) ?? -1;
                            if (index < 0)
                                return false;

                            Slot container = EmitStructContainer(memberAccess.Expression, field.ContainingType);
                            lvalue = new StructFieldLValue(this, container, index, field.Type);
                            return true;
                        }

                        bool isStatic = field.IsStatic;

                        if (!isStatic && DerivesFromBetterUdon(field.ContainingType))
                        {
                            Slot receiver = EmitExpression(memberAccess.Expression);

                            lvalue = new CallbackLValue(field.Type,
                                clone => EmitReadBehaviourField(receiver, field, memberAccess, clone),
                                value => EmitWriteBehaviourField(receiver, field, value, memberAccess));
                            return true;
                        }

                        Slot instance = isStatic ? null : EmitExpression(memberAccess.Expression);
                        lvalue = new ExternMemberLValue(this, instance,
                            UdonExternSignatures.FieldGet(field), UdonExternSignatures.FieldSet(field), field.Type);
                        return true;
                    }

                    if (symbol is IPropertySymbol property)
                    {
                        if (!property.IsStatic && IsRemoteMemberOf(property))
                        {
                            lvalue = CreateRemotePropertyLValue(property, memberAccess);
                            return true;
                        }

                        if (!property.IsStatic && IsBehaviourReceiver(memberAccess.Expression) &&
                            TryGetBehaviourPropertySlot(property, out Slot propertySlot))
                        {
                            lvalue = new SlotLValue(this, propertySlot);
                            return true;
                        }

                        if (!property.IsStatic && !IsBehaviourReceiver(memberAccess.Expression) &&
                            DerivesFromBetterUdon(property.ContainingType))
                        {
                            Slot receiver = EmitExpression(memberAccess.Expression);

                            if (GetBackingField(property) != null)
                            {
                                lvalue = new CallbackLValue(property.Type,
                                    clone => EmitReadBehaviourMember(receiver, property.Name, property.Type, memberAccess),
                                    value => EmitWriteBehaviourMember(receiver, property.Name, ConvertTo(value, property.Type)));
                                return true;
                            }

                            if (AccessorHasBody(property.SetMethod))
                            {
                                lvalue = new CallbackLValue(property.Type,
                                    clone => MaybeCloneStruct(InlineRemoteMethod(property.GetMethod, receiver, new List<Slot>(), memberAccess)),
                                    value => InlineRemoteMethod(property.SetMethod, receiver, new List<Slot> { value }, memberAccess));
                                return true;
                            }

                            AddError($"'{property.Name}' is a property of another behaviour that has no body to inline.", memberAccess);
                            return false;
                        }

                        // Properties with their own accessor bodies are inlined on get and set.
                        if (GetBackingField(property) == null &&
                            (AccessorHasBody(property.GetMethod) || AccessorHasBody(property.SetMethod)))
                        {
                            Slot accessorReceiver = property.IsStatic
                                ? null
                                : EmitStructContainer(memberAccess.Expression, property.ContainingType);

                            IPropertySymbol capturedProperty = property;
                            MemberAccessExpressionSyntax capturedSyntax = memberAccess;

                            lvalue = new CallbackLValue(property.Type,
                                clone =>
                                {
                                    if (!AccessorHasBody(capturedProperty.GetMethod))
                                    {
                                        AddError($"'{capturedProperty.Name}' has no getter.", capturedSyntax);
                                        return NewSlot(capturedProperty.Type, "bad");
                                    }

                                    Slot value = InlineMethod(capturedProperty.GetMethod, accessorReceiver, new List<Slot>(), capturedSyntax);
                                    return clone ? MaybeCloneStruct(value) : value;
                                },
                                newValue =>
                                {
                                    if (!AccessorHasBody(capturedProperty.SetMethod))
                                    {
                                        AddError($"'{capturedProperty.Name}' has no setter.", capturedSyntax);
                                        return;
                                    }

                                    InlineMethod(capturedProperty.SetMethod, accessorReceiver, new List<Slot> { newValue }, capturedSyntax);
                                });

                            return true;
                        }

                        if (UdonTypeSystem.IsUserStruct(property.ContainingType))
                        {
                            IFieldSymbol backing = GetBackingField(property);
                            if (backing == null)
                                return false;

                            StructLayout layout = GetStructLayout(property.ContainingType);
                            int index = layout?.GetIndex(backing) ?? -1;
                            if (index < 0)
                                return false;

                            Slot container = EmitStructContainer(memberAccess.Expression, property.ContainingType);
                            lvalue = new StructFieldLValue(this, container, index, property.Type);
                            return true;
                        }

                        if (property.SetMethod == null)
                            return false;

                        bool isStatic = property.IsStatic;
                        Slot instance = isStatic ? null : EmitExpression(memberAccess.Expression);
                        lvalue = new ExternMemberLValue(this, instance,
                            UdonExternSignatures.PropertyGet(property), UdonExternSignatures.PropertySet(property), property.Type);
                        return true;
                    }

                    return false;
                }
            }

            return false;
        }

        private bool TryGetStructThisField(IFieldSymbol field, out int index)
        {
            index = -1;

            if (!_thisIsStruct || _thisSlot?.Type == null)
                return false;

            if (!SymbolEqualityComparer.Default.Equals(field.ContainingType, _thisSlot.Type))
                return false;

            StructLayout layout = GetStructLayout(_thisSlot.Type);
            if (layout == null)
                return false;

            index = layout.GetIndex(field);
            return index >= 0;
        }

        private bool TryGetBackingField(IPropertySymbol property, out IFieldSymbol field)
        {
            field = GetBackingField(property);
            return field != null;
        }

        /// <summary>
        /// True when a property accessor declares its own body, i.e. it is not an auto property.
        /// </summary>
        private static bool AccessorHasBody(IMethodSymbol accessor)
        {
            if (accessor == null || accessor.IsImplicitlyDeclared)
                return false;

            foreach (SyntaxReference reference in accessor.DeclaringSyntaxReferences)
            {
                SyntaxNode node = reference.GetSyntax();

                if (node is AccessorDeclarationSyntax declaration &&
                    (declaration.Body != null || declaration.ExpressionBody != null))
                {
                    return true;
                }

                // Expression bodied properties (public int X => ...) declare their accessor on the
                // property declaration itself.
                if (node is PropertyDeclarationSyntax propertyDeclaration &&
                    propertyDeclaration.ExpressionBody != null)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Finds the heap slot of an auto property declared on the behaviour (or a base behaviour).
        /// </summary>
        private bool TryGetBehaviourPropertySlot(IPropertySymbol property, out Slot slot)
        {
            slot = null;

            IFieldSymbol backing = GetBackingField(property);
            return backing != null && _fieldSlots.TryGetValue(backing, out slot);
        }

        private IFieldSymbol GetBackingField(IPropertySymbol property)
        {
            if (property.ContainingType == null)
                return null;

            foreach (IFieldSymbol field in property.ContainingType.GetMembers().OfType<IFieldSymbol>())
            {
                if (field.AssociatedSymbol != null && SymbolEqualityComparer.Default.Equals(field.AssociatedSymbol, property))
                    return field;
            }

            return null;
        }

        #endregion
    }
}
