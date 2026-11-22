using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BetterUdon.Editor
{
    internal sealed partial class BehaviourCodeGenerator
    {
        #region LValues

        private abstract class LValue
        {
            public abstract ITypeSymbol Type { get; }

            /// <summary>Reads the value. When <paramref name="clone"/> is set user structs are copied.</summary>
            public abstract Slot Get(bool clone);

            public abstract void Set(Slot value);
        }

        private sealed class SlotLValue : LValue
        {
            private readonly BehaviourCodeGenerator _owner;
            private readonly Slot _slot;

            public SlotLValue(BehaviourCodeGenerator owner, Slot slot)
            {
                _owner = owner;
                _slot = slot;
            }

            public override ITypeSymbol Type => _slot.Type;

            public override Slot Get(bool clone) => clone ? _owner.MaybeCloneStruct(_slot) : _slot;

            public override void Set(Slot value)
            {
                Slot converted = _owner.ConvertTo(value, _slot.Type);
                _owner.Copy(converted, _slot);
            }
        }

        private sealed class ArrayElementLValue : LValue
        {
            private readonly BehaviourCodeGenerator _owner;
            private readonly Slot _array;
            private readonly Slot _index;
            private readonly ITypeSymbol _elementType;
            private readonly ITypeSymbol _arrayType;

            public ArrayElementLValue(BehaviourCodeGenerator owner, Slot array, Slot index, ITypeSymbol arrayType, ITypeSymbol elementType)
            {
                _owner = owner;
                _array = array;
                _index = index;
                _arrayType = arrayType;
                _elementType = elementType;
            }

            public override ITypeSymbol Type => _elementType;

            public override Slot Get(bool clone)
            {
                Slot result = _owner.NewSlot(_elementType, "elem");
                _owner._asm.Push(_array);
                _owner._asm.Push(_index);
                _owner._asm.Push(result);
                _owner.Extern(UdonExternSignatures.ArrayGet(_arrayType), "array element read");

                return clone ? _owner.MaybeCloneStruct(result) : result;
            }

            public override void Set(Slot value)
            {
                Slot converted = _owner.ConvertTo(value, _elementType);
                _owner._asm.Push(_array);
                _owner._asm.Push(_index);
                _owner._asm.Push(converted);
                _owner.Extern(UdonExternSignatures.ArraySet(_arrayType), "array element write");
            }
        }

        private sealed class StructFieldLValue : LValue
        {
            private readonly BehaviourCodeGenerator _owner;
            private readonly Slot _container;
            private readonly int _index;
            private readonly ITypeSymbol _fieldType;

            public StructFieldLValue(BehaviourCodeGenerator owner, Slot container, int index, ITypeSymbol fieldType)
            {
                _owner = owner;
                _container = container;
                _index = index;
                _fieldType = fieldType;
            }

            public override ITypeSymbol Type => _fieldType;

            public override Slot Get(bool clone)
            {
                Slot result = _owner.NewSlot(_fieldType, "field");
                _owner.PushObjectArrayIndex(_container, _index, result);
                return clone ? _owner.MaybeCloneStruct(result) : result;
            }

            public override void Set(Slot value)
            {
                Slot converted = _owner.ConvertTo(value, _fieldType);
                Slot indexSlot = _owner.IntConstant(_index);
                _owner._asm.Push(_container);
                _owner._asm.Push(indexSlot);
                _owner._asm.Push(converted);
                _owner.Extern("SystemObjectArray.__Set__SystemInt32_SystemObject__SystemVoid", "struct field write");
            }
        }

        /// <summary>
        /// An lvalue backed by user code, used for properties that declare their own accessor bodies.
        /// </summary>
        private sealed class CallbackLValue : LValue
        {
            private readonly Func<bool, Slot> _get;
            private readonly Action<Slot> _set;
            private readonly ITypeSymbol _type;

            public CallbackLValue(ITypeSymbol type, Func<bool, Slot> get, Action<Slot> set)
            {
                _type = type;
                _get = get;
                _set = set;
            }

            public override ITypeSymbol Type => _type;

            public override Slot Get(bool clone) => _get(clone);

            public override void Set(Slot value) => _set(value);
        }

        private sealed class ExternMemberLValue : LValue
        {
            private readonly BehaviourCodeGenerator _owner;
            private readonly Slot _instance;
            private readonly string _getSignature;
            private readonly string _setSignature;
            private readonly ITypeSymbol _fieldType;

            public ExternMemberLValue(BehaviourCodeGenerator owner, Slot instance, string getSignature, string setSignature, ITypeSymbol fieldType)
            {
                _owner = owner;
                _instance = instance;
                _getSignature = getSignature;
                _setSignature = setSignature;
                _fieldType = fieldType;
            }

            public override ITypeSymbol Type => _fieldType;

            public override Slot Get(bool clone)
            {
                Slot result = _owner.NewSlot(_fieldType, "memb");
                if (_instance != null)
                    _owner._asm.Push(_instance);

                _owner._asm.Push(result);
                _owner.Extern(_getSignature, "member read");
                return result;
            }

            public override void Set(Slot value)
            {
                Slot converted = _owner.ConvertTo(value, _fieldType);

                if (_instance != null)
                    _owner._asm.Push(_instance);

                _owner._asm.Push(converted);
                _owner.Extern(_setSignature, "member write");
            }
        }

        #endregion

        #region Helpers

        private void Copy(Slot source, Slot target)
        {
            _asm.Copy(source, target);
        }

        private void Extern(string signature, string context)
        {
            ValidateExtern(signature, context);
            _asm.Extern(signature);
        }

        private Slot IntConstant(int value)
        {
            Slot constant = ConstantSlot(_intType, value);
            return constant;
        }

        private Slot PushObjectArrayIndex(Slot container, int index, Slot target)
        {
            _asm.Push(container);
            _asm.Push(IntConstant(index));
            _asm.Push(target);
            Extern("SystemObjectArray.__Get__SystemInt32__SystemObject", "struct field read");
            return target;
        }

        private Slots.Scope PushScope()
        {
            Dictionary<ISymbol, Slot> scope = new Dictionary<ISymbol, Slot>(SymbolEqualityComparer.Default);
            _scopes.Add(scope);
            return new Slots.Scope(this, scope);
        }

        internal void PopScope()
        {
            _scopes.RemoveAt(_scopes.Count - 1);
        }

        private Slot DeclareLocal(ISymbol symbol, ITypeSymbol type, string hint = null)
        {
            Slot slot = NewSlot(type, hint ?? symbol.Name);
            _scopes[_scopes.Count - 1][symbol] = slot;
            return slot;
        }

        private Slot FindLocal(ISymbol symbol)
        {
            foreach (Dictionary<ISymbol, Slot> captured in _capturedSlotMaps)
            {
                if (captured.TryGetValue(symbol, out Slot capturedSlot))
                    return capturedSlot;
            }

            for (int i = _scopes.Count - 1; i >= 0; i--)
            {
                if (_scopes[i].TryGetValue(symbol, out Slot slot))
                    return slot;
            }

            return null;
        }

        private ITypeSymbol GetType(ExpressionSyntax expression)
        {
            if (expression == null)
                return null;

            TypeInfo info = GetTypeInfo(expression);
            return Substitute(info.Type ?? info.ConvertedType);
        }

        /// <summary>
        /// Roslyn nodes can only be queried through the semantic model of the tree they belong to.
        /// </summary>
        private SemanticModel ModelFor(SyntaxNode node)
        {
            if (node == null || node.SyntaxTree == _model.SyntaxTree)
                return _model;

            foreach (SyntaxTree tree in _model.Compilation.SyntaxTrees)
            {
                if (ReferenceEquals(tree, node.SyntaxTree))
                    return _model.Compilation.GetSemanticModel(node.SyntaxTree);
            }

            bool samePathAsCompilationTree = _model.Compilation.SyntaxTrees.Any(t =>
                string.Equals(t.FilePath, node.SyntaxTree.FilePath, StringComparison.OrdinalIgnoreCase));

            throw new InvalidOperationException(
                $"Syntax node from a tree that is not part of the compilation. Node tree: '{node.SyntaxTree.FilePath}', model tree: '{_model.SyntaxTree.FilePath}', " +
                $"compilation contains {_model.Compilation.SyntaxTrees.Count()} trees, same path present: {samePathAsCompilationTree}.");
        }

        private TypeInfo GetTypeInfo(ExpressionSyntax expression) => ModelFor(expression).GetTypeInfo(expression);

        private SymbolInfo GetSymbolInfo(SyntaxNode node) => ModelFor(node).GetSymbolInfo(node);

        private Microsoft.CodeAnalysis.Optional<object> GetConstantValue(ExpressionSyntax expression) =>
            ModelFor(expression).GetConstantValue(expression);

        private ISymbol GetDeclaredSymbol(SyntaxNode node) => ModelFor(node).GetDeclaredSymbol(node);

        private Slot EmitDiscard(Slot value)
        {
            return value;
        }

        #endregion

        #region Structs

        private StructLayout GetStructLayout(ITypeSymbol type)
        {
            type = Substitute(type);
            if (_structLayouts.TryGetValue(type, out StructLayout layout))
                return layout;

            INamedTypeSymbol named = type as INamedTypeSymbol;
            if (named == null)
                return null;

            layout = new StructLayout(named);
            _structLayouts[type] = layout;
            return layout;
        }

        private Slot MaybeCloneStruct(Slot value)
        {
            if (value?.Type == null || !UdonTypeSystem.IsUserStruct(value.Type))
                return value;

            return CloneStruct(value);
        }

        /// <summary>
        /// Creates a new heap object that is a deep copy of <paramref name="source"/>.
        /// </summary>
        private Slot CloneStruct(Slot source)
        {
            StructLayout layout = GetStructLayout(source.Type);
            if (layout == null)
                return source;

            Slot result = NewObjectArraySlot(layout.Fields.Count, source.Type);

            foreach (IFieldSymbol field in layout.Fields)
            {
                Slot fieldValue = NewSlot(field.Type);
                PushObjectArrayIndex(source, layout.GetIndex(field), fieldValue);

                if (UdonTypeSystem.IsUserStruct(field.Type))
                    fieldValue = CloneStruct(fieldValue);

                StoreStructField(result, layout.GetIndex(field), fieldValue);
            }

            return result;
        }

        private void StoreStructField(Slot container, int index, Slot value)
        {
            _asm.Push(container);
            _asm.Push(IntConstant(index));
            _asm.Push(value);
            Extern("SystemObjectArray.__Set__SystemInt32_SystemObject__SystemVoid", "struct field write");
        }

        private void CreateObjectArray(Slot target, int size)
        {
            _asm.Push(IntConstant(size));
            _asm.Push(target);
            Extern("SystemObjectArray.__ctor__SystemInt32__SystemObjectArray", "struct allocation");
        }

        /// <summary>
        /// Writes the default value for a struct (all fields zero / null) into <paramref name="target"/>.
        /// </summary>
        private void EmitDefaultValue(Slot target, ITypeSymbol type)
        {
            if (UdonTypeSystem.IsUserStruct(type))
            {
                StructLayout layout = GetStructLayout(type);
                if (layout == null)
                    return;

                CreateObjectArray(target, layout.Fields.Count);

                foreach (IFieldSymbol field in layout.Fields)
                {
                    Slot fieldValue = NewSlot(field.Type);
                    EmitDefaultValue(fieldValue, field.Type);
                    StoreStructField(target, layout.GetIndex(field), fieldValue);
                }

                return;
            }

            object defaultValue = GetDefaultValue(type);
            Slot constant = ConstantSlot(type, defaultValue);
            if (target != null && constant != target)
                _asm.Copy(constant, target);
        }

        private object GetDefaultValue(ITypeSymbol type)
        {
            if (type == null)
                return null;

            if (UdonTypeSystem.IsUserStruct(type))
            {
                StructLayout layout = GetStructLayout(type);
                if (layout == null)
                    return null;

                object[] fields = new object[layout.Fields.Count];
                for (int i = 0; i < fields.Length; i++)
                    fields[i] = GetDefaultValue(layout.Fields[i].Type);

                return fields;
            }

            if (type.TypeKind == TypeKind.Enum)
            {
                // User enums are stored as their underlying integer; extern enums keep their real value.
                if (UdonTypeSystem.IsUserEnum(type))
                    return 0;

                Type enumType = UdonTypeSystem.ToSystemType(type);
                return enumType != null ? Enum.ToObject(enumType, 0) : 0;
            }

            if (type.IsValueType)
            {
                Type systemType = UdonTypeSystem.ToSystemType(type);
                if (systemType != null)
                    return Activator.CreateInstance(systemType);

                return null;
            }

            return null;
        }

        #endregion

        #region Entry points

        private readonly Dictionary<string, Slot> _parameterSlots = new Dictionary<string, Slot>();

        private Slot GetParameterSlot(string name, ITypeSymbol type)
        {
            if (_parameterSlots.TryGetValue(name, out Slot existing))
                return existing;

            // The VM writes event arguments by their exact symbol name (onPlayerJoinedPlayer,
            // amount__param, ...), so the heap variable cannot use the compiler's mangled names.
            Slot slot = _asm.AddSlot(name, UdonTypeSystem.GetStorageTypeName(type), type);
            _parameterSlots.Add(name, slot);
            return slot;
        }

        private void EmitEventEntryPoint(UdonEventDefinition definition, IMethodSymbol method)
        {
            _asm.MarkEntryPoint(definition.UdonName);
            _exportedEntryPoints.Add(definition.UdonName);

            if (IsRecursiveMethod(method))
            {
                if (method.Parameters.Length != 0)
                {
                    AddError($"'{method.Name}' is an Udon event with arguments, so it cannot be a [RecursiveMethod]. " +
                             "Move the recursion into a separate method.", method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax());
                    Halt();
                    return;
                }

                EmitRecursiveEntryPointTrampoline(method);
                return;
            }

            if (method.Parameters.Length != definition.ArgumentSymbols.Length)
            {
                AddError($"{method.Name} must take {definition.ArgumentSymbols.Length} argument(s) to match the Udon event.", method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax());
                Halt();
                return;
            }

            ReturnContext returnContext = new ReturnContext { EndLabel = NewLabel("eventEnd") };

            if (!method.ReturnsVoid)
            {
                Slot returnValue = GetParameterSlot("__returnValue", _objectType);
                returnContext.ReturnSlot = returnValue;
            }

            // The structs have to reflect the values that just arrived before user code runs, and the
            // OnChange callbacks fire before the user's handler sees the new state.
            if (definition.UdonName == "_onDeserialization")
            {
                EmitUnpackSyncedStructs();
                EmitSyncedCallbacks();
            }

            using (PushScope())
            {
                for (int i = 0; i < method.Parameters.Length; i++)
                {
                    Slot parameterSlot = GetParameterSlot(definition.ArgumentSymbols[i], method.Parameters[i].Type);

                    if (definition.ArgumentTypes != null)
                        definition.ArgumentTypes[i] = method.Parameters[i].Type;

                    _scopes[_scopes.Count - 1][method.Parameters[i]] = parameterSlot;
                }

                EmitMethodBody(method, returnContext);
            }

            _asm.MarkLabel(returnContext.EndLabel);

            // Packing runs on the way out, so a body that returns early still publishes the structs.
            if (definition.UdonName == "_onPreSerialization")
                EmitPackSyncedStructs();

            Halt();
        }

        private void EmitCustomEventEntryPoint(IMethodSymbol method)
        {
            string name = method.Name;
            if (_exportedEntryPoints.Contains(name))
                return;

            // Events win over public methods with the same name.
            if (UdonRegistry.Instance.TryGetEvent(name, out _))
                return;

            _asm.MarkEntryPoint(name);
            _exportedEntryPoints.Add(name);

            if (IsRecursiveMethod(method))
            {
                EmitRecursiveEntryPointTrampoline(method);
                return;
            }

            ReturnContext returnContext = new ReturnContext { EndLabel = NewLabel("methodEnd") };

            using (PushScope())
            {
                EmitMethodBody(method, returnContext);
            }

            _asm.MarkLabel(returnContext.EndLabel);
            Halt();
        }

        private void EmitMethodBody(IMethodSymbol method, ReturnContext returnContext)
        {
            if (method.DeclaringSyntaxReferences.Length == 0)
                return;

            SyntaxNode node = method.DeclaringSyntaxReferences[0].GetSyntax();

            BlockSyntax body = null;
            ExpressionSyntax expressionBody = null;

            if (node is MethodDeclarationSyntax methodDeclaration)
            {
                body = methodDeclaration.Body;
                expressionBody = methodDeclaration.ExpressionBody?.Expression;
            }
            else if (node is ConstructorDeclarationSyntax constructorDeclaration)
            {
                body = constructorDeclaration.Body;
                expressionBody = constructorDeclaration.ExpressionBody?.Expression;
            }
            else if (node is LocalFunctionStatementSyntax localFunction)
            {
                body = localFunction.Body;
                expressionBody = localFunction.ExpressionBody?.Expression;
            }
            else if (node is AccessorDeclarationSyntax accessorDeclaration)
            {
                body = accessorDeclaration.Body;
                expressionBody = accessorDeclaration.ExpressionBody?.Expression;
            }
            else if (node is PropertyDeclarationSyntax propertyDeclaration)
            {
                expressionBody = propertyDeclaration.ExpressionBody?.Expression;
            }
            else if (node is ParenthesizedLambdaExpressionSyntax parenthesizedLambda)
            {
                body = parenthesizedLambda.Block;
                expressionBody = parenthesizedLambda.ExpressionBody;
            }
            else if (node is SimpleLambdaExpressionSyntax simpleLambda)
            {
                body = simpleLambda.Block;
                expressionBody = simpleLambda.ExpressionBody;
            }
            else if (node is AnonymousMethodExpressionSyntax anonymousMethod)
            {
                body = anonymousMethod.Block;
            }

            if (body == null)
            {
                if (expressionBody != null)
                {
                    Slot value = EmitExpression(expressionBody);

                    if (returnContext.ReturnSlot != null)
                        Copy(ConvertTo(value, method.ReturnType), returnContext.ReturnSlot);

                    return;
                }

                return;
            }

            _returns.Push(returnContext);
            EmitBlock(body);
            _returns.Pop();
        }

        private void Halt() => _asm.Halt();

        #endregion

        #region Statements

        private void EmitBlock(BlockSyntax block)
        {
            using (PushScope())
            {
                foreach (StatementSyntax statement in block.Statements)
                    EmitStatement(statement);
            }
        }

        private void EmitStatement(StatementSyntax statement)
        {
            switch (statement)
            {
                case BlockSyntax block:
                    EmitBlock(block);
                    break;

                case LocalDeclarationStatementSyntax localDeclaration:
                    EmitLocalDeclaration(localDeclaration);
                    break;

                case ExpressionStatementSyntax expressionStatement:
                    EmitExpression(expressionStatement.Expression);
                    break;

                case IfStatementSyntax ifStatement:
                    EmitIf(ifStatement);
                    break;

                case WhileStatementSyntax whileStatement:
                    EmitWhile(whileStatement);
                    break;

                case DoStatementSyntax doStatement:
                    EmitDo(doStatement);
                    break;

                case ForStatementSyntax forStatement:
                    EmitFor(forStatement);
                    break;

                case ForEachStatementSyntax forEachStatement:
                    EmitForEach(forEachStatement);
                    break;

                case ReturnStatementSyntax returnStatement:
                    EmitReturn(returnStatement);
                    break;

                case YieldStatementSyntax yieldStatement:
                    EmitYield(yieldStatement);
                    break;

                case BreakStatementSyntax _:
                    if (_loops.Count > 0)
                        _asm.Jump(_loops.Peek().BreakLabel);
                    break;

                case ContinueStatementSyntax _:
                    if (_loops.Count > 0)
                        _asm.Jump(_loops.Peek().ContinueLabel);
                    break;

                case SwitchStatementSyntax switchStatement:
                    EmitSwitch(switchStatement);
                    break;

                case EmptyStatementSyntax _:
                    break;

                case ThrowStatementSyntax throwStatement:
                    AddError("throw is not supported by the BetterUdon compiler.", throwStatement);
                    break;

                case TryStatementSyntax tryStatement:
                    AddError("try/catch is not supported by the BetterUdon compiler.", tryStatement);
                    break;

                case LocalFunctionStatementSyntax _:
                    // Local functions are inlined at their call sites, the declaration itself emits
                    // no code.
                    break;

                default:
                    AddError($"Unsupported statement '{statement.Kind()}'.", statement);
                    break;
            }
        }

        private void EmitLocalDeclaration(LocalDeclarationStatementSyntax declaration)
        {
            ITypeSymbol declaredType = Substitute(GetTypeInfo(declaration.Declaration.Type).Type);

            foreach (VariableDeclaratorSyntax declarator in declaration.Declaration.Variables)
            {
                ILocalSymbol localSymbol = GetDeclaredSymbol(declarator) as ILocalSymbol;
                ITypeSymbol type = Substitute(localSymbol?.Type ?? declaredType);

                if (localSymbol == null) continue;
                Slot slot = DeclareLocal(localSymbol, type, declarator.Identifier.ValueText);

                // The Udon heap is shared between invocations of the same program, so locals have to be
                // reset to their default value every time the declaration is executed.
                EmitDefaultValue(slot, type);

                if (declarator.Initializer != null &&
                    TryEmitDelegateLocalInitializer(slot, type, declarator.Initializer.Value, declarator))
                    continue;

                if (declarator.Initializer != null)
                {
                    Slot value = EmitExpression(declarator.Initializer.Value);

                    // An error path can produce no value; the diagnostic has already been reported.
                    if (value != null)
                        Copy(ConvertTo(value, type), slot);
                }
            }
        }

        private void EmitIf(IfStatementSyntax ifStatement)
        {
            string elseLabel = NewLabel("else");
            string endLabel = NewLabel("endif");

            Slot condition = EmitCondition(ifStatement.Condition);
            _asm.JumpIfFalse(condition, elseLabel);

            EmitStatement(ifStatement.Statement);

            if (ifStatement.Else != null)
            {
                _asm.Jump(endLabel);
                _asm.MarkLabel(elseLabel);
                EmitStatement(ifStatement.Else.Statement);
                _asm.MarkLabel(endLabel);
            }
            else
            {
                _asm.MarkLabel(elseLabel);
            }
        }

        private Slot EmitCondition(ExpressionSyntax expression)
        {
            Slot value = EmitExpression(expression);
            return ConvertTo(value, _boolType, false);
        }

        private void EmitWhile(WhileStatementSyntax whileStatement)
        {
            string conditionLabel = NewLabel("whileCond");
            string endLabel = NewLabel("whileEnd");

            _asm.MarkLabel(conditionLabel);
            Slot condition = EmitCondition(whileStatement.Condition);
            _asm.JumpIfFalse(condition, endLabel);

            _loops.Push(new LoopContext { BreakLabel = endLabel, ContinueLabel = conditionLabel });
            EmitStatement(whileStatement.Statement);
            _loops.Pop();

            _asm.Jump(conditionLabel);
            _asm.MarkLabel(endLabel);
        }

        private void EmitDo(DoStatementSyntax doStatement)
        {
            string bodyLabel = NewLabel("doBody");
            string conditionLabel = NewLabel("doCond");
            string endLabel = NewLabel("doEnd");

            _asm.MarkLabel(bodyLabel);

            _loops.Push(new LoopContext { BreakLabel = endLabel, ContinueLabel = conditionLabel });
            EmitStatement(doStatement.Statement);
            _loops.Pop();

            _asm.MarkLabel(conditionLabel);
            Slot condition = EmitCondition(doStatement.Condition);
            _asm.JumpIfFalse(condition, endLabel);
            _asm.Jump(bodyLabel);
            _asm.MarkLabel(endLabel);
        }

        private void EmitFor(ForStatementSyntax forStatement)
        {
            using (PushScope())
            {
                if (forStatement.Declaration != null)
                {
                    foreach (VariableDeclaratorSyntax declarator in forStatement.Declaration.Variables)
                    {
                        ILocalSymbol localSymbol = GetDeclaredSymbol(declarator) as ILocalSymbol;
                        ITypeSymbol type = Substitute(localSymbol?.Type);

                        if (localSymbol == null) continue;
                Slot slot = DeclareLocal(localSymbol, type, declarator.Identifier.ValueText);
                        EmitDefaultValue(slot, type);

                        if (declarator.Initializer != null)
                            Copy(ConvertTo(EmitExpression(declarator.Initializer.Value), type), slot);
                    }
                }

                foreach (ExpressionSyntax initializer in forStatement.Initializers)
                    EmitExpression(initializer);

                string conditionLabel = NewLabel("forCond");
                string incrementLabel = NewLabel("forInc");
                string endLabel = NewLabel("forEnd");

                _asm.MarkLabel(conditionLabel);

                if (forStatement.Condition != null)
                {
                    Slot condition = EmitCondition(forStatement.Condition);
                    _asm.JumpIfFalse(condition, endLabel);
                }

                _loops.Push(new LoopContext { BreakLabel = endLabel, ContinueLabel = incrementLabel });
                EmitStatement(forStatement.Statement);
                _loops.Pop();

                _asm.MarkLabel(incrementLabel);
                foreach (ExpressionSyntax incrementor in forStatement.Incrementors)
                    EmitExpression(incrementor);

                _asm.Jump(conditionLabel);
                _asm.MarkLabel(endLabel);
            }
        }

        private void EmitForEach(ForEachStatementSyntax forEachStatement)
        {
            if (TryEmitForEachIterator(forEachStatement))
                return;

            if (TryEmitForEachDictionaryKeys(forEachStatement))
                return;

            if (TryEmitForEachDictionary(forEachStatement))
                return;

            ITypeSymbol collectionType = GetType(forEachStatement.Expression);

            if (TryGetListElementType(collectionType, out ITypeSymbol listElementType))
            {
                EmitForEachList(forEachStatement, listElementType);
                return;
            }

            if (!(collectionType is IArrayTypeSymbol arrayType))
            {
                AddError("foreach is only supported over arrays and List<T>.", forEachStatement);
                return;
            }

            Slot collection = EmitExpression(forEachStatement.Expression);
            ILocalSymbol elementSymbol = GetDeclaredSymbol(forEachStatement) as ILocalSymbol;

            using (PushScope())
            {
                if (elementSymbol == null) return;
                Slot element = DeclareLocal(elementSymbol, arrayType.ElementType, forEachStatement.Identifier.ValueText);
                Slot index = NewSlot(_intType, "index");
                Copy(IntConstant(0), index);

                Slot length = PropertyAccess(collection, collectionType, "Length", _intType, forEachStatement);
                string conditionLabel = NewLabel("feCond");
                string incrementLabel = NewLabel("feInc");
                string endLabel = NewLabel("feEnd");

                _asm.MarkLabel(conditionLabel);

                Slot isLess = EmitBinaryOperator(index, length, _intType, "op_LessThan", _boolType, forEachStatement);
                _asm.JumpIfFalse(isLess, endLabel);

                ArrayElementLValue elementAccess = new ArrayElementLValue(this, collection, index, collectionType, arrayType.ElementType);
                Copy(elementAccess.Get(true), element);

                _loops.Push(new LoopContext { BreakLabel = endLabel, ContinueLabel = incrementLabel });
                EmitStatement(forEachStatement.Statement);
                _loops.Pop();

                _asm.MarkLabel(incrementLabel);
                Slot one = IntConstant(1);
                Slot incremented = EmitBinaryOperator(index, one, _intType, "op_Addition", _intType, forEachStatement);
                Copy(incremented, index);

                _asm.Jump(conditionLabel);
                _asm.MarkLabel(endLabel);
            }
        }

        /// <summary>
        /// <c>foreach</c> over a <c>List&lt;T&gt;</c> iterates the live element count.
        /// </summary>
        private void EmitForEachList(ForEachStatementSyntax forEachStatement, ITypeSymbol elementType)
        {
            Slot list = EmitExpression(forEachStatement.Expression);
            ILocalSymbol elementSymbol = GetDeclaredSymbol(forEachStatement) as ILocalSymbol;

            if (elementSymbol == null)
                return;

            using (PushScope())
            {
                Slot element = DeclareLocal(elementSymbol, elementType, forEachStatement.Identifier.ValueText);
                Slot index = NewSlot(_intType, "i");
                Copy(IntConstant(0), index);

                Slot count = ListCount(list);
                string conditionLabel = NewLabel("feListCond");
                string incrementLabel = NewLabel("feListInc");
                string endLabel = NewLabel("feListEnd");

                _asm.MarkLabel(conditionLabel);

                Slot inRange = EmitBinaryOperator(index, count, _intType, "op_LessThan", _boolType, forEachStatement);
                _asm.JumpIfFalse(inRange, endLabel);

                Copy(ListElementGet(list, elementType, index), element);

                _loops.Push(new LoopContext { BreakLabel = endLabel, ContinueLabel = incrementLabel });
                EmitStatement(forEachStatement.Statement);
                _loops.Pop();

                _asm.MarkLabel(incrementLabel);
                Copy(EmitBinaryOperator(index, IntConstant(1), _intType, "op_Addition", _intType, forEachStatement), index);

                _asm.Jump(conditionLabel);
                _asm.MarkLabel(endLabel);
            }
        }

        private void EmitReturn(ReturnStatementSyntax returnStatement)
        {
            if (_returns.Count == 0)
                return;

            ReturnContext context = _returns.Peek();

            if (returnStatement.Expression != null && context.ReturnSlot != null)
            {
                Slot value = EmitExpression(returnStatement.Expression);
                Copy(ConvertTo(value, context.ReturnSlot.Type), context.ReturnSlot);
            }

            _asm.Jump(context.EndLabel);
        }

        /// <summary>
        /// Emits the equality test for a single switch label, including its <c>when</c> guard.
        /// </summary>
        private void EmitCaseTest(Slot subject, ITypeSymbol switchType, ExpressionSyntax value, WhenClauseSyntax whenClause, string sectionLabel)
        {
            ITypeSymbol constantType = GetType(value) ?? switchType;
            Slot comparison = EmitBinaryOperator(subject, EmitExpression(value), constantType, "op_Equality", _boolType, value);
            string nextLabel = NewLabel("caseNext");

            _asm.JumpIfFalse(comparison, nextLabel);

            if (whenClause != null)
            {
                Slot guard = EmitCondition(whenClause.Condition);
                _asm.JumpIfFalse(guard, nextLabel);
            }

            _asm.Jump(sectionLabel);
            _asm.MarkLabel(nextLabel);
        }

        private void EmitSwitch(SwitchStatementSyntax switchStatement)
        {
            ITypeSymbol switchType = GetType(switchStatement.Expression);
            Slot subject = EmitExpression(switchStatement.Expression);
            string endLabel = NewLabel("switchEnd");

            List<(List<SwitchLabelSyntax> labels, int order)> sections = new List<(List<SwitchLabelSyntax>, int)>();
            string defaultLabel = null;
            List<(string label, SwitchSectionSyntax section)> entries = new List<(string, SwitchSectionSyntax)>();

            foreach (SwitchSectionSyntax section in switchStatement.Sections)
            {
                string sectionLabel = NewLabel("case");

                foreach (SwitchLabelSyntax label in section.Labels)
                {
                    if (label is DefaultSwitchLabelSyntax)
                    {
                        defaultLabel = sectionLabel;
                        continue;
                    }

                    if (label is CaseSwitchLabelSyntax caseLabel)
                    {
                        EmitCaseTest(subject, switchType, caseLabel.Value, null, sectionLabel);
                    }
                    else if (label is CasePatternSwitchLabelSyntax patternLabel &&
                             patternLabel.Pattern is ConstantPatternSyntax constantPattern)
                    {
                        // case 1 when condition:
                        EmitCaseTest(subject, switchType, constantPattern.Expression, patternLabel.WhenClause, sectionLabel);
                    }
                    else
                    {
                        AddError("Only constant case labels are supported.", label);
                    }
                }

                entries.Add((sectionLabel, section));
            }

            if (defaultLabel != null)
                _asm.Jump(defaultLabel);
            else
                _asm.Jump(endLabel);

            _loops.Push(new LoopContext { BreakLabel = endLabel, ContinueLabel = _loops.Count > 0 ? _loops.Peek().ContinueLabel : endLabel });

            foreach ((string label, SwitchSectionSyntax section) in entries)
            {
                _asm.MarkLabel(label);
                using (PushScope())
                {
                    foreach (StatementSyntax statement in section.Statements)
                        EmitStatement(statement);
                }
            }

            _loops.Pop();
            _asm.MarkLabel(endLabel);
        }

        #endregion
    }

    internal static class Slots
    {
        internal sealed class Scope : IDisposable
        {
            private readonly BehaviourCodeGenerator _owner;
            public readonly Dictionary<ISymbol, Slot> Values;

            public Scope(BehaviourCodeGenerator owner, Dictionary<ISymbol, Slot> values)
            {
                _owner = owner;
                Values = values;
            }

            public void Dispose() => _owner.PopScope();
        }
    }
}
