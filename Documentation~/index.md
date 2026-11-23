# BetterUdon — A C# to Udon Compiler for VRChat Worlds

**Version 2.0.0-alpha.1**

BetterUdon compiles C# into ordinary Udon assembly for VRChat worlds. It uses VRChat's assembler and
runs on the stock Udon VM. There is no BetterUdon runtime, no custom interpreter and no hidden scene
service: a BetterUdon world is a normal Udon world with a larger language in front of it.

This document explains the target runtime, how the compiler lowers C# to it, what the language
supports, and where the limits still are.

---

## 1. Why this compiler exists

UdonSharp is useful precisely because it is restricted. It binds a small C# syntax directly to Udon
and rejects anything the VM cannot express. The restriction is real in some places, but in many places
it is a limitation of the transpiler's representation:

- Udon's heap can hold arbitrary CLR object references, including arrays.
- Udon's `EXTERN` instruction can call anything registered by the SDK.
- Udon has `JUMP_INDIRECT`, so a software call/return convention is possible.
- Udon can express user data as arrays of heap values.

BetterUdon starts from those facts. It uses **Roslyn** to understand the project's real C# and lowers a
much larger language onto the same VM. The result is still stock Udon; the gain is in the compiler.

### What 2.0 adds

| Area | Before | 2.0 |
| --- | --- | --- |
| Value types | User structs | Structs, nesting, arrays, constructors, methods, `Equals` |
| Collections | `List<T>` | `List`, `Dictionary`, `HashSet`, `Queue`, `Stack`, keys/values, trimming |
| Generics | None | Generic methods and structs, monomorphised |
| Recursion | Direct self-recursion | Direct and mutual recursion |
| Calls | Inlining only | Closed-world virtual/interface dispatch, properties and indexers |
| Callbacks | None | Delegates, events, lambdas, closures, cross-behaviour targets |
| Iteration | `foreach` over built-ins | `yield` state machines |
| Patterns | Basic switch | Type/declaration/property patterns and switch expressions |
| Tooling | Assembly view | Complexity budget bar and code-size guards |

---

## 2. The runtime we target

### 2.1 Heap

The Udon heap is a flat array of `(object value, Type type)` pairs. Every variable in a program is an
index into that array. There are no call-stack locals: a program's heap variables *are* its storage.

Values cross the ABI by heap address. An extern receives addresses and casts the boxed values to its
parameter types. A slot's declared type is an initial hint; an assignment can change the runtime type.
If a value is not castable when an extern runs, the VM halts the behaviour.

Two consequences drive the whole compiler:

1. A heap slot can hold any CLR object, including arrays. Structs and collections are represented this
   way.
2. Copying C# value semantics must be explicit. The heap stores references, so a struct copy is a deep
   clone of its field array.

### 2.2 Instructions

The VM is a small stack machine with no native call/return:

| Opcode | Operand | Effect |
| --- | --- | --- |
| `PUSH` | heap index | push an address |
| `POP` | — | discard |
| `COPY` | — | copy the value at the popped address |
| `JUMP` | address | unconditional branch |
| `JUMP_IF_FALSE` | address | pop and branch on `SystemBoolean` |
| `JUMP_INDIRECT` | heap index | branch to an address read from the heap |
| `EXTERN` | heap index | call an SDK function; the operand is rewritten with a cached delegate |
| `NOP` / `ANNOTATION` | — | padding |

`JUMP, 0xFFFFFFFC` means "return": it jumps past the end of the bytecode and halts the invocation.

### 2.3 Externs

An extern name has the form:

```
SomeUdonType.__Method__ParamType1_ParamType2__ReturnType
```

`Ref` is appended for by-reference parameters, `ctor` is used for constructors, and generic arguments
appear as invisible `SystemType` parameters. The runtime resolves the string to a generated wrapper
and caches it.

The compiler validates every signature against the installed SDK's node registry. A missing extern
becomes a compile error naming the exact signature instead of a runtime halt.

### 2.4 Events

An entry point is an `.export`ed label. The VM calls it by name and writes arguments into named heap
symbols such as `onPlayerJoinedPlayer`. Custom events are entry points without a leading underscore.

---

## 3. Compiler architecture

```
BetterUdonBehaviour (C# subclass)
        │
        ▼
┌────────────────────────┐
│ Front end              │  Roslyn: parse Assets/**, build the semantic model,
│ BetterUdonCompiler     │  find BetterUdonBehaviour types
└───────────┬────────────┘
            ▼
┌────────────────────────┐
│ Lowering               │  expressions → slots, statements → labels,
│ BehaviourCodeGenerator │  structs, collections, generics, calls, events
└───────────┬────────────┘
            ▼
┌────────────────────────┐
│ Emission               │  AssemblyBuilder: labels, instruction sizes,
│ AssemblyBuilder        │  data section, heap size
└───────────┬────────────┘
            ▼
┌────────────────────────┐
│ Assembly               │  UAssemblyAssembler + fixed-size heap factory
│ BetterUdonAssembler    │
└───────────┬────────────┘
            ▼
   SerializedUdonProgramAsset → stock Udon VM
```

### 3.1 Front end

The compiler builds a `CSharpCompilation` from every `.cs` file under `Assets/` (excluding `Editor`
folders) plus metadata references to the loaded editor assemblies. The project has already compiled
that code for Unity, so Roslyn gives BetterUdon real overload resolution, conversions and type
inference.

Each class deriving from `BetterUdonBehaviour` is compiled independently. Sources are re-parsed per
compile pass because Roslyn nodes cannot be moved between trees. Results are cached by the newest
source timestamp.

### 3.2 Lowering

The generator is a recursive emitter. Every expression becomes a **slot** (a heap variable), and every
statement becomes control flow over labels.

- **Slot allocation** — one slot per field, local, parameter, temporary and constant, with stable
  names (`__tmp_17`, `__const_3`, `slots`, …).
- **Lvalues** — assignments use an `LValue` abstraction: plain slot, array element, struct field,
  extern member, callback property or list element.
- **Conversions** — `ConvertTo` maps implicit and explicit C# conversions to
  `SystemConvert.__ToX__` externs, with reference conversions free and struct copies explicit.
- **Inlining** — normal user calls, constructors, local functions and property accessors are expanded
  at the call site. `[RecursiveMethod]` methods are compiled once as callable blocks.
- **Extern construction** — `UdonExternSignatures` builds signatures from Roslyn symbols and validates
  them against the SDK.
- **Monomorphisation** — generic type arguments are substituted through inlined bodies, slots, arrays,
  defaults and extern signatures.

### 3.3 Emission

`AssemblyBuilder` tracks the byte address of each instruction so jumps can resolve. The size table
mirrors the VM:

```
PUSH 8   POP 4   COPY 4   JUMP 8   JUMP_IF_FALSE 8   EXTERN 8   JUMP_INDIRECT 8
```

Labels and exports add no instruction size. An entry point emits `.export name` followed by `name:`.

### 3.4 Assembly and heap sizing

`UdonEditorManager.Assemble` uses a fixed default heap that larger programs overflow. BetterUdon builds
its own `UAssemblyAssembler` with a heap factory sized to the generated slots plus unique externs,
mirroring UdonSharp's approach.

### 3.5 Validation

Before a program is stored, BetterUdon:

1. Checks every extern against the SDK node registry.
2. Serializes and deserializes the program with Udon's serializer.
3. Reports any symbol-table loss as a compile error naming the asset.

The serializer fails softly — it logs a bad value and loses the symbol table — so this round trip is
required for reliable assets.

---

## 4. Value types

### 4.1 Representation

A user struct is lowered to an `object[]` whose elements are its fields in declaration order:

```csharp
public struct InventorySlot          // object[] { itemId, durability, filled }
{
    public int itemId;
    public float durability;
    public bool filled;
}
```

Nested structs are arrays inside arrays. Struct fields of a behaviour are heap slots holding the field
array. Any field type Udon can store can live inside a struct: primitives, Unity structs, strings,
references and other structs.

### 4.2 Copy semantics

Every C# operation that copies a value performs an explicit deep clone:

| C# operation | Lowered operation |
| --- | --- |
| `b = a` | `b = Clone(a)` |
| `f(a)` | parameter slot = `Clone(a)` |
| `return a` | result slot = `Clone(a)` |
| `var x = s.field` | `x = Clone(Get(s, index))` |
| `s.field = v` | `Set(s, index, Clone(v))` |
| `arr[i] = v` | `Set(arr, i, Clone(v))` |

Reference-typed fields are copied as references, as C# requires.

### 4.3 Lvalues

A write through a struct chain must not copy intermediate values:

```csharp
stats.weapon.itemId = 42;

// ref := stats            (no copy)
// ref := Get(ref, 0)      (no copy: the nested array itself)
// Set(ref, 0, 42)
```

The lvalue chain therefore uses non-copying reads. Only the final store copies.

### 4.4 Struct methods and constructors

A struct method call binds `this` to the receiver's storage and inlines the body, so field mutations
are visible to the caller when the receiver is a variable. A call on a temporary — a method result or
a property — operates on a copy, matching C#.

Constructors allocate the field array, fill defaults, and inline the constructor body with `this`
bound to it.

### 4.5 Equality

A user struct's `Equals` and collection equality are member-wise. `List<MyStruct>.Contains`,
`IndexOf`, `Remove`, dictionary keys and `HashSet` membership all use the same value comparison.

---

## 5. Collections

Udon has no collection nodes. All collections are compiler intrinsics built from plain arrays.

### 5.1 `List<T>`

`List<T>` is a two-element heap object:

```
[0] = T[] items
[1] = int count
```

It is a reference type: assignment aliases, mutations are visible through every reference, and
`List<MyStruct>` stores cloned elements.

| Member | Implementation |
| --- | --- |
| `Count` | stored count |
| `Capacity` | element-array length; settable |
| `Add` | grow if needed, store, increment |
| `AddRange` | append an array |
| `Insert` | grow, shift up, store |
| `RemoveAt` | shift down, decrement |
| `Remove` | `IndexOf` then `RemoveAt` |
| `Contains` / `IndexOf` | linear scan with value equality |
| `Clear` | count = 0 |
| `ToArray` | allocate and copy |
| `TrimExcess` | shrink the backing array to the count |
| `this[i]` | array element read/write |
| `foreach` | index loop over live count |

Growth doubles capacity with a floor of 4, matching the BCL policy.

### 5.2 Other collections

| Type | Representation | Coverage |
| --- | --- | --- |
| `Queue<T>` | `[T[] items, int count]` | `Enqueue`, `Dequeue`, `Peek`, `Count`, `Contains`, `Clear`, `ToArray`, `TrimExcess` |
| `Stack<T>` | `[T[] items, int count]` | `Push`, `Pop`, `Peek`, `Count`, `Contains`, `Clear`, `ToArray`, `TrimExcess` |
| `HashSet<T>` | `[T[] items, int count]` | `Add`, `Remove`, `Contains`, `Count`, `Clear`, `ToArray`, `TrimExcess` |
| `Dictionary<K,V>` | `[K[] keys, V[] values, int count]` | indexer, `Add`, `ContainsKey`, `TryGetValue`, `Remove`, `Count`, `Clear`, `TrimExcess`, `KeyValuePair` foreach |

Queue dequeue shifts the live elements down; Stack uses the tail as its top. Dictionary and HashSet
use member-wise equality and a linear scan instead of a hash table. This is slower than the BCL but
keeps equality correct for primitives, strings and user structs without a runtime service.

### 5.3 Dictionary keys and values

`dictionary.Keys` and `dictionary.Values` are stored wrappers around the dictionary:

```csharp
Dictionary<int, string> map = new Dictionary<int, string>();
map[1] = "one";
map[2] = "two";

Dictionary<int, string>.KeyCollection keys = map.Keys;
int count = keys.Count;              // 2
if (keys.Contains(2)) { ... }
int[] copy = keys.ToArray();

foreach (int key in keys)
    sum += key;
```

`Count`, `Contains`, `ToArray`, `CopyTo` and `foreach` are supported for both collections.

### 5.4 Capacity and trimming

`List<T>.Capacity` is settable and preserves live elements. `TrimExcess()` is implemented for `List`,
`Queue`, `Stack`, `HashSet` and `Dictionary`.

Unity's .NET profile does not expose `Queue<T>.Capacity` or `Dictionary<K,V>.Capacity`, so those
properties cannot be written in source even though the underlying representation has a capacity.

### 5.5 Collection performance

The important cost is not the representation but the generated code. Every list operation is inlined;
a loop that calls `Add` per element expands code, and `foreach` over a dictionary is linear. The
inspector complexity bar exists to make that growth visible.

---

## 6. Language coverage

### 6.1 Expressions and statements

| Feature | Lowering |
| --- | --- |
| `ref` / `out` / `out var` | inline, copy in, copy back after the call |
| Implicit numeric conversions | integral → floating and floating → `double`, including extern constructor arguments |
| `in` | inline, copy in only |
| Named / optional / `params` | argument-to-parameter binding, constants, packing |
| Local functions | inlined; captures resolve to enclosing slots |
| Auto properties | backing field slot |
| Properties with bodies | accessor bodies inlined on get and set |
| `?.` | null test and guarded block |
| `??` / `??=` | control flow |
| `is null` / `is not null` | equality against an object-typed null constant |
| `switch` / `when` | equality chain with guard |
| `?:` | branch and convert both arms to the result type |
| `nameof(x)` | compile-time string |
| String interpolation | string `op_Addition` concatenation |
| `foreach` over arrays / `List<T>` | index loop |
| `typeof(T)` | `System.Type` heap constant |
| `GetComponent<T>()` | Udon `SystemType` overload / marker lookup for behaviours |
| `base.Method()` | inlined like a local method |
| `[RecursiveMethod]` | jumpable block with software frames |
| Generic methods and structs | monomorphised |
| Virtual and interface calls | closed-world type-marker dispatch |
| User enums | underlying integer |
| Extern enums (`KeyCode`, `Space`) | real Udon enum type; compare as `(int)` |
| Extern struct initialisers | `new Color(...)`, `new Vector3(...)` baked as values |

### 6.2 Functions and properties

Local functions are inlined and can capture enclosing locals. Properties work whether they are auto
properties, have explicit getter/setter bodies, or are expression-bodied. Private setters and accessor
bodies behave like normal C#.

### 6.3 Patterns and switch expressions

`is` supports null, type, declaration and property patterns:

```csharp
if (target is Enemy enemy)
    enemy.Damage(10);

if (target is Enemy { Health: > 0 })
    ...
```

Property patterns read through the same dispatch/storage paths as ordinary property access. Switch
expressions support constant, relational, `when` and discard arms:

```csharp
string label = health switch
{
    > 75 => "healthy",
    > 25 => "hurt",
    _ => "critical",
};
```

### 6.4 Enums, types and Unity helpers

User-defined enums are stored as their underlying integer, so comparisons, casts, `[Flags]` and switch
work as C# expects. Extern enums keep their Udon enum type but have no operators in the registry, so
the compiler compares them as `(int)`.

`typeof(T)` produces a real `System.Type` heap constant. `GetComponent<T>()` and its common variants
work for BetterUdon behaviours and normal Unity component types.

### 6.5 Exceptions and unsupported syntax

There is no `try` / `catch` / `throw`: an exception in the Udon VM halts the behaviour and cannot be
caught. `lock`, `unsafe`, `async` / `await`, coroutine scheduling and manual `IEnumerator` objects are
also outside the compiler's model.

---

## 7. Generics

Generic methods and generic structs are monomorphised. The compiler substitutes the concrete type
arguments into the inlined body and emits a specialised copy:

```csharp
private T Identity<T>(T value) => value;

int a = Identity(7);
string b = Identity("hello");
```

Each call gets its own slots and conversions. `Box<int>` and `Box<string>` are separate struct layouts.

Supported inside generic bodies:

- `default(T)`, `new T()` for value types, `typeof(T)`
- `T[]` creation and element access
- `List<T>` and other generic collections
- `ref` / `out` / `in` parameters
- nested generic calls, including calls to other generic methods
- `where T : struct`, `class`, `new()` constraints that only need substitution
- `IComparable<T>.CompareTo` for numeric types and user structs

There is no runtime generic instantiation. Every concrete instantiation must be visible at compile
time. Generic classes and generic `BetterUdonBehaviour` components are not supported.

Because every call site can produce a specialised body, generic breadth affects code size. The
compiler reports an error above 256 generic instantiations in one behaviour, and the inspector bar
shows the same ratio.

---

## 8. Recursion

Udon has no native call stack. BetterUdon normally assumes a method cannot call itself and inlines it.

`[RecursiveMethod]` changes that:

```csharp
[RecursiveMethod]
private int Factorial(int n)
{
    if (n <= 1)
        return 1;

    return n * Factorial(n - 1);
}
```

The method is emitted once as a labelled block. A call pushes a return address and jumps to the block;
the block returns with `JUMP_INDIRECT`. Before a recursive call, the caller's live slots are pushed onto
a growable heap stack and restored after the call returns.

Both direct and mutual recursion work:

```csharp
[RecursiveMethod] private bool IsEven(int n) => n == 0 || IsOdd(n - 1);
[RecursiveMethod] private bool IsOdd(int n) => n != 0 && IsEven(n - 1);
```

Restrictions:

- recursive methods cannot take `ref` / `out` / `in` parameters;
- they must be declared on the behaviour itself, not called on another behaviour;
- Udon events with arguments cannot be recursive; move the recursion into a helper;
- `[NetworkCallable]` and `[RecursiveMethod]` cannot be combined.

The frame stack grows to twice the size the current frame needs when it fills up.

---

## 9. Delegates, events and closures

Delegates are represented as an invocation array. Each entry contains a target behaviour, a method ID
and an optional closure object.

Supported delegate shapes include:

- `System.Action`
- `System.Action<T>` and other `Action<...>` overloads
- `System.Func<TResult>` and `System.Func<..., TResult>`

A delegate value can come from a method group or a lambda:

```csharp
private Action onReady;
private Func<int, int> transform;

private void Ready() { ... }

private void Configure()
{
    onReady = Ready;
    onReady += () => Debug.Log("ready");

    transform = value => value * 2;
}
```

`+=` and `-=` add and remove subscribers. Invocation loops over the entries and inlines each target.
A captured variable is snapshotted in the closure at delegate creation and written back after the
invocation; it is not a shared variable alias.

`event` fields use the same representation, so multi-subscriber events work:

```csharp
public event Action<Item> ItemAdded;

ItemAdded(item);   // safe with zero, one or many subscribers
```

Cross-behaviour method groups are supported: the entry stores the target behaviour and invokes the
target body against that receiver.

---

## 10. Iterators

An iterator used directly in `foreach` is compiled into a resumable state machine in the caller:

```csharp
private IEnumerable<int> Numbers(int count)
{
    for (int i = 0; i < count; i++)
        yield return i;
}

foreach (int number in Numbers(4))
    total += number;
```

A state slot records which `yield` was reached. Locals live on the heap, so re-entering the block jumps
back after the previous yield. Nested loops, arguments, locals, struct elements and `yield break` work.

This is a synchronous lowering for direct `foreach`. Manual `IEnumerator` objects, coroutine
scheduling, `yield` inside `try`/`finally`, and `async`/`await` are not supported.

---

## 11. Inheritance, interfaces and dispatch

Every BetterUdon behaviour in the project is visible at compile time. A polymorphic call is lowered to
a switch over the receiver's hidden `__betterUdonType` marker. Each branch inlines the concrete
implementation against the receiver.

This covers:

- `virtual`, `override`, `abstract` and interface methods;
- virtual/interface properties;
- virtual/interface indexers;
- `is` and `as` against known behaviour and interface types.

```csharp
public interface IDamageable
{
    void Damage(int amount);
    int Health { get; }
}

public class Target : BetterUdonBehaviour, IDamageable
{
    public int health;
    public virtual void Damage(int amount) => health -= amount;
    public virtual int Health => health;
}

public class ArmoredTarget : Target
{
    public override void Damage(int amount) => health -= amount / 2;
    public override int Health => health;
}
```

`base.Damage(...)` is statically bound and is not dispatched. A null receiver runs no branch.

The receiver must be a BetterUdon behaviour. Plain classes and structs are not part of the type-marker
table. BCL runtime type tests are outside this closed-world model.

---

## 12. Cross-behaviour calls

A reference to another BetterUdon behaviour behaves like a normal C# reference for the supported member
kinds. The callee body is inlined with the receiver bound as `this`, so calls are synchronous.

```csharp
public class Door : BetterUdonBehaviour
{
    public Lever lever;

    public void Open()
    {
        if (!lever.pulled)
            return;

        lever.uses = lever.uses + 1;   // field read/write
        lever.Total = 5;               // property
        int marks = lever.Play(3);     // method with argument and return value
        marks += lever[0];             // indexer
    }
}
```

Supported:

- fields, including struct copies across the behaviour boundary;
- methods with arguments and return values;
- properties and indexers;
- private fields and nested calls inside the inlined body;
- `transform`, `gameObject`, `enabled`, `SendCustomEvent`, `GetProgramVariable` and
  `SetProgramVariable` resolve against the receiver;
- typed `GetComponent<T>()` using the hidden type marker.

A call is synchronous because the code is inlined. `SendCustomEvent` remains the asynchronous option
when the target must run on its own schedule.

`GetComponentInChildren` / `GetComponentInParent` are not supported for behaviour types because Udon
cannot search by behaviour type; pass the `GameObject` and call `GetComponent` on it.

Several BetterUdon behaviours can live on one GameObject, including two components of the same type.
Each proxy owns its own backing `UdonBehaviour`.

---

## 13. Networking

### 13.1 Synced variables

`[BetterUdonSynced]` marks a public field for Udon synchronization:

```csharp
public class Door : BetterUdonBehaviour
{
    [BetterUdonSynced] public bool open;
    [BetterUdonSynced] public SyncSample state;

    public void Toggle()
    {
        TakeOwnership();
        open = !open;
        RequestSerialization();
    }
}
```

The compiler generates the sync metadata and any entry points needed to pack/unpack structs. You do
not write serialization callbacks unless you want to observe or adjust values.

Rules:

- synced fields must be exported public variables; `List<T>`, auto properties and
  `[BetterUdonNoExport]` fields cannot be synced;
- the field type must be syncable (`UdonNetworkTypes.CanSync`);
- synced fields are stored with their precise Udon type, not `SystemObject`;
- interpolation is requested per field with `BetterUdonSyncMode.Linear` or `Smooth`.

### 13.2 Sync modes

`[BetterUdonBehaviourSyncMode]` accepts `Any`, `None`, `NoVariableSync`, `Continuous` and `Manual`,
with the same validation UdonSharp applies.

| Mode | Effect |
| --- | --- |
| `Any` | default, derived from synced fields |
| `Manual` | values sent on `RequestSerialization()`; no interpolation |
| `Continuous` | forces continuous sync; arrays and structs are rejected |
| `None` | no sync and no network events |
| `NoVariableSync` | network events only |

Sync mode belongs to the **GameObject**, not the behaviour. Udon's `SyncMethod` setter applies to every
`UdonBehaviour` on the object, so several behaviours on one object share one mode.

### 13.3 Network events and callables

`SendCustomNetworkEvent(NetworkEventTarget.All, "Open")` works on any behaviour with a sync mode other
than `None`. Parameters are supported on `[NetworkCallable]` entry points:

```csharp
[NetworkCallable(30)]
public void Apply(int amount, string label) { ... }

public void Send() => SendCustomNetworkEvent(NetworkEventTarget.All, nameof(Apply), 9, "net");
```

The compiler validates public, non-static, void, at most eight parameters, no `ref`/`out`/`params`/
defaults, and no overloads. Parameter symbols are named `<Method>_<parameter>__param` and the client
metadata uses the same names.

### 13.4 Change callbacks

`[BetterUdonSynced(OnChange = nameof(Handler))]` runs a parameterless method or property setter after
synced values arrive:

```csharp
[BetterUdonSynced(OnChange = nameof(OnHealthChanged))] public int health;

private void OnHealthChanged()
{
    UpdateBar();
}
```

The callback runs on `OnDeserialization`, after struct mirrors are unpacked and before the user's
`OnDeserialization` body.

### 13.5 Structs and struct arrays

A struct is an `object[]`, which Udon cannot sync directly. BetterUdon flattens it into hidden synced
scalars:

```
__sync_sample_hits              %SystemInt32       .sync
__sync_sample_seconds           %SystemSingle      .sync
__sync_sample_nested_active     %SystemBoolean     .sync
__sync_sample_nested_position   %UnityEngineVector3 .sync
```

Struct arrays add a synced length plus parallel arrays for each leaf. Packing runs in generated
`_onPreSerialization`; unpacking runs at the top of `_onDeserialization`. Both are generated when the
script does not declare them.

Struct sync is always manual. Every leaf must be a syncable scalar; nested structs are flattened
recursively. A null struct array packs as length 0 and unpacks as an empty array.

---

## 14. Editor tooling

Selecting a BetterUdon behaviour shows:

- the normal Unity fields;
- a **BetterUdon** box with **Recompile**, **Copy Assembly** and a link to the program asset;
- a **Program** box with generated assembly and disassembly;
- a **complexity bar**.

The complexity bar combines three measurements:

| Metric | Guidance budget |
| --- | ---: |
| Heap slots | 20,000 |
| Instruction words | 50,000 |
| Generic instantiations | 256 |

It fills from green through yellow/orange to red as the worst ratio grows. Over-budget programs also
receive a compile-time diagnostic. These are BetterUdon guardrails: Udon uses 32-bit heap/program
addresses and does not impose the same counts.

`Tools > BetterUdon > Run Diagnostics` compiles every behaviour and writes the assembly, diagnostics,
entry points and heap values to `Library/BetterUdonReport.txt`.

---

## 15. Validation and testing

Every extern signature is checked against the installed SDK's node definitions. Every compiled program
is also serialized and deserialized once with Udon's serializer before it is stored. That turns the
serializer's silent symbol-table loss into a compile error.

BetterUdon is developed against an executable specification: language, struct, networking,
cross-behaviour, recursion, collection, iterator, delegate and pattern suites run inside the editor
against the real VM.

---

## 16. Limits

BetterUdon cannot add capabilities the runtime lacks.

| Limit | Reason |
| --- | --- |
| No `try` / `catch` / `throw` | The VM halts on an exception and has no unwinding |
| No `async` / `await` / coroutines / manual `IEnumerator` | No scheduler or coroutine runtime is provided |
| No runtime generic construction | Udon cannot instantiate a generic type at runtime |
| No generic classes or generic behaviour components | Unity component types cannot be generic |
| No `lock`, `unsafe`, threads or native code | Not exposed by Udon |
| No `record struct` | Requires C# 10; the project compiles as C# 9 |
| No list patterns | Require C# 11; the project compiles as C# 9 |
| Closure identity is snapshot-based | Captures copy values rather than sharing a variable cell |
| BCL surface is limited | Only SDK externs and implemented intrinsics are callable |
| No open-world type checks | Dispatch and `is`/`as` use the project's closed-world type table |

Additional authoring notes:

- field initialisers must be compile-time constants; create collections in `Start`;
- auto property and collection backings are not Unity-serializable;
- Unity object references are not baked into program assets; Udon fills them from public variables;
- a custom event raised from `Start` is dropped until `Start` completes;
- `SendCustomEvent` dispatch is asynchronous.

---

## 17. Conclusion

The Udon VM is a capable heap machine with a small instruction set and a registry of callable
functions. Most of what UdonSharp forbids is not forbidden by that machine; it is absent because the
transpiler has no representation for it.

BetterUdon shows that a real semantic front end and deliberate lowering can support value types,
generic containers, recursion, delegates, iterators, pattern matching and synchronous cross-behaviour
calls while still producing stock Udon programs.
