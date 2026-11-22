using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BetterUdon.Editor
{
    /// <summary>
    /// Recursion support.
    ///
    /// A method marked <c>[RecursiveMethod]</c> is compiled once as a labelled block instead of being
    /// inlined. A call pushes the return address onto Udon's evaluation stack, jumps to the block, and
    /// the block returns with <c>JUMP_INDIRECT</c>. Re-entering the same block would overwrite the
    /// caller's locals, so the slots allocated so far in the block are saved on a manual heap stack
    /// before a recursive call and restored after it returns.
    /// </summary>
    internal sealed partial class BehaviourCodeGenerator
    {
        private sealed class RecursiveMethodInfo
        {
            public IMethodSymbol Method;
            public string Label;
            public Slot ReturnSlot;
            public readonly List<Slot> ParameterSlots = new List<Slot>();
            public int FrameStart;
            public int FrameEnd;
            public bool Emitted;
        }

        private readonly Dictionary<IMethodSymbol, RecursiveMethodInfo> _recursiveMethods =
            new Dictionary<IMethodSymbol, RecursiveMethodInfo>(SymbolEqualityComparer.Default);

        private Slot _recStack;
        private Slot _recStackAddress;
        private IMethodSymbol _currentRecursiveBlock;

        private static bool IsRecursiveMethod(IMethodSymbol method)
        {
            return method.GetAttributes().Any(a => a.AttributeClass?.Name == "RecursiveMethodAttribute");
        }

        private RecursiveMethodInfo GetRecursiveInfo(IMethodSymbol method)
        {
            if (!_recursiveMethods.TryGetValue(method, out RecursiveMethodInfo info))
            {
                info = new RecursiveMethodInfo { Method = method };
                _recursiveMethods.Add(method, info);
            }

            return info;
        }

        private void EnsureRecursiveStack()
        {
            if (_recStack != null)
                return;

            _recStack = NewSlot(_model.Compilation.CreateArrayTypeSymbol(_objectType), "recStack");
            _recStack.InitialValue = new object[128];

            _recStackAddress = NewSlot(_intType, "recStackAddress");
            _recStackAddress.InitialValue = 0;
        }

        /// <summary>Assigns labels to every recursive method before any code is emitted.</summary>
        private void PrepareRecursiveMethods(List<IMethodSymbol> methods)
        {
            List<IMethodSymbol> recursive = methods.Where(IsRecursiveMethod).ToList();

            if (recursive.Count == 0)
                return;

            EnsureRecursiveStack();

            foreach (IMethodSymbol method in recursive)
                GetRecursiveInfo(method).Label = "__method_" + UdonTypeSystem.Sanitize(method.Name) + "_" + (_labelCounter++);
        }

        private void EmitRecursiveMethodBlocks(List<IMethodSymbol> methods)
        {
            foreach (IMethodSymbol method in methods.Where(IsRecursiveMethod))
                EmitRecursiveBlock(method);
        }

        /// <summary>Compiles the method body once, as a block reached by <c>JUMP</c>.</summary>
        private void EmitRecursiveBlock(IMethodSymbol method)
        {
            RecursiveMethodInfo info = GetRecursiveInfo(method);

            if (info.Emitted)
                return;

            info.Emitted = true;
            _asm.MarkLabel(info.Label);

            info.FrameStart = _asm.Slots.Count;

            ReturnContext returnContext = new ReturnContext { EndLabel = NewLabel("recEnd") };

            if (!method.ReturnsVoid)
            {
                info.ReturnSlot = NewSlot(method.ReturnType, "recRet");
                returnContext.ReturnSlot = info.ReturnSlot;

                // A frame push happens before the first return of this invocation, so the return slot
                // must already hold a valid (default) value rather than null.
                EmitDefaultValue(info.ReturnSlot, method.ReturnType);
            }

            IMethodSymbol previousBlock = _currentRecursiveBlock;
            _currentRecursiveBlock = method;

            try
            {
                using (PushScope())
                {
                    foreach (IParameterSymbol parameter in method.Parameters)
                    {
                        if (parameter.RefKind != RefKind.None)
                        {
                            AddError($"'{method.Name}' is recursive, so its parameters cannot be ref/out/in.",
                                method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax());
                        }

                        Slot parameterSlot = NewSlot(parameter.Type, "recParam");
                        _scopes[_scopes.Count - 1][parameter] = parameterSlot;
                        info.ParameterSlots.Add(parameterSlot);
                    }

                    if (method.Parameters.Length != info.ParameterSlots.Count)
                    {
                        AddError($"'{method.Name}' cannot be compiled as a recursive method.", null);
                        return;
                    }

                    EmitMethodBody(method, returnContext);
                }
            }
            finally
            {
                _currentRecursiveBlock = previousBlock;
            }

            _asm.MarkLabel(returnContext.EndLabel);

            // The caller pushed the return address; copy it out of the evaluation stack and jump back.
            Slot returnAddress = NewSlot(_uintType, "recReturnAddress");
            _asm.CopyFromTop(returnAddress);
            _asm.JumpIndirect(returnAddress);

            info.FrameEnd = _asm.Slots.Count;
        }

        /// <summary>Emits a call to a recursive method from a non-recursive body.</summary>
        private Slot EmitRecursiveEntryCall(IMethodSymbol method, List<Slot> arguments, SyntaxNode node)
        {
            RecursiveMethodInfo info = GetRecursiveInfo(method);

            if (!info.Emitted)
            {
                AddError($"'{method.Name}' is recursive and calls another recursive method; mutual recursion is not " +
                         "supported. Mark only the method that recurses and call the other one normally.", node);
                return method.ReturnsVoid ? null : NewSlot(method.ReturnType, "bad");
            }

            if (!BindRecursiveParameters(info, arguments, node))
                return method.ReturnsVoid ? null : NewSlot(method.ReturnType, "bad");

            string returnLabel = NewLabel("recCallReturn");
            Slot address = NewSlot(_uintType, "recCallAddress");
            address.AddressLabel = returnLabel;

            _asm.Push(address);
            _asm.Jump(info.Label);
            _asm.MarkLabel(returnLabel);

            if (method.ReturnsVoid)
                return null;

            Slot result = NewSlot(method.ReturnType, "recResult");
            Copy(info.ReturnSlot, result);
            return result;
        }

        /// <summary>Emits a recursive call from inside the block that is currently being emitted.</summary>
        private Slot EmitRecursiveSelfCall(IMethodSymbol method, List<Slot> arguments, SyntaxNode node)
        {
            RecursiveMethodInfo info = GetRecursiveInfo(method);

            // Everything the block has used so far is live at this point and has to survive re-entry.
            List<Slot> frame = _asm.Slots.Skip(info.FrameStart).Take(_asm.Slots.Count - info.FrameStart).ToList();

            EmitEnsureStackCapacity(frame.Count + 1);

            foreach (Slot slot in frame)
                EmitPushFrameValue(slot);

            if (!BindRecursiveParameters(info, arguments, node))
                return method.ReturnsVoid ? null : NewSlot(method.ReturnType, "bad");

            string returnLabel = NewLabel("recSelfReturn");
            Slot address = NewSlot(_uintType, "recSelfAddress");
            address.AddressLabel = returnLabel;

            _asm.Push(address);
            _asm.Jump(info.Label);
            _asm.MarkLabel(returnLabel);

            // Read the result before restoring the frame: the return slot belongs to the frame.
            Slot result = null;
            if (!method.ReturnsVoid)
            {
                result = NewSlot(method.ReturnType, "recResult");
                Copy(info.ReturnSlot, result);
            }

            for (int i = frame.Count - 1; i >= 0; i--)
                Copy(EmitPopFrameValue(frame[i].Type), frame[i]);

            return result;
        }

        private bool BindRecursiveParameters(RecursiveMethodInfo info, List<Slot> arguments, SyntaxNode node)
        {
            if (arguments.Count != info.ParameterSlots.Count)
                return true;

            for (int i = 0; i < info.ParameterSlots.Count; i++)
                Copy(ConvertTo(arguments[i], info.ParameterSlots[i].Type), info.ParameterSlots[i]);

            return true;
        }

        private void EmitPushFrameValue(Slot value)
        {
            Slot saved = value;

            // Structs are reference arrays on the heap, so a frame slot can still be null (nothing has
            // assigned it yet in this invocation). Copy only when there is a value to copy.
            if (UdonTypeSystem.IsUserStruct(value.Type))
            {
                saved = NewSlot(value.Type, "recSave");
                Copy(value, saved);

                Slot isNull = EmitBinaryOperator(value, ConstantSlot(_objectType, null), _objectType,
                    "op_Equality", _boolType, null);

                string notNull = NewLabel("recSaveNotNull");
                string done = NewLabel("recSaveDone");

                _asm.JumpIfFalse(isNull, notNull);
                _asm.Jump(done);

                _asm.MarkLabel(notNull);
                Copy(MaybeCloneStruct(value), saved);
                _asm.MarkLabel(done);
            }

            ArrayElementLValue element = new ArrayElementLValue(this, _recStack, _recStackAddress, _objectArrayType, _objectType);
            element.Set(saved);

            Slot next = EmitBinaryOperator(_recStackAddress, IntConstant(1), _intType, "op_Addition", _intType, null);
            Copy(next, _recStackAddress);
        }

        private Slot EmitPopFrameValue(ITypeSymbol type)
        {
            Slot previous = EmitBinaryOperator(_recStackAddress, IntConstant(1), _intType, "op_Subtraction", _intType, null);
            Copy(previous, _recStackAddress);

            ArrayElementLValue element = new ArrayElementLValue(this, _recStack, _recStackAddress, _objectArrayType, _objectType);
            Slot raw = element.Get(false);

            Slot typed = NewSlot(type, "recPop");
            Copy(raw, typed);
            return typed;
        }

        /// <summary>Doubles the manual stack when it is too small for the next frame.</summary>
        private void EmitEnsureStackCapacity(int count)
        {
            Slot needed = EmitBinaryOperator(_recStackAddress, IntConstant(count), _intType, "op_Addition", _intType, null);
            Slot capacity = ArrayLength(_recStack);
            Slot full = EmitBinaryOperator(needed, capacity, _intType, "op_GreaterThanOrEqual", _boolType, null);

            string enough = NewLabel("recStackEnough");
            _asm.JumpIfFalse(full, enough);

            // Grow past what this frame needs: doubling a small stack is not enough for a method with
            // hundreds of live slots.
            Slot newCapacity = EmitBinaryOperator(needed, IntConstant(2), _intType, "op_Multiply", _intType, null);
            Slot newStack = NewSlot(_objectArrayType, "recStackNew");

            _asm.Push(newCapacity);
            _asm.Push(newStack);
            Extern("SystemObjectArray.__ctor__SystemInt32__SystemObjectArray", "recursion stack allocation");

            _asm.Push(_recStack);
            _asm.Push(newStack);
            _asm.Push(capacity);
            Extern("SystemArray.__Copy__SystemArray_SystemArray_SystemInt32__SystemVoid", "recursion stack copy");

            Copy(newStack, _recStack);
            _asm.MarkLabel(enough);
        }

        /// <summary>Raises a recursive method as an entry point (trampoline into its block).</summary>
        private void EmitRecursiveEntryPointTrampoline(IMethodSymbol method)
        {
            RecursiveMethodInfo info = GetRecursiveInfo(method);

            string returnLabel = NewLabel("recEntryReturn");
            Slot address = NewSlot(_uintType, "recEntryAddress");
            address.AddressLabel = returnLabel;

            _asm.Push(address);
            _asm.Jump(info.Label);
            _asm.MarkLabel(returnLabel);
            Halt();
        }
    }
}
