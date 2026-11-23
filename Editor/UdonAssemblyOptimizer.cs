using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterUdon.Editor
{
    /// <summary>
    /// Behaviour preserving output optimizations over the structured assembly.
    ///
    /// The optimizer works on complete push/instruction operations so that it can reason about which
    /// heap slots are read and written by an Udon extern. It never removes field, constant, parameter,
    /// exported or synced slots, and it never shares two slots that are live at the same time.
    /// </summary>
    internal static class UdonAssemblyOptimizer
    {
        private sealed class Operation
        {
            public int InstructionLine;
            public int[] PushLines = Array.Empty<int>();
            public readonly List<string> Operands = new List<string>();
            public readonly List<string> Uses = new List<string>();
            public readonly List<string> ByRefDefs = new List<string>();
            public string Def;
            public string Opcode;
            public string JumpTarget;
            public bool Halt;
            public bool Removed;
        }

        private sealed class ParseResult
        {
            public readonly List<Operation> Operations = new List<Operation>();
            public bool Success = true;
        }

        private sealed class PushEntry
        {
            public int Line;
            public string Slot;

            public PushEntry(int line, string slot)
            {
                Line = line;
                Slot = slot;
            }
        }

        private sealed class Block
        {
            public int Index;
            public readonly List<string> Labels = new List<string>();
            public readonly List<Operation> Operations = new List<Operation>();
            public readonly List<int> Successors = new List<int>();
            public readonly HashSet<string> Use = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> Def = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> LiveIn = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> LiveOut = new HashSet<string>(StringComparer.Ordinal);
            public bool DynamicExit;
        }

        public static void Optimize(AssemblyBuilder assembly, UdonOptimizationFlags flags)
        {
            List<AssemblyBuilder.Line> lines = assembly.Lines;

            if ((flags & UdonOptimizationFlags.Peephole) != 0)
            {
                RemoveTrivialInstructions(lines);
                RemoveUnreachableCode(lines);
                ThreadJumps(lines);
                RemoveTrivialInstructions(lines);
            }
            else if ((flags & UdonOptimizationFlags.DeadCode) != 0)
            {
                RemoveUnreachableCode(lines);
            }

            if ((flags & UdonOptimizationFlags.CopyPropagation) != 0)
            {
                PropagateReadOnlyCopies(assembly);
                RemoveDeadCopies(assembly);
            }

            if ((flags & UdonOptimizationFlags.Peephole) != 0)
                RemoveTrivialInstructions(lines);

            if ((flags & (UdonOptimizationFlags.DeadCode | UdonOptimizationFlags.CopyPropagation)) != 0)
                RemoveUnusedSlots(assembly);

            if ((flags & UdonOptimizationFlags.SlotReuse) != 0)
            {
                ReuseSlots(assembly);
                RemoveUnusedSlots(assembly);
            }
        }

        #region Parsing

        private static ParseResult ParseOperations(List<AssemblyBuilder.Line> lines)
        {
            ParseResult result = new ParseResult();
            List<PushEntry> pending = new List<PushEntry>();

            for (int i = 0; i < lines.Count; i++)
            {
                AssemblyBuilder.Line line = lines[i];

                if (line.Removed)
                    continue;

                if (line.Kind == AssemblyBuilder.LineKind.Label)
                {
                    // The generated code never carries the evaluation stack across a basic block
                    // boundary, except for recursive call addresses. Those are handled by the
                    // conservative fallback below, not by the lifetime optimizer.
                    if (pending.Count > 0)
                        result.Success = false;

                    pending.Clear();
                    continue;
                }

                if (line.Kind != AssemblyBuilder.LineKind.Instruction)
                    continue;

                if (line.Op == "PUSH")
                {
                    pending.Add(new PushEntry(i, line.Operand));
                    continue;
                }

                Operation operation = CreateOperation(i, line);

                switch (line.Op)
                {
                    case "NOP":
                        break;

                    case "COPY":
                        if (pending.Count < 2)
                        {
                            result.Success = false;
                            break;
                        }

                        List<PushEntry> copy = Take(pending, 2);
                        operation.PushLines = PendingLines(copy);
                        operation.Operands.Add(copy[0].Slot);
                        operation.Def = copy[1].Slot;
                        break;

                    case "POP":
                        if (pending.Count < 1)
                        {
                            result.Success = false;
                            break;
                        }

                        List<PushEntry> pop = Take(pending, 1);
                        operation.PushLines = PendingLines(pop);
                        operation.Operands.Add(pop[0].Slot);
                        break;

                    case "EXTERN":
                        if (!UdonRegistry.Instance.TryGetArity(line.Operand, out UdonRegistry.UdonNodeArity arity) ||
                            arity.Outputs > 1)
                        {
                            // Without the real arity the operand stack cannot be reconstructed.
                            result.Success = false;
                            break;
                        }

                        int operandCount = arity.Inputs + arity.Outputs;

                        if (pending.Count < operandCount)
                        {
                            result.Success = false;
                            break;
                        }

                        List<PushEntry> externOperands = Take(pending, operandCount);
                        operation.PushLines = PendingLines(externOperands);

                        for (int p = 0; p < arity.Inputs; p++)
                            operation.Operands.Add(externOperands[p].Slot);

                        if (arity.Outputs == 1)
                            operation.Def = externOperands[operandCount - 1].Slot;

                        if (line.ByRefInputs != null)
                        {
                            foreach (int byRef in line.ByRefInputs)
                            {
                                if (byRef < 0 || byRef >= arity.Inputs)
                                {
                                    result.Success = false;
                                    continue;
                                }

                                operation.ByRefDefs.Add(externOperands[byRef].Slot);
                            }
                        }

                        break;

                    case "JUMP_IF_FALSE":
                        if (pending.Count < 1)
                        {
                            result.Success = false;
                            break;
                        }

                        List<PushEntry> condition = Take(pending, 1);
                        operation.PushLines = PendingLines(condition);
                        operation.Operands.Add(condition[0].Slot);
                        break;

                    case "JUMP":
                        // A recursive call leaves the return address on the evaluation stack for the
                        // callee. Static lifetime analysis cannot model that transfer.
                        if (pending.Count > 0)
                            result.Success = false;
                        break;

                    case "JUMP_INDIRECT":
                        if (pending.Count > 0)
                            result.Success = false;

                        if (!string.IsNullOrEmpty(line.Operand))
                            operation.Operands.Add(line.Operand);
                        break;
                }

                // Values below the consumed operands stay on the evaluation stack and are therefore
                // live through this instruction.
                operation.Uses.AddRange(operation.Operands);

                foreach (PushEntry carried in pending)
                    operation.Uses.Add(carried.Slot);

                result.Operations.Add(operation);
            }

            if (pending.Count > 0)
                result.Success = false;

            return result;
        }

        private static List<PushEntry> Take(List<PushEntry> pending, int count)
        {
            int start = pending.Count - count;
            List<PushEntry> taken = pending.GetRange(start, count);
            pending.RemoveRange(start, count);
            return taken;
        }

        private static Operation CreateOperation(int lineIndex, AssemblyBuilder.Line line)
        {
            return new Operation
            {
                InstructionLine = lineIndex,
                Opcode = line.Op,
                JumpTarget = line.JumpTarget,
                Halt = line.Halt,
            };
        }

        private static int[] PendingLines(List<PushEntry> pending)
        {
            int[] result = new int[pending.Count];

            for (int i = 0; i < pending.Count; i++)
                result[i] = pending[i].Line;

            return result;
        }

        private static void RemoveOperation(Operation operation, List<AssemblyBuilder.Line> lines)
        {
            operation.Removed = true;

            foreach (int lineIndex in operation.PushLines)
                lines[lineIndex].Removed = true;

            lines[operation.InstructionLine].Removed = true;
        }

        #endregion

        #region Peephole

        private static void RemoveTrivialInstructions(List<AssemblyBuilder.Line> lines)
        {
            ParseResult parsed = ParseOperations(lines);

            if (!parsed.Success)
                return;

            List<Operation> operations = parsed.Operations;
            Dictionary<string, uint> labels = ComputeLabelAddresses(lines);
            uint[] lineAddresses = ComputeLineAddresses(lines);

            foreach (Operation operation in operations)
            {
                switch (operation.Opcode)
                {
                    case "NOP":
                        RemoveOperation(operation, lines);
                        break;

                    case "COPY":
                        if (operation.Def != null &&
                            operation.Operands.Count == 1 &&
                            string.Equals(operation.Operands[0], operation.Def, StringComparison.Ordinal))
                        {
                            RemoveOperation(operation, lines);
                        }
                        break;

                    case "POP":
                        if (operation.Operands.Count == 0)
                            RemoveOperation(operation, lines);
                        break;

                    case "JUMP":
                        if (operation.Halt || operation.Uses.Count > 0 || operation.PushLines.Length > 0 ||
                            operation.JumpTarget == null)
                        {
                            break;
                        }

                        if (IsNextAddress(lines, operation.InstructionLine, operation.JumpTarget, labels, lineAddresses))
                            RemoveOperation(operation, lines);

                        break;

                    case "JUMP_IF_FALSE":
                        if (IsNextAddress(lines, operation.InstructionLine, operation.JumpTarget, labels, lineAddresses))
                            RemoveOperation(operation, lines);

                        break;
                }
            }

            lines.RemoveAll(line => line.Removed);
        }

        private static bool IsNextAddress(
            List<AssemblyBuilder.Line> lines,
            int instructionLine,
            string target,
            Dictionary<string, uint> labels,
            uint[] lineAddresses)
        {
            if (string.IsNullOrEmpty(target) || !labels.TryGetValue(target, out uint targetAddress))
                return false;

            AssemblyBuilder.Line instruction = lines[instructionLine];
            uint fallthrough = lineAddresses[instructionLine] + (uint)instruction.Size;

            return fallthrough == targetAddress;
        }

        private static uint[] ComputeLineAddresses(List<AssemblyBuilder.Line> lines)
        {
            uint[] addresses = new uint[lines.Count];
            uint address = 0;

            for (int i = 0; i < lines.Count; i++)
            {
                addresses[i] = address;

                AssemblyBuilder.Line line = lines[i];

                if (!line.Removed && line.Kind == AssemblyBuilder.LineKind.Instruction)
                    address += (uint)line.Size;
            }

            return addresses;
        }

        private static Dictionary<string, uint> ComputeLabelAddresses(List<AssemblyBuilder.Line> lines)
        {
            Dictionary<string, uint> labels = new Dictionary<string, uint>(StringComparer.Ordinal);
            uint address = 0;

            foreach (AssemblyBuilder.Line line in lines)
            {
                if (line.Removed)
                    continue;

                if (line.Kind == AssemblyBuilder.LineKind.Label)
                    labels[line.Label] = address;
                else if (line.Kind == AssemblyBuilder.LineKind.Instruction)
                    address += (uint)line.Size;
            }

            return labels;
        }

        private static void RemoveUnreachableCode(List<AssemblyBuilder.Line> lines)
        {
            bool reachable = true;

            foreach (AssemblyBuilder.Line line in lines)
            {
                if (line.Removed)
                    continue;

                if (line.Kind == AssemblyBuilder.LineKind.Label)
                {
                    // Every label is treated as an entry, so code reachable by an indirect or
                    // computed jump is never removed.
                    reachable = true;
                    continue;
                }

                if (line.Kind != AssemblyBuilder.LineKind.Instruction)
                    continue;

                if (!reachable)
                {
                    line.Removed = true;
                    continue;
                }

                if (line.Op == "JUMP" || line.Op == "JUMP_INDIRECT")
                    reachable = false;
            }

            lines.RemoveAll(line => line.Removed);
        }

        private static void ThreadJumps(List<AssemblyBuilder.Line> lines)
        {
            Dictionary<string, int> labelLine = new Dictionary<string, int>(StringComparer.Ordinal);

            for (int i = 0; i < lines.Count; i++)
            {
                AssemblyBuilder.Line line = lines[i];

                if (line.Removed)
                    continue;

                if (line.Kind == AssemblyBuilder.LineKind.Label)
                    labelLine[line.Label] = i;
            }

            string Resolve(string label)
            {
                HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
                string current = label;

                while (current != null && visited.Add(current))
                {
                    if (!labelLine.TryGetValue(current, out int index))
                        break;

                    int next = NextInstructionLine(lines, index + 1);

                    if (next < 0 || lines[next].Op != "JUMP" || lines[next].Halt ||
                        string.IsNullOrEmpty(lines[next].JumpTarget))
                    {
                        break;
                    }

                    // A bare jump has no operand pushes. If a push immediately precedes it, the
                    // trampoline carries a value and is not interchangeable with its target.
                    int previous = PreviousInstructionLine(lines, next - 1);

                    if (previous >= 0 && lines[previous].Op == "PUSH")
                        break;

                    current = lines[next].JumpTarget;
                }

                return current;
            }

            ParseResult parsed = ParseOperations(lines);

            if (!parsed.Success)
                return;

            foreach (Operation operation in parsed.Operations)
            {
                if ((operation.Opcode != "JUMP" && operation.Opcode != "JUMP_IF_FALSE") ||
                    operation.Halt || operation.JumpTarget == null)
                {
                    continue;
                }

                string resolved = Resolve(operation.JumpTarget);

                if (resolved != null && !string.Equals(resolved, operation.JumpTarget, StringComparison.Ordinal))
                {
                    operation.JumpTarget = resolved;
                    lines[operation.InstructionLine].JumpTarget = resolved;
                }
            }
        }

        private static int NextInstructionLine(List<AssemblyBuilder.Line> lines, int start)
        {
            for (int i = start; i < lines.Count; i++)
            {
                AssemblyBuilder.Line line = lines[i];

                if (line.Removed || line.Kind == AssemblyBuilder.LineKind.Comment || line.Kind == AssemblyBuilder.LineKind.Export)
                    continue;

                if (line.Kind == AssemblyBuilder.LineKind.Label)
                    continue;

                return i;
            }

            return -1;
        }

        private static int PreviousInstructionLine(List<AssemblyBuilder.Line> lines, int start)
        {
            for (int i = start; i >= 0; i--)
            {
                AssemblyBuilder.Line line = lines[i];

                if (line.Removed || line.Kind == AssemblyBuilder.LineKind.Comment || line.Kind == AssemblyBuilder.LineKind.Export)
                    continue;

                if (line.Kind == AssemblyBuilder.LineKind.Label)
                    continue;

                return i;
            }

            return -1;
        }

        #endregion

        #region Copies

        /// <summary>
        /// Replaces chains of copies that end at a value which is never written (a constant, a field
        /// that this program only reads, a parameter or <c>this</c>).
        /// </summary>
        private static void PropagateReadOnlyCopies(AssemblyBuilder assembly)
        {
            List<AssemblyBuilder.Line> lines = assembly.Lines;
            ParseResult parsed = ParseOperations(lines);

            if (!parsed.Success)
                return;

            Dictionary<string, Slot> slots = new Dictionary<string, Slot>(StringComparer.Ordinal);

            foreach (Slot slot in assembly.MutableSlots)
                slots[slot.Name] = slot;

            List<Operation> operations = parsed.Operations;
            Dictionary<string, int> defCount = CountDefinitions(operations);
            Dictionary<string, string> copySource = new Dictionary<string, string>(StringComparer.Ordinal);
            Dictionary<string, Operation> copyOperation = new Dictionary<string, Operation>(StringComparer.Ordinal);

            foreach (Operation operation in operations)
            {
                if (operation.Removed || operation.Opcode != "COPY" || operation.Def == null || operation.Operands.Count != 1)
                    continue;

                string destination = operation.Def;

                // Only compiler generated locals and temporaries may be bypassed. Rewriting a public
                // variable, field, parameter or constant would change observable state.
                if (!slots.TryGetValue(destination, out Slot destinationSlot) || !IsCandidate(destinationSlot))
                    continue;

                string source = operation.Operands[0];

                // The copy must be the only definition of the destination.
                if (defCount.TryGetValue(destination, out int count) && count == 1)
                {
                    copySource[destination] = source;
                    copyOperation[destination] = operation;
                }
            }

            Dictionary<string, string> replacement = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (string destination in copySource.Keys)
            {
                if (replacement.ContainsKey(destination))
                    continue;

                List<string> chain = new List<string>();
                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                string current = destination;
                bool resolved = false;

                while (current != null && seen.Add(current))
                {
                    if (!copySource.TryGetValue(current, out string next))
                    {
                        // The chain ended at a slot that this program never assigns. Fields are
                        // excluded because another behaviour can change them through the public
                        // variable table, so copying one into a temporary is observable.
                        bool stable = slots.TryGetValue(current, out Slot sourceSlot) &&
                                      (sourceSlot.IsConstant || sourceSlot.IsParameter || sourceSlot.InitialIsThis);

                        resolved = stable && (!defCount.ContainsKey(current) || defCount[current] == 0);
                        break;
                    }

                    chain.Add(current);
                    current = next;
                }

                if (!resolved || current == null || chain.Count == 0)
                    continue;

                foreach (string slot in chain)
                    replacement[slot] = current;
            }

            if (replacement.Count == 0)
                return;

            foreach (Operation operation in operations)
            {
                if (operation.Removed)
                    continue;

                for (int i = 0; i < operation.Operands.Count; i++)
                    operation.Operands[i] = Replace(operation.Operands[i], replacement);

                for (int i = 0; i < operation.Uses.Count; i++)
                    operation.Uses[i] = Replace(operation.Uses[i], replacement);

                if (operation.Def != null)
                    operation.Def = Replace(operation.Def, replacement);

                for (int i = 0; i < operation.ByRefDefs.Count; i++)
                    operation.ByRefDefs[i] = Replace(operation.ByRefDefs[i], replacement);
            }

            foreach (KeyValuePair<string, Operation> pair in copyOperation)
            {
                if (replacement.ContainsKey(pair.Key))
                    RemoveOperation(pair.Value, lines);
            }

            // Rewrite the actual operands from the replacement table.
            for (int i = 0; i < lines.Count; i++)
            {
                AssemblyBuilder.Line line = lines[i];

                if (line.Removed || line.Kind != AssemblyBuilder.LineKind.Instruction)
                    continue;

                if ((line.Op == "PUSH" || line.Op == "JUMP_INDIRECT") &&
                    line.Operand != null &&
                    replacement.TryGetValue(line.Operand, out string mapped))
                {
                    line.Operand = mapped;
                }
            }

            lines.RemoveAll(line => line.Removed);
        }

        private static string Replace(string slot, Dictionary<string, string> replacement)
        {
            HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);

            while (slot != null && replacement.TryGetValue(slot, out string next) && visited.Add(slot))
                slot = next;

            return slot;
        }

        private static Dictionary<string, int> CountDefinitions(List<Operation> operations)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (Operation operation in operations)
            {
                if (operation.Removed)
                    continue;

                if (operation.Def != null)
                {
                    counts.TryGetValue(operation.Def, out int count);
                    counts[operation.Def] = count + 1;
                }

                foreach (string byRefDef in operation.ByRefDefs)
                {
                    counts.TryGetValue(byRefDef, out int count);
                    counts[byRefDef] = count + 1;
                }
            }

            return counts;
        }

        /// <summary>
        /// Drops copies whose destination is never read. Only compiler generated temporaries are
        /// removed, so field and public variable writes are never lost.
        /// </summary>
        private static void RemoveDeadCopies(AssemblyBuilder assembly)
        {
            List<AssemblyBuilder.Line> lines = assembly.Lines;
            ParseResult parsed = ParseOperations(lines);

            if (!parsed.Success)
                return;

            List<Operation> operations = parsed.Operations;
            Dictionary<string, int> useCount = CountUses(operations);
            Dictionary<string, Slot> slots = new Dictionary<string, Slot>(StringComparer.Ordinal);

            foreach (Slot slot in assembly.MutableSlots)
                slots[slot.Name] = slot;

            foreach (Operation operation in operations)
            {
                if (operation.Removed || operation.Opcode != "COPY" || operation.Def == null)
                    continue;

                // A COPY into a slot that nothing reads is a dead store. Slots that are not compiler
                // temporaries are kept because they can be observed through the inspector.
                if (useCount.TryGetValue(operation.Def, out int count) && count > 0)
                    continue;

                if (!slots.TryGetValue(operation.Def, out Slot destination) || !IsCandidate(destination))
                    continue;

                RemoveOperation(operation, lines);
            }

            lines.RemoveAll(line => line.Removed);
        }

        private static Dictionary<string, int> CountUses(List<Operation> operations)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (Operation operation in operations)
            {
                if (operation.Removed)
                    continue;

                foreach (string use in operation.Uses)
                {
                    counts.TryGetValue(use, out int count);
                    counts[use] = count + 1;
                }
            }

            return counts;
        }

        #endregion

        #region Slot reuse

        private static void ReuseSlots(AssemblyBuilder assembly)
        {
            List<AssemblyBuilder.Line> lines = assembly.Lines;
            ParseResult parsed = ParseOperations(lines);

            if (!parsed.Success)
                return;

            List<Operation> operations = parsed.Operations;

            if (operations.Count == 0)
                return;

            Dictionary<string, Slot> slots = new Dictionary<string, Slot>(StringComparer.Ordinal);

            foreach (Slot slot in assembly.MutableSlots)
                slots[slot.Name] = slot;

            HashSet<string> candidates = new HashSet<string>(StringComparer.Ordinal);

            foreach (Slot slot in assembly.MutableSlots)
            {
                if (IsCandidate(slot))
                    candidates.Add(slot.Name);
            }

            Dictionary<string, HashSet<string>> interference = BuildInterference(lines, operations, candidates);

            if (interference == null)
                return;

            Dictionary<string, string> groups = CoalesceCopies(operations, candidates, interference, slots);
            Dictionary<string, Slot> colors = ColorGroups(groups, interference, slots);
            Dictionary<string, string> repName = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (KeyValuePair<string, string> pair in groups)
            {
                string root = Find(pair.Value, groups);

                if (!repName.ContainsKey(pair.Key))
                    repName[pair.Key] = colors[root].Name;
            }

            if (repName.Count == 0)
                return;

            for (int i = 0; i < lines.Count; i++)
            {
                AssemblyBuilder.Line line = lines[i];

                if (line.Removed || line.Kind != AssemblyBuilder.LineKind.Instruction)
                    continue;

                if ((line.Op == "PUSH" || line.Op == "JUMP_INDIRECT") &&
                    line.Operand != null &&
                    repName.TryGetValue(line.Operand, out string mapped))
                {
                    line.Operand = mapped;
                }
            }

            // Removing coalesced copies may expose new self copies.
            ParseResult rewritten = ParseOperations(lines);

            if (!rewritten.Success)
                return;

            foreach (Operation operation in rewritten.Operations)
            {
                if (operation.Opcode == "COPY" &&
                    operation.Def != null &&
                    operation.Operands.Count == 1 &&
                    string.Equals(operation.Operands[0], operation.Def, StringComparison.Ordinal))
                {
                    RemoveOperation(operation, lines);
                }
            }

            lines.RemoveAll(line => line.Removed);

            HashSet<string> referenced = CollectReferencedSlots(lines);

            assembly.MutableSlots.RemoveAll(slot =>
                IsCandidate(slot) && !referenced.Contains(slot.Name));
        }

        private static bool IsCandidate(Slot slot)
        {
            return slot != null &&
                   slot.Reusable &&
                   !slot.Exported &&
                   slot.SyncMode == null &&
                   slot.InitialValue == null &&
                   !slot.InitialIsThis &&
                   string.IsNullOrEmpty(slot.AddressLabel);
        }

        private static Dictionary<string, HashSet<string>> BuildInterference(
            List<AssemblyBuilder.Line> lines,
            List<Operation> operations,
            HashSet<string> candidates)
        {
            List<Block> blocks = BuildBlocks(lines, operations);
            ComputeLiveness(blocks, operations);

            Dictionary<string, HashSet<string>> interference =
                new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            // Interference is quadratic in the number of simultaneously live values. A pathological
            // program should lose the optional slot reuse, not lock up the editor.
            const long MaxEdges = 1000000;
            long edgeCount = 0;
            bool tooComplex = false;

            void AddEdge(string a, string b)
            {
                if (tooComplex || a == null || b == null || a == b)
                    return;

                if (!candidates.Contains(a) || !candidates.Contains(b))
                    return;

                if (!interference.TryGetValue(a, out HashSet<string> set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    interference[a] = set;
                }

                if (!set.Add(b))
                    return;

                edgeCount++;

                if (!interference.TryGetValue(b, out HashSet<string> reverse))
                {
                    reverse = new HashSet<string>(StringComparer.Ordinal);
                    interference[b] = reverse;
                }

                reverse.Add(a);

                if (edgeCount > MaxEdges)
                    tooComplex = true;
            }

            foreach (Block block in blocks)
            {
                if (tooComplex)
                    break;

                HashSet<string> live = new HashSet<string>(block.LiveOut, StringComparer.Ordinal);

                for (int i = block.Operations.Count - 1; i >= 0; i--)
                {
                    Operation operation = block.Operations[i];

                    List<string> point = new List<string>(live);

                    foreach (string use in operation.Uses)
                    {
                        if (!point.Contains(use))
                            point.Add(use);
                    }

                    if (operation.Def != null && !point.Contains(operation.Def))
                        point.Add(operation.Def);

                    foreach (string byRefDef in operation.ByRefDefs)
                    {
                        if (!point.Contains(byRefDef))
                            point.Add(byRefDef);
                    }

                    string copySource = operation.Opcode == "COPY" && operation.Operands.Count == 1
                        ? operation.Operands[0]
                        : null;

                    for (int a = 0; a < point.Count; a++)
                    {
                        for (int b = a + 1; b < point.Count; b++)
                        {
                            // A copy source and destination may share a slot: after the copy they
                            // hold the same value, and later definitions still create an edge.
                            if (copySource != null && operation.Def != null &&
                                ((point[a] == copySource && point[b] == operation.Def) ||
                                 (point[b] == copySource && point[a] == operation.Def)))
                            {
                                continue;
                            }

                            AddEdge(point[a], point[b]);
                        }
                    }

                    if (operation.Def != null)
                        live.Remove(operation.Def);

                    foreach (string byRefDef in operation.ByRefDefs)
                        live.Remove(byRefDef);

                    foreach (string use in operation.Uses)
                        live.Add(use);
                }
            }

            if (tooComplex)
                return null;

            return interference;
        }

        private static List<Block> BuildBlocks(List<AssemblyBuilder.Line> lines, List<Operation> operations)
        {
            List<Block> blocks = new List<Block>();
            Dictionary<int, Operation> byLine = new Dictionary<int, Operation>();
            Dictionary<string, int> labelBlock = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (Operation operation in operations)
                byLine[operation.InstructionLine] = operation;

            Block current = null;
            bool forceNew = true;

            for (int i = 0; i < lines.Count; i++)
            {
                AssemblyBuilder.Line line = lines[i];

                if (line.Removed)
                    continue;

                if (line.Kind == AssemblyBuilder.LineKind.Label)
                {
                    if (current == null || current.Operations.Count > 0)
                    {
                        current = new Block { Index = blocks.Count };
                        blocks.Add(current);
                    }

                    current.Labels.Add(line.Label);
                    labelBlock[line.Label] = current.Index;
                    forceNew = false;
                    continue;
                }

                if (line.Kind != AssemblyBuilder.LineKind.Instruction)
                    continue;

                if (current == null || (forceNew && current.Operations.Count > 0))
                {
                    current = new Block { Index = blocks.Count };
                    blocks.Add(current);
                }

                forceNew = false;

                if (byLine.TryGetValue(i, out Operation operation))
                    current.Operations.Add(operation);

                if (line.Op == "JUMP" || line.Op == "JUMP_IF_FALSE" || line.Op == "JUMP_INDIRECT")
                    forceNew = true;
            }

            foreach (Block block in blocks)
            {
                if (block.Operations.Count == 0)
                {
                    // A label without instructions falls through to the next block.
                    if (block.Index + 1 < blocks.Count)
                        block.Successors.Add(block.Index + 1);

                    continue;
                }

                Operation last = block.Operations[block.Operations.Count - 1];

                if (last.Opcode == "JUMP")
                {
                    if (!last.Halt && last.JumpTarget != null && labelBlock.TryGetValue(last.JumpTarget, out int target))
                        block.Successors.Add(target);
                }
                else if (last.Opcode == "JUMP_IF_FALSE")
                {
                    if (last.JumpTarget != null && labelBlock.TryGetValue(last.JumpTarget, out int target) && !block.Successors.Contains(target))
                        block.Successors.Add(target);

                    if (block.Index + 1 < blocks.Count && !block.Successors.Contains(block.Index + 1))
                        block.Successors.Add(block.Index + 1);
                }
                else if (last.Opcode == "JUMP_INDIRECT")
                {
                    // The return address of a recursive call is not visible to a static analysis.
                    // Treat every heap value as live across the indirect jump.
                    block.DynamicExit = true;
                }
                else if (block.Index + 1 < blocks.Count)
                {
                    block.Successors.Add(block.Index + 1);
                }
            }

            return blocks;
        }

        private static void ComputeLiveness(List<Block> blocks, List<Operation> operations)
        {
            HashSet<string> allSlots = new HashSet<string>(StringComparer.Ordinal);

            foreach (Operation operation in operations)
            {
                if (operation.Removed)
                    continue;

                foreach (string use in operation.Uses)
                    allSlots.Add(use);

                if (operation.Def != null)
                    allSlots.Add(operation.Def);

                foreach (string byRefDef in operation.ByRefDefs)
                    allSlots.Add(byRefDef);
            }

            foreach (Block block in blocks)
            {
                foreach (Operation operation in block.Operations)
                {
                    foreach (string use in operation.Uses)
                    {
                        if (!block.Def.Contains(use))
                            block.Use.Add(use);
                    }

                    if (operation.Def != null)
                        block.Def.Add(operation.Def);

                    foreach (string byRefDef in operation.ByRefDefs)
                        block.Def.Add(byRefDef);
                }
            }

            bool changed = true;

            while (changed)
            {
                changed = false;

                foreach (Block block in blocks)
                {
                    HashSet<string> liveOut = new HashSet<string>(StringComparer.Ordinal);

                    foreach (int successor in block.Successors)
                        liveOut.UnionWith(blocks[successor].LiveIn);

                    if (block.DynamicExit)
                        liveOut.UnionWith(allSlots);

                    HashSet<string> liveIn = new HashSet<string>(block.Use, StringComparer.Ordinal);

                    foreach (string value in liveOut)
                    {
                        if (!block.Def.Contains(value))
                            liveIn.Add(value);
                    }

                    if (!SetsEqual(liveOut, block.LiveOut) || !SetsEqual(liveIn, block.LiveIn))
                    {
                        ReplaceSet(block.LiveOut, liveOut);
                        ReplaceSet(block.LiveIn, liveIn);
                        changed = true;
                    }
                }
            }
        }

        private static bool SetsEqual(HashSet<string> a, HashSet<string> b)
        {
            return a.Count == b.Count && a.SetEquals(b);
        }

        private static void ReplaceSet(HashSet<string> target, HashSet<string> source)
        {
            target.Clear();
            target.UnionWith(source);
        }

        private static Dictionary<string, string> CoalesceCopies(
            List<Operation> operations,
            HashSet<string> candidates,
            Dictionary<string, HashSet<string>> interference,
            Dictionary<string, Slot> slots)
        {
            Dictionary<string, string> parent = new Dictionary<string, string>(StringComparer.Ordinal);
            Dictionary<string, HashSet<string>> conflicts = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            foreach (string candidate in candidates)
            {
                parent[candidate] = candidate;
                conflicts[candidate] = new HashSet<string>(StringComparer.Ordinal);
            }

            foreach (KeyValuePair<string, HashSet<string>> pair in interference)
            {
                foreach (string neighbor in pair.Value)
                {
                    if (candidates.Contains(pair.Key) && candidates.Contains(neighbor))
                        conflicts[pair.Key].Add(neighbor);
                }
            }

            string FindRoot(string slot)
            {
                while (parent[slot] != slot)
                {
                    parent[slot] = parent[parent[slot]];
                    slot = parent[slot];
                }

                return slot;
            }

            foreach (Operation operation in operations)
            {
                if (operation.Opcode != "COPY" || operation.Def == null || operation.Operands.Count != 1)
                    continue;

                string source = operation.Operands[0];
                string destination = operation.Def;

                if (!candidates.Contains(source) || !candidates.Contains(destination))
                    continue;

                if (!slots.TryGetValue(source, out Slot sourceSlot) ||
                    !slots.TryGetValue(destination, out Slot destinationSlot))
                {
                    continue;
                }

                if (!string.Equals(sourceSlot.UdonType, destinationSlot.UdonType, StringComparison.Ordinal))
                    continue;

                string a = FindRoot(source);
                string b = FindRoot(destination);

                if (a == b || conflicts[a].Contains(b))
                    continue;

                if (string.CompareOrdinal(a, b) > 0)
                {
                    string swap = a;
                    a = b;
                    b = swap;
                }

                parent[b] = a;

                HashSet<string> merged = new HashSet<string>(conflicts[a], StringComparer.Ordinal);
                merged.UnionWith(conflicts[b]);
                merged.Remove(a);
                merged.Remove(b);
                conflicts[a] = merged;

                foreach (string other in merged)
                {
                    if (conflicts.TryGetValue(other, out HashSet<string> set))
                    {
                        set.Remove(b);
                        set.Add(a);
                    }
                }

                conflicts.Remove(b);
            }

            return parent;
        }

        private static Dictionary<string, Slot> ColorGroups(
            Dictionary<string, string> groups,
            Dictionary<string, HashSet<string>> interference,
            Dictionary<string, Slot> slots)
        {
            Dictionary<string, List<string>> members = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (string slot in groups.Keys)
            {
                string root = Find(slot, groups);

                if (!members.TryGetValue(root, out List<string> list))
                {
                    list = new List<string>();
                    members[root] = list;
                }

                list.Add(slot);
            }

            Dictionary<string, HashSet<string>> groupConflicts = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            foreach (string root in members.Keys)
                groupConflicts[root] = new HashSet<string>(StringComparer.Ordinal);

            foreach (KeyValuePair<string, HashSet<string>> pair in interference)
            {
                if (!groups.ContainsKey(pair.Key))
                    continue;

                string a = Find(pair.Key, groups);

                foreach (string neighbor in pair.Value)
                {
                    if (!groups.ContainsKey(neighbor))
                        continue;

                    string b = Find(neighbor, groups);

                    if (a != b)
                    {
                        groupConflicts[a].Add(b);
                        groupConflicts[b].Add(a);
                    }
                }
            }

            List<string> ordered = members.Keys
                .OrderByDescending(root => groupConflicts[root].Count)
                .ThenBy(root => root, StringComparer.Ordinal)
                .ToList();

            Dictionary<string, Slot> colors = new Dictionary<string, Slot>(StringComparer.Ordinal);
            Dictionary<string, List<Slot>> byType = new Dictionary<string, List<Slot>>(StringComparer.Ordinal);

            foreach (string root in ordered)
            {
                List<string> groupMembers = members[root];
                string type = slots[groupMembers[0]].UdonType;
                HashSet<string> neighborColors = new HashSet<string>(StringComparer.Ordinal);

                foreach (string neighbor in groupConflicts[root])
                {
                    if (colors.TryGetValue(neighbor, out Slot color))
                        neighborColors.Add(color.Name);
                }

                Slot chosen = null;

                if (byType.TryGetValue(type, out List<Slot> existing))
                {
                    foreach (Slot candidate in existing)
                    {
                        if (!neighborColors.Contains(candidate.Name))
                        {
                            chosen = candidate;
                            break;
                        }
                    }
                }
                else
                {
                    existing = new List<Slot>();
                    byType[type] = existing;
                }

                if (chosen == null)
                {
                    chosen = slots[groupMembers[0]];
                    existing.Add(chosen);
                }

                colors[root] = chosen;
            }

            return colors;
        }

        private static string Find(string slot, Dictionary<string, string> groups)
        {
            while (groups.TryGetValue(slot, out string next) && !string.Equals(next, slot, StringComparison.Ordinal))
                slot = next;

            return slot;
        }

        #endregion

        #region Dead slots

        private static HashSet<string> CollectReferencedSlots(List<AssemblyBuilder.Line> lines)
        {
            HashSet<string> referenced = new HashSet<string>(StringComparer.Ordinal);

            foreach (AssemblyBuilder.Line line in lines)
            {
                if (line.Removed || line.Kind != AssemblyBuilder.LineKind.Instruction)
                    continue;

                if ((line.Op == "PUSH" || line.Op == "JUMP_INDIRECT") && line.Operand != null)
                    referenced.Add(line.Operand);
            }

            return referenced;
        }

        private static void RemoveUnusedSlots(AssemblyBuilder assembly)
        {
            HashSet<string> referenced = CollectReferencedSlots(assembly.Lines);

            assembly.MutableSlots.RemoveAll(slot =>
                slot.Reusable &&
                !slot.Exported &&
                slot.SyncMode == null &&
                !referenced.Contains(slot.Name) &&
                string.IsNullOrEmpty(slot.AddressLabel) &&
                slot.InitialValue == null &&
                !slot.InitialIsThis);
        }

        #endregion
    }
}
