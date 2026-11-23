using System;

namespace BetterUdon.Editor
{
    /// <summary>
    /// Output optimizations the compiler may apply. They are all behaviour preserving: optimization
    /// only removes redundant instructions, unreachable code, redundant copies and unused heap
    /// temporaries, or shares temporaries whose lifetimes do not overlap.
    /// </summary>
    [Flags]
    public enum UdonOptimizationFlags
    {
        None = 0,

        /// <summary>Drops self copies, push/pop pairs, NOPs and jumps to the next instruction.</summary>
        Peephole = 1 << 0,

        /// <summary>Drops code after an unconditional jump or halt, and removes unused temporaries.</summary>
        DeadCode = 1 << 1,

        /// <summary>Replaces copies from read-only values and removes copies whose result is never read.</summary>
        CopyPropagation = 1 << 2,

        /// <summary>Shares compiler temporaries whose live ranges do not overlap.</summary>
        SlotReuse = 1 << 3,

        /// <summary>The recommended, behaviour preserving set.</summary>
        Recommended = Peephole | DeadCode | CopyPropagation | SlotReuse,
    }
}
