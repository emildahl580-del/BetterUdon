using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BetterUdon.Editor
{
    /// <summary>
    /// Reading and writing the fields of another BetterUdon behaviour.
    ///
    /// Each generated program owns its own heap, so there is no way to reach another behaviour's
    /// variables directly. Udon exposes exactly one door: the receiver's public variable table, through
    /// <c>GetProgramVariable</c> and <c>SetProgramVariable</c>. The compiler lowers <c>other.field</c>
    /// to those calls, which keeps the C# shape while going through Udon's own mechanism.
    ///
    /// Only exported variables are reachable, because that is all another program can see.
    /// </summary>
    internal sealed partial class BehaviourCodeGenerator
    {
        internal const string BetterUdonBaseTypeName = "BetterUdon.BetterUdonBehaviour";

        private static bool DerivesFromBetterUdon(ITypeSymbol type)
        {
            for (INamedTypeSymbol current = type as INamedTypeSymbol; current != null; current = current.BaseType)
            {
                if (current.ToDisplayString() == BetterUdonBaseTypeName)
                    return true;
            }

            return false;
        }

        private bool ValidateCrossBehaviourField(IFieldSymbol field, SyntaxNode node)
        {
            if (field.IsStatic)
            {
                AddError($"'{field.Name}' is static; only instance variables can be read from another behaviour.", node);
                return false;
            }

            // Inside an inlined method body every field of the receiver is a heap variable, exported or
            // not, so private state is reachable exactly where C# would allow it.
            if (InRemoteBody && IsRemoteMemberOf(field))
                return true;

            if (!IsExported(field))
            {
                AddError($"'{field.Name}' is not a public variable on '{field.ContainingType.Name}', so another behaviour cannot read it. " +
                         "Mark the field public (and not [BetterUdonNoExport], a property or a List<T>).", node);
                return false;
            }

            return true;
        }

        /// <summary>Reads a public variable of another behaviour by symbol name.</summary>
        private Slot EmitGetProgramVariable(Slot receiver, string symbolName, string context)
        {
            Slot raw = NewSlot(_objectType, "progvar");
            _asm.Push(receiver);
            _asm.Push(ConstantSlot(_stringType, symbolName));
            _asm.Push(raw);
            Extern($"{UdonReceiverType}.__GetProgramVariable__SystemString__SystemObject", context);
            return raw;
        }

        /// <summary>Reads <c>receiver.field</c> through the receiver's heap.</summary>
        private Slot EmitReadBehaviourField(Slot receiver, IFieldSymbol field, SyntaxNode node, bool clone = true)
        {
            if (!ValidateCrossBehaviourField(field, node))
                return NewSlot(field.Type, "bad");

            return EmitReadBehaviourMember(receiver, field.Name, field.Type, node, clone);
        }

        /// <summary>Reads a heap variable of another behaviour by symbol name.</summary>
        private Slot EmitReadBehaviourMember(Slot receiver, string symbolName, ITypeSymbol type, SyntaxNode node, bool clone = true)
        {
            Slot raw = EmitGetProgramVariable(receiver, symbolName, "GetProgramVariable");

            // Give the value the member's type so later operations (member access, operators, calls) see
            // the right type, then copy structs so the read keeps value semantics.
            Slot typed = NewSlot(type, "progvarValue");
            Copy(raw, typed);

            return clone ? MaybeCloneStruct(typed) : typed;
        }

        /// <summary>Writes <c>receiver.field = value</c> through the receiver's heap.</summary>
        private void EmitWriteBehaviourField(Slot receiver, IFieldSymbol field, Slot value, SyntaxNode node)
        {
            if (!ValidateCrossBehaviourField(field, node))
                return;

            // ConvertTo clones structs, so the other behaviour does not alias the caller's value.
            EmitWriteBehaviourMember(receiver, field.Name, ConvertTo(value, field.Type));
        }

        private void EmitWriteBehaviourMember(Slot receiver, string symbolName, Slot value)
        {
            _asm.Push(receiver);
            _asm.Push(ConstantSlot(_stringType, symbolName));
            _asm.Push(value);
            Extern($"{UdonReceiverType}.__SetProgramVariable__SystemString_SystemObject__SystemVoid", "SetProgramVariable");
        }

        /// <summary>
        /// Udon has no runtime type test for behaviours, so every program publishes its C# type name in a
        /// hidden variable. A typed component lookup compares it and yields null for the wrong behaviour,
        /// which is what <c>GetComponent&lt;T&gt;()</c> is expected to do.
        /// </summary>
        private void EmitBehaviourTypeCheck(Slot component, ITypeSymbol expectedType, SyntaxNode node)
        {
            Slot actual = EmitGetProgramVariable(component, TypeMarkerSymbol, "behaviour type");
            Slot expected = ConstantSlot(_stringType, expectedType.ToDisplayString());
            Slot isMatch = EmitBinaryOperator(actual, expected, _stringType, "op_Equality", _boolType, node);

            string mismatch = NewLabel("typeMismatch");
            string done = NewLabel("typeMatchDone");

            _asm.JumpIfFalse(isMatch, mismatch);
            _asm.Jump(done);

            _asm.MarkLabel(mismatch);
            Copy(ConstantSlot(_objectType, null), component);

            _asm.MarkLabel(done);
        }

        /// <summary>Hidden heap variable holding the C# type name of the behaviour.</summary>
        internal const string TypeMarkerSymbol = "__betterUdonType";
    }
}
