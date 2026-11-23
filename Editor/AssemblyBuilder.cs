using System;
using System.Collections.Generic;
using System.Text;

namespace BetterUdon.Editor
{
    /// <summary>
    /// A single heap variable in the generated Udon program.
    /// </summary>
    internal sealed class Slot
    {
        public readonly string Name;
        public readonly string UdonType;
        public readonly Microsoft.CodeAnalysis.ITypeSymbol Type;
        public bool Exported;
        public string SyncMode; // null, "none", "linear", "smooth"
        public object InitialValue;
        public bool InitialIsThis;

        /// <summary>
        /// True for compiler generated locals and temporaries that may be shared with another slot
        /// whose lifetime does not overlap. Exported, synced, parameter and constant slots are never
        /// reusable.
        /// </summary>
        public bool Reusable;

        /// <summary>True for a compiler constant.</summary>
        public bool IsConstant;

        /// <summary>True for a user field. Fields can change through the public variable table.</summary>
        public bool IsField;

        /// <summary>True for an event argument supplied by Udon.</summary>
        public bool IsParameter;

        /// <summary>
        /// When set, the initial value becomes this label's bytecode address. Used for the return
        /// address of recursive calls.
        /// </summary>
        public string AddressLabel;

        public Slot(string name, string udonType, Microsoft.CodeAnalysis.ITypeSymbol type)
        {
            Name = name;
            UdonType = udonType;
            Type = type;
        }

        public override string ToString() => Name + ": " + UdonType;
    }

    /// <summary>
    /// Builds the textual Udon assembly for a single program.
    ///
    /// Instructions are kept as structured lines instead of strings so that the optional optimizer can
    /// analyse operand lifetimes and rewrite the program before addresses are resolved.
    /// </summary>
    internal sealed class AssemblyBuilder
    {
        internal enum LineKind
        {
            Instruction,
            Comment,
            Export,
            Label,
        }

        internal sealed class Line
        {
            public LineKind Kind;
            public int Size;

            // Comment and export text (already includes any prefix).
            public string Text;

            // Instruction.
            public string Op;
            public string Operand;
            public string JumpTarget;
            public bool Halt;

            /// <summary>
            /// For an EXTERN, the zero based input operand indices that the extern writes back into
            /// (ref/out arguments).
            /// </summary>
            public int[] ByRefInputs;

            // Label.
            public string Label;
            public bool EmitLabel;

            public bool Removed;
        }

        // Instruction sizes in bytes, matching the Udon bytecode encoding (one 4 byte word per
        // opcode and per operand).
        public const int SizePush = 8;
        public const int SizePop = 4;
        public const int SizeCopy = 4;
        public const int SizeJump = 8;
        public const int SizeJumpIfFalse = 8;
        public const int SizeExtern = 8;
        public const int SizeJumpIndirect = 8;
        public const int SizeNop = 4;

        private readonly List<Line> _lines = new List<Line>();
        private readonly List<Slot> _slots = new List<Slot>();
        private readonly List<string> _exportStatements = new List<string>();
        private readonly Dictionary<string, uint> _labels = new Dictionary<string, uint>();
        private readonly HashSet<string> _labelNames = new HashSet<string>();
        private readonly HashSet<string> _externSignatures = new HashSet<string>();

        public IReadOnlyList<Slot> Slots => _slots;
        public IReadOnlyCollection<string> ExternSignatures => _externSignatures;

        internal List<Line> Lines => _lines;

        public Slot AddSlot(string name, string udonType, Microsoft.CodeAnalysis.ITypeSymbol type)
        {
            Slot slot = new Slot(name, udonType, type);
            _slots.Add(slot);
            return slot;
        }

        public void AddExport(string symbolName)
        {
            _exportStatements.Add(symbolName);
        }

        public void AddComment(string comment)
        {
            _lines.Add(new Line { Kind = LineKind.Comment, Text = "# " + comment });
        }

        public void MarkLabel(string name)
        {
            if (!_labelNames.Add(name))
                throw new InvalidOperationException($"Duplicate assembly label '{name}'.");

            _lines.Add(new Line { Kind = LineKind.Label, Label = name });
        }

        /// <summary>
        /// Defines a code symbol and exports it so that Udon can invoke it as an event entry point.
        /// </summary>
        public void MarkEntryPoint(string name)
        {
            if (!_labelNames.Add(name))
                throw new InvalidOperationException($"Duplicate assembly label '{name}'.");

            _lines.Add(new Line { Kind = LineKind.Export, Text = ".export " + name });
            _lines.Add(new Line { Kind = LineKind.Label, Label = name, EmitLabel = true });
        }

        public bool HasLabel(string name) => _labelNames.Contains(name);

        public void Push(Slot slot) => AddInstruction("PUSH", slot.Name, SizePush);

        public void Pop() => AddInstruction("POP", null, SizePop);

        public void Copy(Slot source, Slot target)
        {
            Push(source);
            Push(target);
            AddInstruction("COPY", null, SizeCopy);
        }

        public void Jump(string label) =>
            AddInstruction("JUMP", null, SizeJump, label);

        public void JumpIfFalse(Slot condition, string label)
        {
            Push(condition);
            AddInstruction("JUMP_IF_FALSE", null, SizeJumpIfFalse, label);
        }

        public void JumpIndirect(Slot addressSlot) =>
            AddInstruction("JUMP_INDIRECT", addressSlot.Name, SizeJumpIndirect);

        /// <summary>
        /// Copies the value whose address is on top of the evaluation stack into <paramref name="target"/>.
        /// Used to receive the return address of a recursive call.
        /// </summary>
        public void CopyFromTop(Slot target)
        {
            Push(target);
            AddInstruction("COPY", null, SizeCopy);
        }

        public void Extern(string signature)
        {
            Extern(signature, null);
        }

        /// <summary>
        /// Emits an extern call. <paramref name="byRefInputs"/> lists the input operand indices that
        /// the extern writes back into, so the optimizer sees those slots as definitions.
        /// </summary>
        public void Extern(string signature, int[] byRefInputs)
        {
            _externSignatures.Add(signature);
            AddInstruction("EXTERN", signature, SizeExtern);
            _lines[_lines.Count - 1].ByRefInputs = byRefInputs;
        }

        public void Nop() => AddInstruction("NOP", null, SizeNop);

        /// <summary>
        /// Ends execution of the program (jumps past the end of the byte code).
        /// </summary>
        public void Halt() => AddInstruction("JUMP", null, SizeJump, null, true);

        private void AddInstruction(string op, string operand, int size, string jumpTarget = null, bool halt = false)
        {
            _lines.Add(new Line
            {
                Kind = LineKind.Instruction,
                Op = op,
                Operand = operand,
                Size = size,
                JumpTarget = jumpTarget,
                Halt = halt,
            });
        }

        /// <summary>
        /// Runs the requested buffer optimizations. No-op instructions are removed, unreachable code is
        /// dropped and compiler temporaries are coalesced. Slots are only ever shared when their live
        /// ranges do not overlap and their heap storage type is identical.
        /// </summary>
        public void Optimize(UdonOptimizationFlags flags)
        {
            if (flags == UdonOptimizationFlags.None)
                return;

            UdonAssemblyOptimizer.Optimize(this, flags);
        }

        /// <summary>
        /// Computes the address of every label from the current (possibly optimized) instruction list.
        /// </summary>
        internal void ComputeLabelAddresses()
        {
            _labels.Clear();

            uint address = 0;

            foreach (Line line in _lines)
            {
                if (line.Removed)
                    continue;

                if (line.Kind == LineKind.Label)
                    _labels[line.Label] = address;
                else if (line.Kind == LineKind.Instruction)
                    address += (uint)line.Size;
            }
        }

        internal List<Slot> MutableSlots => _slots;

        /// <summary>
        /// Computes the number of heap values the assembler will create: one per declared symbol plus
        /// one hidden value per unique extern string.
        /// </summary>
        public uint GetHeapSize()
        {
            return (uint)_slots.Count + (uint)_externSignatures.Count;
        }

        public string BuildAssembly()
        {
            PruneExterns();
            ComputeLabelAddresses();

            StringBuilder builder = new StringBuilder();

            // Label addresses are final once every instruction has been added, so address constants can
            // be resolved before the data section is written.
            foreach (Slot slot in _slots)
            {
                if (!string.IsNullOrEmpty(slot.AddressLabel))
                    slot.InitialValue = _labels.TryGetValue(slot.AddressLabel, out uint address) ? (object)address : 0u;
            }

            builder.Append(".data_start\n");

            HashSet<string> writtenExports = new HashSet<string>();
            foreach (string export in _exportStatements)
            {
                if (writtenExports.Add(export))
                    builder.Append("    .export ").Append(export).Append('\n');
            }

            foreach (Slot slot in _slots)
            {
                if (slot.SyncMode != null)
                    builder.Append("    .sync ").Append(slot.Name).Append(", ").Append(slot.SyncMode).Append('\n');
            }

            foreach (Slot slot in _slots)
            {
                builder.Append("    ").Append(slot.Name).Append(": %").Append(slot.UdonType)
                    .Append(slot.InitialIsThis ? ", this\n" : ", null\n");
            }

            builder.Append(".data_end\n");
            builder.Append(".code_start\n");

            foreach (Line line in _lines)
            {
                if (line.Removed)
                    continue;

                switch (line.Kind)
                {
                    case LineKind.Comment:
                        builder.Append(line.Text).Append('\n');
                        break;

                    case LineKind.Export:
                        builder.Append("        ").Append(line.Text).Append('\n');
                        break;

                    case LineKind.Label:
                        if (line.EmitLabel)
                            builder.Append("        ").Append(line.Label).Append(":\n");
                        break;

                    case LineKind.Instruction:
                        builder.Append("        ").Append(FormatInstruction(line)).Append('\n');
                        break;
                }
            }

            builder.Append(".code_end\n");
            return builder.ToString();
        }

        private void PruneExterns()
        {
            _externSignatures.Clear();

            foreach (Line line in _lines)
            {
                if (!line.Removed && line.Kind == LineKind.Instruction && line.Op == "EXTERN")
                    _externSignatures.Add(line.Operand);
            }
        }

        private string FormatInstruction(Line line)
        {
            switch (line.Op)
            {
                case "PUSH":
                    return "PUSH, " + line.Operand;

                case "POP":
                case "COPY":
                case "NOP":
                    return line.Op;

                case "JUMP":
                    return line.Halt
                        ? "JUMP, 0xFFFFFFFC"
                        : "JUMP, 0x" + ResolveLabel(line.JumpTarget).ToString("X8");

                case "JUMP_IF_FALSE":
                    return "JUMP_IF_FALSE, 0x" + ResolveLabel(line.JumpTarget).ToString("X8");

                case "JUMP_INDIRECT":
                    return "JUMP_INDIRECT, " + line.Operand;

                case "EXTERN":
                    return "EXTERN, \"" + line.Operand + "\"";

                default:
                    return line.Op;
            }
        }

        private uint ResolveLabel(string label)
        {
            if (label != null && _labels.TryGetValue(label, out uint target))
                return target;

            throw new InvalidOperationException($"Jump to undefined label '{label}'.");
        }
    }
}
