using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BetterUdon.Editor
{
    /// <summary>
    /// Small set of generic-constraint intrinsics that do not need a runtime interface implementation.
    /// IComparable&lt;T&gt;.CompareTo on numeric type parameters lowers directly to comparisons.
    /// </summary>
    internal sealed partial class BehaviourCodeGenerator
    {
        private bool TryEmitCompareTo(IMethodSymbol method, Slot instance, List<Slot> arguments, SyntaxNode node, out Slot result)
        {
            result = null;

            if (method.Name != "CompareTo" || method.Parameters.Length != 1 ||
                arguments.Count != 1 || instance == null)
                return false;

            ITypeSymbol type = instance.Type;
            if (type == null || !IsNumericType(type))
                return false;

            result = NewSlot(_intType, "compare");
            Copy(IntConstant(0), result);

            Slot less = EmitBinaryOperator(instance, ConvertTo(arguments[0], type), type,
                "op_LessThan", _boolType, node);
            Slot greater = EmitBinaryOperator(instance, ConvertTo(arguments[0], type), type,
                "op_GreaterThan", _boolType, node);

            string endLabel = NewLabel("compareEnd");
            string notLess = NewLabel("compareNotLess");
            _asm.JumpIfFalse(less, notLess);
            Copy(IntConstant(-1), result);
            _asm.Jump(endLabel);
            _asm.MarkLabel(notLess);

            _asm.JumpIfFalse(greater, endLabel);
            Copy(IntConstant(1), result);
            _asm.MarkLabel(endLabel);
            return true;
        }

        private static bool IsNumericType(ITypeSymbol type)
        {
            switch (type.SpecialType)
            {
                case SpecialType.System_SByte:
                case SpecialType.System_Byte:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                case SpecialType.System_Single:
                case SpecialType.System_Double:
                case SpecialType.System_Decimal:
                case SpecialType.System_Char:
                    return true;

                default:
                    return false;
            }
        }
    }
}
