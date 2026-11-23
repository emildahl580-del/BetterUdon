# BetterUdon

### Write C#. Run Udon.

BetterUdon is a C#-to-Udon compiler for VRChat worlds. It targets the stock Udon VM, uses VRChat's
own assembler, and adds none of its own runtime code. You write the C# you already know; BetterUdon
turns it into ordinary Udon assembly and the world runs it exactly like any other Udon program.

The interesting part is what you no longer have to work around. UdonSharp is a deliberately small
language: no user-defined value types, no collections, no generics, no recursion, no delegates, no
iterators, and no synchronous cross-behaviour calls. BetterUdon compiles a much larger slice of C#.

## At a glance

- **Structs with real value semantics**: constructors, fields, mutating methods, nesting, arrays and
  member-wise equality.
- **Collections**: `List<T>`, `Dictionary<K,V>`, `HashSet<T>`, `Queue<T>`, `Stack<T>`, `KeyValuePair`,
  `Keys`/`Values`, `foreach`, capacity and `TrimExcess`.
- **Generics**: generic methods and generic structs, monomorphised per instantiation, including
  `default(T)`, `new T()`, `typeof(T)`, `T[]` and `ref`/`out T`.
- **Recursion**: direct and mutual recursion through `[RecursiveMethod]`, with arguments, return values
  and a growable runtime frame stack.
- **C# call semantics**: `ref`, `out`, `in`, `out var`, named and optional arguments, `params`, local
  functions with captures, and implicit numeric conversions such as passing `int` where a `float` is
  expected.
- **Unity-style fields**: public fields and private `[SerializeField]` fields both appear in the normal
  inspector and become Udon public variables; you do not need to make a reference public just to use it
  from the program.
- **Properties**: auto properties, private setters, expression-bodied properties and properties with
  explicit getter/setter bodies.
- **Delegates and events**: `Action`, `Action<T>`, `Func<..., TResult>`, method groups, capturing
  lambdas, `+=`/`-=`, multicast events and cross-behaviour targets. Lambdas work as constructor and
  method arguments as well as assignments.
- **Iterators**: `yield return` and `yield break`, lowered to a resumable state machine.
- **Interfaces and inheritance**: virtual, override, abstract and interface calls for methods,
  properties and indexers, resolved against the project's closed world of behaviour types.
- **Pattern matching**: `is` type/declaration/property patterns, switch expressions and relational
  patterns.
- **Cross-behaviour C#**: synchronous methods, properties, indexers and fields on another BetterUdon
  behaviour, with arguments, return values and private state behaving like normal C#.
- **Several behaviours on one object**, including two components of the same type.
- **Networking**: synced structs and struct arrays, interpolation, sync modes, `OnChange` callbacks
  and parameterised `[NetworkCallable]` events.
- **Editor tooling**: generated assembly and disassembly in the inspector, a compile button and a
  green-to-red complexity bar.

## A small example

A backpack is a good test: it needs a value type, two collections, an event, a pattern match and a
synced value, without any boilerplate for Udon's heap.

```csharp
using BetterUdon;
using System;
using System.Collections.Generic;

public struct Item
{
    public string name;
    public int value;

    public Item(string name, int value)
    {
        this.name = name;
        this.value = value;
    }

    public void Upgrade(int amount)
    {
        value += amount;
    }
}

public class Backpack : BetterUdonBehaviour
{
    [BetterUdonSynced] public int coins;

    public List<Item> items;
    public Dictionary<string, int> counts;

    public event Action<Item> ItemAdded;

    private void Start()
    {
        items = new List<Item>();
        counts = new Dictionary<string, int>();

        Add(new Item("Sword", 10));
        Add(new Item("Potion", 3));
    }

    public void Add(Item item)
    {
        items.Add(item);
        counts[item.name] = counts.ContainsKey(item.name) ? counts[item.name] + 1 : 1;
        ItemAdded(item);
    }

    public Item Best()
    {
        Item best = items[0];

        foreach (Item item in items)
        {
            if (item.value > best.value)
                best = item;
        }

        return best;
    }

    public void SellBest()
    {
        Item item = Best();
        coins += item.value;
        items.Remove(item);
        RequestSerialization();
    }

    public string Rarity(Item item) => item.value switch
    {
        > 50 => "rare",
        > 10 => "uncommon",
        _ => "common",
    };
}
```

The details that matter:

- `Item` is a struct, so `best`, `item` and `bag[0]` are independent values. Copying one never aliases
  another.
- `List<Item>` and `Dictionary<string, int>` work without boxing wrappers or manual array bookkeeping.
- `ItemAdded(item)` is a multicast event. With one subscriber, ten, or none, it behaves like C#.
- `Rarity` uses a switch expression and relational patterns.
- `coins` is a synced public field. `RequestSerialization()` is the only network call in the example.
- `Start` creates the collections because C# field initialisers must be compile-time constants on Udon.

## Install

BetterUdon is a Unity package. `main` is the 2.x line; use a pinned tag if you want a fixed point.

1. Open **Window > Package Manager**.
2. Press **+** → **Add package from git URL…**
3. Paste one of these:

   Main line:

   ```
   https://github.com/emildahl580-del/BetterUdon.git
   ```

   Pinned 2.3 release:

   ```
   https://github.com/emildahl580-del/BetterUdon.git#v2.3.0
   ```

   Development branch:

   ```
   https://github.com/emildahl580-del/BetterUdon.git#feature-expansion
   ```

Requires **VRChat SDK – Worlds 3.5 or newer**. The package declares `com.vrchat.worlds` as a
`vpmDependencies` entry, so VCC and Unity's package manager understand the requirement.

### Other installs

- **Local clone** — `git clone https://github.com/emildahl580-del/BetterUdon.git Packages/com.betterudon`.
- **Zip** — take `BetterUdon-2.3.0.zip` from the
  [releases page](https://github.com/emildahl580-del/BetterUdon/releases) and extract it into `Packages/`.
- **Copy** — copy the package folder into your project's `Packages/` directory.

Package Manager keeps the checkout under `Library/PackageCache`. Use **Update** in the package's
context menu, or change the tag in `Packages/manifest.json` to move between releases.

## Getting started

1. Add a component deriving from `BetterUdon.BetterUdonBehaviour` to a GameObject.
2. Write normal C#. Public fields become Udon public variables.
3. Press **Recompile** in the component Inspector, or run
   `Tools > BetterUdon > Compile All Programs`.
4. Press Play. ClientSim runs the generated program exactly like a normal Udon behaviour.

The **Program** box shows the generated assembly and disassembly. It also contains a complexity bar
that fills from green to red as the program approaches BetterUdon's own budgets for heap slots,
instruction words and generic instantiations. Those are guidance values, not hard Udon limits.

`Tools > BetterUdon > Run Diagnostics` compiles every behaviour and writes the generated assembly,
diagnostics, entry points and heap values to `Library/BetterUdonReport.txt`.

## What UdonSharp cannot do

This is the practical difference, not a marketing table:

| UdonSharp restriction | BetterUdon |
| --- | --- |
| No user-defined structs | Structs with constructors, mutating methods, nesting and value semantics |
| No collections | `List<T>`, `Dictionary<K,V>`, `HashSet<T>`, `Queue<T>`, `Stack<T>` |
| No generics | Generic methods and generic structs, monomorphised |
| No recursion | Direct and mutual recursion through `[RecursiveMethod]` |
| No `ref` / `out` | `ref`, `out`, `in`, `out var`, copy-in/copy-back |
| No local functions | Local functions with captures |
| No delegates or events | Method groups, lambdas, capturing closures, multicast events |
| No iterators | `yield return` / `yield break` for direct `foreach` |
| No interface or virtual dispatch | Closed-world dispatch for methods, properties and indexers |
| No cross-behaviour methods | Synchronous inlined calls with arguments and return values |
| No multiple behaviours per object | Several behaviours, including duplicates of the same type |
| No pattern matching | `is` patterns, property patterns, switch expressions |
| No `?.` / `??=` | Supported |
| No `typeof` | Supported, including generic `typeof(T)` |

## How it works

BetterUdon asks **Roslyn** for the semantic model of the project's actual C# sources. It then lowers
each behaviour into a Udon program:

1. **Front end** — parse every `.cs` file under `Assets/`, resolve real C# symbols, overloads and
   conversions.
2. **Lowering** — turn expressions, statements, structs, collections, generics, calls and events into
   heap slots and labels. Normal calls are inlined; `[RecursiveMethod]` methods become callable blocks.
3. **Emission** — build Udon assembly, validate every extern signature against the installed SDK, and
   assemble it with VRChat's toolchain.
4. **Storage** — serialize the resulting `UdonProgram` into a normal Udon program asset.

Nothing custom runs in the world. At runtime it is stock Udon: the same VM, heap and externs as any
other world.

### Output optimization

The compiled program is optimized by default. The behaviour inspector's **Program** box has four
independent tick boxes, and changing one recompiles the program immediately so the complexity bar shows
the effect:

| Toggle | What it removes |
| --- | --- |
| Remove redundant instructions | NOPs, self copies, push/pop pairs and jumps to the next instruction |
| Remove unreachable code | Code after an unconditional jump or halt, plus unused compiler temporaries |
| Propagate copies | Copies from constants, event arguments and `this` into temporaries, and dead copies |
| Reuse heap slots | Compiler temporaries whose live ranges do not overlap |

**Recommended** enables all four; **None** leaves the raw lowering untouched. The optimizer never
removes or shares an exported field, a synced variable, a constant or an event argument, so the
inspector's public variables and the network layout keep working. It only rewrites compiler-generated
locals and temporaries.

Slot reuse uses a control-flow and liveness analysis. Two slots are only shared when their live ranges
do not overlap and their Udon storage type is identical. Programs that carry values across a recursive
call's evaluation stack, or whose extern metadata is unavailable, skip the lifetime rewrites instead
of guessing.

## Feature notes

### Structs and value semantics

A struct is stored as an `object[]` of its fields. Every operation that C# would copy does an explicit
deep copy: assignment, argument passing, returning, reading an array element, writing a struct field.
Lvalue chains skip the copy until the final write, so `stats.weapon.itemId = 42` still writes through.

### Collections

`List<T>` is a two-element heap object: `[T[] items, int count]`. `Queue<T>`, `Stack<T>` and
`HashSet<T>` use the same shape. `Dictionary<K,V>` is `[K[] keys, V[] values, int count]`.

Dictionary and set lookup use member-wise equality with a linear scan. That is intentionally simple:
it keeps primitives, strings and user structs correct without a runtime service. Hash-table speed is
traded for predictability and zero extra runtime code.

`Dictionary.Keys` and `Dictionary.Values` are stored collection values supporting `Count`,
`Contains`, `ToArray`, `CopyTo` and `foreach`.

Delegates are valid collection values. `List<Action>`, `Dictionary<string, Action>`, `Queue<Action>`,
`Stack<Action>` and `HashSet<Action>` work with method groups and lambdas directly:

```csharp
List<Action> callbacks = new List<Action>();
callbacks.Add(OnOpen);
callbacks.Add(() => count++);
callbacks[0]();

Dictionary<string, Action> map = new Dictionary<string, Action>();
map["open"] = OnOpen;
map["open"]();

Action[] array = callbacks.ToArray();
```

Delegate equality compares the invocation list (method, target and closure), so `Contains`, `Remove`,
`HashSet` membership and delegate dictionary keys behave like C#. Delegates are stored as the same
compiler invocation arrays used for delegate locals and fields; they are not inspector-serialisable, so
create and populate collections in `Start`.

### Generics

Generic methods and generic structs are monomorphised. `Box<int>` and `Box<string>` become separate
struct layouts; `Identity<int>(7)` and `Identity<string>("x")` become separate inlined bodies. There
is no runtime generic construction — every instantiation must be visible at build time.

### Recursion

Unmarked recursion is a compile error. Mark a method with `[RecursiveMethod]` and it is compiled once
as a labelled block. Calls push a return address and jump; re-entry saves the caller's live slots on a
growable heap stack. Direct and mutual recursion work. Recursive methods cannot take `ref`/`out`/`in`
parameters, be called on another behaviour, or be combined with `[NetworkCallable]`.

### Delegates and events

A delegate stores an invocation array of `(target, method ID, closure)` entries. `+=` and `-=` add and
remove entries; invocation loops the entries and inlines each target synchronously. Method groups,
capturing lambdas, multicast events and cross-behaviour targets are supported. A captured variable is
snapshotted at delegate creation and written back after invocation, rather than being a shared alias.

### Iterators

An iterator used directly by `foreach` is compiled into the caller as a resumable block. A heap state
slot records the last `yield`; locals live on the heap. Nested loops, arguments and `yield break` are
supported. Manual `IEnumerator` objects and async/coroutine scheduling are not.

### Interfaces, inheritance and dispatch

Every BetterUdon behaviour in the project is visible to the compiler. A virtual, override, abstract or
interface call switches on the receiver's hidden type marker and inlines the matching implementation.
This keeps the call synchronous and preserves arguments, return values and private fields. Properties
and indexers use the same mechanism. `is` and `as` use the same marker table.

`base.Method()` is statically bound and is not dispatched.

### Cross-behaviour calls

With a reference to another BetterUdon behaviour, fields, properties, indexers and methods behave like
C#. The callee body is inlined against the receiver, so calls are synchronous. `SendCustomEvent` remains
available when you actually want asynchronous dispatch.

`Instantiate` also understands behaviour references:

```csharp
[SerializeField] private BetterUdonCubeBehaviour cubePrefab;

public void Spawn()
{
    BetterUdonCubeBehaviour clone = Instantiate(cubePrefab);
    clone.Tint(Color.red);                    // ordinary cross-behaviour call

    GameObject cloneObject = Instantiate(cubePrefab.gameObject);
    BetterUdonCubeBehaviour lookup = cloneObject.GetComponent<BetterUdonCubeBehaviour>();

    Destroy(clone.gameObject);
}
```

`Instantiate(behaviour)` clones the backing GameObject and resolves the clone's own backing
`UdonBehaviour`, so the returned reference is usable immediately. `Instantiate(gameObject)` returns the
clone GameObject. The C# component on the clone is only the edit-time view; runtime values live in the
clone's Udon heap. Multi-argument `Instantiate(original, parent/position/rotation)` is not supported yet;
instantiate first, then set the transform. `Destroy(clone)` destroys only the component, so use
`Destroy(clone.gameObject)` to remove the spawned object.

### Networking

`[BetterUdonSynced]` marks synced fields. Structs and struct arrays are flattened into hidden syncable
mirrors automatically. Interpolation modes, `[BetterUdonBehaviourSyncMode]` and
`[BetterUdonSynced(OnChange = nameof(...))]` match UdonSharp's sync model. `SendCustomNetworkEvent`
supports parameterised `[NetworkCallable]` entry points.

Sync mode belongs to the GameObject, not the behaviour: several BetterUdon behaviours on one object
share one sync mode.

## UdonSharp compatibility

Everything UdonSharp can express is covered:

| UdonSharp feature | BetterUdon equivalent |
| --- | --- |
| `[UdonSynced]` + interpolation | `[BetterUdonSynced]` |
| `[FieldChangeCallback]` | `[BetterUdonSynced(OnChange = nameof(...))]` |
| `[UdonBehaviourSyncMode]` | `[BetterUdonBehaviourSyncMode]` |
| `SendCustomNetworkEvent` | Same, with `[NetworkCallable]` parameters |
| `[RecursiveMethod]` | Same, with both direct and mutual recursion |
| Udon events (`OnPlayerJoined`, …) | Same event names |
| `SendCustomEvent`, delayed variants, `Get`/`SetProgramVariable` | Same |
| Inheritance between behaviours | Same, with base fields and methods inlined |

## Limits

BetterUdon is a compiler, not a new runtime. It cannot add capabilities the VM does not have.

- No `try` / `catch` / `throw`: Udon halts a behaviour on an exception and does not unwind.
- No `async` / `await`, coroutine scheduling or manual `IEnumerator` objects.
- No runtime generics, generic classes or generic behaviour components.
- No `lock`, `unsafe`, threads or native code.
- `record struct` needs C# 10 and list patterns need C# 11; the project compiles as C# 9, so those
  syntaxes are unavailable.
- Captured closures snapshot values instead of sharing the original variable.
- The callable BCL/Unity surface is the set of externs exposed by the installed SDK, plus the
  collection and language intrinsics BetterUdon implements itself.
- Generic constraint calls are limited to `IComparable<T>.CompareTo` on numeric and user-struct types.
- Auto-property and collection fields are heap variables with non-serialisable backings; create
  collections in `Start` and treat inspector values as defaults.
- BetterUdon's compiler guard rejects programs over 20,000 heap slots or 256 generic instantiations.
  The inspector warning also includes a 50,000 instruction-word guidance budget. These are BetterUdon
  guardrails, not Udon limits.

## Documentation

- `Documentation~/index.md` — the compiler and runtime model, lowering strategies, language coverage,
  networking and limits.
- `Documentation~/aot-expansion.md` — the working checklist of what is implemented and what is blocked.

## License

MIT. See `LICENSE.md`.
