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
    /// </summary>
    internal sealed class AssemblyBuilder
    {
        private sealed class Line
        {
            public string Text;
            public int Size;
            public string LabelTarget; // when set the last operand is replaced with the resolved address
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
        private readonly List<string> _labelOrder = new List<string>();
        private readonly HashSet<string> _externSignatures = new HashSet<string>();
        private uint _address;

        public IReadOnlyList<Slot> Slots => _slots;
        public IReadOnlyCollection<string> ExternSignatures => _externSignatures;

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
            _lines.Add(new Line { Text = "# " + comment, Size = 0 });
        }

        public void MarkLabel(string name)
        {
            if (_labels.ContainsKey(name))
                throw new InvalidOperationException($"Duplicate assembly label '{name}'.");

            _labels.Add(name, _address);
            _labelOrder.Add(name);
        }

        /// <summary>
        /// Defines a code symbol and exports it so that Udon can invoke it as an event entry point.
        /// </summary>
        public void MarkEntryPoint(string name)
        {
            if (_labels.ContainsKey(name))
                throw new InvalidOperationException($"Duplicate assembly label '{name}'.");

            _labels.Add(name, _address);
            _labelOrder.Add(name);

            _lines.Add(new Line { Text = ".export " + name, Size = 0 });
            _lines.Add(new Line { Text = name + ":", Size = 0 });
        }

        public bool HasLabel(string name) => _labels.ContainsKey(name);

        public void Push(Slot slot) => AddLine($"PUSH, {slot.Name}", SizePush);

        public void Pop() => AddLine("POP", SizePop);

        public void Copy(Slot source, Slot target)
        {
            Push(source);
            Push(target);
            AddLine("COPY", SizeCopy);
        }

        public void Jump(string label) => AddLine(null, SizeJump, label, "JUMP, 0x{0:X8}");

        public void JumpIfFalse(Slot condition, string label)
        {
            Push(condition);
            AddLine(null, SizeJumpIfFalse, label, "JUMP_IF_FALSE, 0x{0:X8}");
        }

        public void JumpIndirect(Slot addressSlot) => AddLine($"JUMP_INDIRECT, {addressSlot.Name}", SizeJumpIndirect);

        /// <summary>
        /// Copies the value whose address is on top of the evaluation stack into <paramref name="target"/>.
        /// Used to receive the return address of a recursive call.
        /// </summary>
        public void CopyFromTop(Slot target)
        {
            Push(target);
            AddLine("COPY", SizeCopy);
        }

        public void Extern(string signature)
        {
            _externSignatures.Add(signature);
            AddLine($"EXTERN, \"{signature}\"", SizeExtern);
        }

        public void Nop() => AddLine("NOP", SizeNop);

        /// <summary>
        /// Ends execution of the program (jumps past the end of the byte code).
        /// </summary>
        public void Halt() => AddLine("JUMP, 0xFFFFFFFC", SizeJump);

        private void AddLine(string text, int size)
        {
            _lines.Add(new Line { Text = text, Size = size });
            _address += (uint)size;
        }

        private void AddLine(string _, int size, string labelTarget, string format)
        {
            _lines.Add(new Line { Text = format, Size = size, LabelTarget = labelTarget });
            _address += (uint)size;
        }

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
                if (line.LabelTarget != null)
                {
                    if (!_labels.TryGetValue(line.LabelTarget, out uint target))
                        throw new InvalidOperationException($"Jump to undefined label '{line.LabelTarget}'.");

                    builder.Append("        ").Append(string.Format(line.Text, target)).Append('\n');
                }
                else
                {
                    if (line.Text.StartsWith("#", StringComparison.Ordinal))
                        builder.Append(line.Text).Append('\n');
                    else
                        builder.Append("        ").Append(line.Text).Append('\n');
                }
            }

            builder.Append(".code_end\n");
            return builder.ToString();
        }
    }
}
