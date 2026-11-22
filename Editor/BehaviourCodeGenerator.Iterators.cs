using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BetterUdon.Editor
{
    /// <summary>
    /// Synchronous iterator support for <c>foreach</c> over a direct call to a local iterator method.
    ///
    /// The iterator body is compiled into the caller as a resumable block. A state slot records which
    /// <c>yield return</c> was reached, and the method's locals live on the heap, so a later
    /// MoveNext-like pass can jump straight back into the loop. This is deliberately a synchronous
    /// lowering: it is not a general coroutine scheduler.
    /// </summary>
    internal sealed partial class BehaviourCodeGenerator
    {
        private sealed class IteratorContext
        {
            public Slot State;
            public Slot Current;
            public ITypeSymbol ElementType;
            public string ValueLabel;
            public string EndLabel;
            public Dictionary<YieldStatementSyntax, int> States;
            public Dictionary<YieldStatementSyntax, string> ResumeLabels;
            public HashSet<YieldStatementSyntax> BreakStatements;
        }

        private readonly Stack<IteratorContext> _iteratorContexts = new Stack<IteratorContext>();

        private IteratorContext CurrentIterator =>
            _iteratorContexts.Count > 0 ? _iteratorContexts.Peek() : null;

        private bool TryEmitForEachIterator(ForEachStatementSyntax forEachStatement)
        {
            if (!(forEachStatement.Expression is InvocationExpressionSyntax invocation))
                return false;

            IMethodSymbol method = GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (method == null || method.DeclaringSyntaxReferences.Length == 0)
                return false;

            if (!TryGetIteratorElementType(method.ReturnType, out ITypeSymbol elementType))
                return false;

            MethodDeclarationSyntax declaration = method.DeclaringSyntaxReferences
                .Select(reference => reference.GetSyntax())
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault();

            if (declaration?.Body == null)
                return false;

            List<YieldStatementSyntax> yields = declaration.Body.DescendantNodes()
                .OfType<YieldStatementSyntax>()
                .Where(yield => yield.ReturnOrBreakKeyword.IsKind(SyntaxKind.ReturnKeyword))
                .ToList();

            if (yields.Count == 0)
                return false;

            IteratorContext context = new IteratorContext
            {
                State = NewSlot(_intType, "iterState"),
                Current = NewSlot(elementType, "iterCurrent"),
                ElementType = elementType,
                ValueLabel = NewLabel("iterValue"),
                EndLabel = NewLabel("iterEnd"),
                States = new Dictionary<YieldStatementSyntax, int>(),
                ResumeLabels = new Dictionary<YieldStatementSyntax, string>(),
                BreakStatements = new HashSet<YieldStatementSyntax>(
                    declaration.Body.DescendantNodes().OfType<YieldStatementSyntax>()
                        .Where(yield => yield.ReturnOrBreakKeyword.IsKind(SyntaxKind.BreakKeyword)))
            };

            for (int i = 0; i < yields.Count; i++)
            {
                context.States[yields[i]] = i + 1;
                context.ResumeLabels[yields[i]] = NewLabel("iterResume");
            }

            Copy(IntConstant(0), context.State);
            EmitDefaultValue(context.Current, elementType);

            if (!TryEmitIteratorArguments(method, invocation, out Slot[] parameterSlots))
                return true;

            _iteratorContexts.Push(context);

            try
            {
                using (PushScope())
                {
                    for (int i = 0; i < method.Parameters.Length && i < parameterSlots.Length; i++)
                    {
                        IParameterSymbol parameter = method.OriginalDefinition.Parameters[i];
                        Slot slot = DeclareLocal(parameter, Substitute(method.Parameters[i].Type), parameter.Name);
                        Copy(ConvertTo(parameterSlots[i], slot.Type), slot);
                    }

                    ILocalSymbol elementSymbol = GetDeclaredSymbol(forEachStatement) as ILocalSymbol;
                    if (elementSymbol == null)
                        return true;

                    Slot element = DeclareLocal(elementSymbol, elementType, forEachStatement.Identifier.ValueText);

                    string loopLabel = NewLabel("iterLoop");
                    _asm.MarkLabel(loopLabel);

                    foreach (YieldStatementSyntax yield in yields)
                    {
                        Slot isState = EmitBinaryOperator(context.State, IntConstant(context.States[yield]),
                            _intType, "op_Equality", _boolType, yield);
                        string next = NewLabel("iterNextState");
                        _asm.JumpIfFalse(isState, next);
                        _asm.Jump(context.ResumeLabels[yield]);
                        _asm.MarkLabel(next);
                    }

                    ReturnContext returnContext = new ReturnContext { EndLabel = context.EndLabel };
                    EmitMethodBody(method, returnContext);
                    _asm.Jump(context.EndLabel);

                    _asm.MarkLabel(context.ValueLabel);
                    Copy(context.Current, element);

                    _loops.Push(new LoopContext { BreakLabel = context.EndLabel, ContinueLabel = loopLabel });
                    EmitStatement(forEachStatement.Statement);
                    _loops.Pop();

                    _asm.Jump(loopLabel);
                    _asm.MarkLabel(context.EndLabel);
                }
            }
            finally
            {
                _iteratorContexts.Pop();
            }

            return true;
        }

        private bool TryEmitIteratorArguments(IMethodSymbol method, InvocationExpressionSyntax invocation, out Slot[] arguments)
        {
            arguments = new Slot[method.Parameters.Length];

            for (int i = 0; i < invocation.ArgumentList.Arguments.Count && i < arguments.Length; i++)
                arguments[i] = EmitExpression(invocation.ArgumentList.Arguments[i].Expression);

            for (int i = 0; i < arguments.Length; i++)
            {
                if (arguments[i] != null)
                    continue;

                if (method.Parameters[i].HasExplicitDefaultValue)
                {
                    arguments[i] = EmitConstant(null, method.Parameters[i].ExplicitDefaultValue,
                        Substitute(method.Parameters[i].Type));
                    continue;
                }

                AddError($"Missing argument for iterator parameter '{method.Parameters[i].Name}'.", invocation);
                return false;
            }

            return true;
        }

        private static bool TryGetIteratorElementType(ITypeSymbol type, out ITypeSymbol elementType)
        {
            elementType = null;

            if (!(type is INamedTypeSymbol named) || !named.IsGenericType || named.TypeArguments.Length != 1)
                return false;

            string definition = named.ConstructedFrom?.ToDisplayString();
            if (definition != "System.Collections.Generic.IEnumerable<T>" &&
                definition != "System.Collections.Generic.IEnumerator<T>")
                return false;

            elementType = named.TypeArguments[0];
            return true;
        }

        private void EmitYield(YieldStatementSyntax yield)
        {
            IteratorContext context = CurrentIterator;

            if (context == null)
            {
                AddError("'yield' is only supported inside an iterator method used directly by foreach.", yield);
                return;
            }

            if (yield.Ancestors().OfType<TryStatementSyntax>().Any())
            {
                AddError("'yield' inside try/catch/finally is not supported yet; move the yield outside the " +
                         "try block.", yield);
                return;
            }

            if (yield.ReturnOrBreakKeyword.IsKind(SyntaxKind.BreakKeyword))
            {
                Copy(IntConstant(-1), context.State);
                _asm.Jump(context.EndLabel);
                return;
            }

            if (!context.States.TryGetValue(yield, out int state))
            {
                AddError("Unable to resolve this yield return statement.", yield);
                return;
            }

            Slot value = EmitExpression(yield.Expression);
            if (value != null)
                Copy(ConvertTo(value, context.ElementType), context.Current);

            Copy(IntConstant(state), context.State);
            _asm.Jump(context.ValueLabel);

            // On the next pass the state dispatch jumps here, continuing after the yield.
            _asm.MarkLabel(context.ResumeLabels[yield]);
        }
    }
}
