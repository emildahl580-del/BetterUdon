# BetterUdon — A C# to Udon Compiler for VRChat Worlds

**Version 1.0.0**

---

## Abstract

Udon is VRChat's scripting runtime for worlds. It executes a compact bytecode on a heap of
type-erased values, and its authoring tools are a node graph and UdonSharp, a C#-like transpiler.
Both share a hard restriction: the language surface is deliberately small. There are no user defined
value types, no collections, no `ref`/`out` parameters, and no local functions, because the runtime has
no call stack, no way to describe a user type, and no collection library.

BetterUdon is a compiler that removes much of that restriction without changing the runtime. It
compiles a larger subset of C# — including structs with real value semantics and `List<T>` — down to
ordinary Udon assembly, which is then assembled by VRChat's own toolchain and executed by the stock
Udon VM. Nothing custom runs at world runtime.

This paper describes the runtime model BetterUdon targets, the lowering strategies that make the extra
language features possible, the compiler's architecture, and the limits that remain.

---

## 1. Motivation

UdonSharp is a source translator. It binds C# syntax against a hand written symbol table, validates
each construct against what Udon can express, and emits assembly text. Its principal limitations come
from that model:

| Restriction | Root cause |
| --- | --- |
| No user `struct` | The heap has no way to name or allocate a user defined value type |
| No `List<T>` / `Dictionary<K,V>` | No collection nodes are exposed by the SDK at all |
| No `ref` / `out` | No call stack; every call is inlined, so aliasing is impossible |
| No local functions | Same |
| No `?.` | Not implemented in the binder |
| Recursion forbidden | No call stack |

Some of these are genuine runtime limits. Others are limits of the transpiler. BetterUdon is built on
the observation that **the runtime is more capable than the transpiler assumes**: the heap holds
arbitrary boxed objects, arrays of any exposed type can be created, and the `EXTERN` opcode can call
anything the SDK registered. Everything else is a compilation problem.

The second observation is about the front end. UdonSharp binds syntax itself, which means it must
re-implement overload resolution, conversions and type inference. BetterUdon instead asks **Roslyn**
for the semantic model of the project's own sources, so the C# compiler does the binding for it.

---

## 2. The runtime model

Understanding the lowering requires the shape of the target.

### 2.1 Heap

The Udon heap is a flat array of `(object value, Type type)` pairs. Every variable in a program is an
index into that array. There are no locals on a call stack: a program's variables *are* its storage.
Values are type-erased at the ABI boundary — an extern receives heap addresses and casts the boxed
value to the parameter type.

Two consequences shape everything else:

1. A slot can hold a reference to any CLR object, including arrays. This is how structs and lists are
   represented.
2. A slot's declared type is only an initial value hint. Assignments may change the runtime type, and
   an extern will fail at runtime if the boxed value is not castable to the expected type.

### 2.2 Instruction set

The VM is a small stack machine with no call/return:

| Opcode | Operand | Effect |
| --- | --- | --- |
| `PUSH` | heap index | push an address |
| `POP` | — | discard |
| `COPY` | — | `heap[pop] = heap[pop]` |
| `JUMP` | address | unconditional |
| `JUMP_IF_FALSE` | address | pop, branch on `SystemBoolean` |
| `JUMP_INDIRECT` | heap index | branch to an address read from the heap |
| `EXTERN` | heap index | call an SDK function; the operand is rewritten with a cached delegate |
| `NOP` / `ANNOTATION` | — | padding |

`JUMP, 0xFFFFFFFC` is the idiom for "return": it jumps past the end of the bytecode, which halts the
invocation.

### 2.3 Externs

An extern is named by a string of the form

```
SomeUdonTypeName.__MethodName__ParamType1_ParamType2__ReturnType
```

with `Ref` appended to by-reference parameters, `ctor` as the method name for constructors, and
invisible `SystemType` parameters in place of generic type arguments. The runtime resolves the string
to a generated wrapper delegate on first execution and caches it.

This is the single most important fact for extensibility: **anything the SDK registered is callable**,
and the set is discoverable at compile time through the editor's node definition table.

### 2.4 Events

An entry point is a `.export`ed code label. The VM invokes one by name, passing arguments through
named heap symbols (`onPlayerJoinedPlayer`, etc.). Custom events are entry points whose name has no
leading underscore.

---

## 3. Architecture

```
BetterUdonBehaviour (C# subclass)
        │
        ▼
┌───────────────────────┐
│ Front end             │  Roslyn: parse all project sources, semantic model,
│ BetterUdonCompiler    │  find classes deriving from BetterUdonBehaviour
└──────────┬────────────┘
           ▼
┌───────────────────────┐
│ Lowering              │  expressions, statements, structs, lists, events,
│ BehaviourCodeGenerator│  method inlining, extern signature construction
└──────────┬────────────┘
           ▼
┌───────────────────────┐
│ Emission              │  AssemblyBuilder: labels, instruction sizes,
│ AssemblyBuilder       │  data section, heap size
└──────────┬────────────┘
           ▼
┌───────────────────────┐
│ Assembly              │  UAssemblyAssembler + fixed size heap factory
│ BetterUdonAssembler   │
└──────────┬────────────┘
           ▼
   SerializedUdonProgramAsset  →  Udon VM
```

### 3.1 Front end

The compiler builds a `CSharpCompilation` from **every `.cs` file under `Assets/`** (excluding
`Editor` folders), plus metadata references to every assembly loaded in the editor domain. The
project has already compiled that code for Unity, so the analysis compiles too — and gets real
overload resolution, implicit conversions, constant folding and type inference for free.

Each class deriving from `BetterUdonBehaviour` is then compiled independently into its own program.

Two details matter:

- **Syntax trees are re-parsed per compile pass.** Roslyn rejects nodes that do not belong to the
  semantic model's tree, and caching trees across compilations produced exactly that failure. Caching
  is instead done at the *result* level, keyed by the newest source file timestamp.
- **Paths are normalised** (`Path.GetFullPath`) and deduplicated, otherwise a file can be parsed twice
  and its symbols end up pointing at the wrong tree.

### 3.2 Lowering

The generator is a recursive emitter. Every expression is lowered into a **slot** — a heap variable
holding its value — and every statement into control flow over labels. There are no registers and no
expression trees; the emission is direct.

Key components:

- **Slot allocation.** One slot per field, local, parameter, temporary and constant, with a stable
  unique name (`__tmp_17`, `__const_3`, `slots`, …).
- **Lvalue abstraction.** Assignments go through an `LValue` that knows how to read and write a
  storage location. Concrete kinds: plain slot, array element, struct field, extern member (a Unity
  property or field accessed through getter/setter externs), callback (a property with its own
  accessor body), and list element.
- **Conversions.** `ConvertTo` maps C# implicit and explicit conversions onto `SystemConvert.__ToX__`
  externs, with reference conversions free and struct copies explicit.
- **Inlining.** Udon has no call stack, so every user method call — including constructors, local
  functions and property accessors — is expanded at the call site. Recursion is detected and reported
  as an error.
- **Extern construction.** `UdonExternSignatures` builds the signature string from the Roslyn symbol
  and validates it against Udon's node registry; an unavailable extern becomes a compile error with
  the exact signature, not a runtime failure.

### 3.3 Emission

`AssemblyBuilder` tracks the byte address of every instruction so that jumps can be resolved. The size
table mirrors the VM's encoding (one word per opcode and per operand):

```
PUSH 8   POP 4   COPY 4   JUMP 8   JUMP_IF_FALSE 8   EXTERN 8   JUMP_INDIRECT 8
```

Labels carry no size; entry points emit `.export name` followed by `name:`.

### 3.4 Assembly and heap sizing

`UdonEditorManager.Assemble` uses a fixed default heap, which non-trivial programs overflow. The
compiler therefore constructs its own `UAssemblyAssembler` with a heap factory sized to
`slots + uniqueExterns`, mirroring what UdonSharp does.

---

## 4. Structs with value semantics

This is the central feature.

### 4.1 Representation

A user struct is lowered to an `object[]` whose elements are its fields, in declaration order.

```csharp
public struct InventorySlot          //  object[] { itemId, durability, filled }
{
    public int itemId;
    public float durability;
    public bool filled;
}
```

Nested structs are `object[]`s inside `object[]`s. Struct fields of the *behaviour* are ordinary heap
slots holding a reference to such an array.

Because the representation is an array of boxed values, a struct can hold any field type Udon can
store: primitives, Unity structs, strings, references and other structs.

### 4.2 Copy semantics

The heap stores references, so *every* place C# copies a value must be an explicit deep copy:

| C# operation | Lowering |
| --- | --- |
| `b = a` | `b = Clone(a)` |
| `f(a)` | parameter slot = `Clone(a)` |
| `return a` | result slot = `Clone(a)` |
| `var x = s.field` | `x = Clone(Get(s, index))` |
| `s.field = v` | `Set(s, index, Clone(v))` |
| `arr[i] = v` | `Set(arr, i, Clone(v))` |

`Clone` allocates a new array and copies each field, recursing for nested structs. Reference-typed
fields are copied as references, which is correct.

### 4.3 Lvalues

The copy rules only apply when a value is *consumed*. Reading a struct as an lvalue — the receiver of
`a.b.c = x`, or of `list[i].x` — must not copy, or the write would be lost. The lvalue chain therefore
uses a non-copying read (`Get(clone: false)`), and only the final store copies.

```csharp
stats.weapon.itemId = 42;
//  ref := stats            (no copy)
//  ref := Get(ref, 0)      (no copy: the nested array itself)
//  Set(ref, 0, 42)
```

### 4.4 Methods on structs

A struct method call binds `this` to the receiver's storage and inlines the body, so mutations to
`this` fields are visible to the caller — matching C# for a variable receiver. A receiver that is not
an lvalue (a call result, a property) is evaluated into a temporary first, so the mutation is local,
again matching C#.

Constructors work the same way: allocate the array, fill defaults, then inline the constructor body
with `this` bound to it.

---

## 5. `List<T>`

Udon exposes no collection nodes — the node registry contains zero `ListT` definitions. `List<T>` is
therefore implemented by the compiler itself:

```
List<T>  →  object[] {  T[] items,  int count  }
```

It is a **reference type**: the heap slot holds a reference, assignment aliases, and mutations are
visible through every reference, exactly like C#. The element array is a real typed array (`int[]`,
`Vector3[]`, …) or an `object[]` when `T` is a user struct.

Every member is emitted inline:

| Member | Implementation |
| --- | --- |
| `Add` | grow if `count == items.Length`, store, increment |
| `Insert` | grow, shift up from the end, store |
| `RemoveAt` | shift down, decrement |
| `Remove` | `IndexOf` then `RemoveAt` |
| `Contains` / `IndexOf` | linear scan with member-wise struct equality |
| `Clear` | `count = 0` |
| `ToArray` | allocate `T[count]`, copy |
| `AddRange` | append an array |
| `this[i]` | array element read/write |
| `foreach` | index loop to the live `count` |

Growth doubles capacity with a floor of 4 — the same policy as the BCL.

Value semantics compose: `List<MyStruct>` stores struct references, and element reads/writes clone
through the normal struct rules, so `list[0].x = 1` needs the usual C# dance (read, modify, write back)
and a copy never aliases the element.

---

## 6. Other language features

| Feature | Strategy |
| --- | --- |
| `ref` / `out` / `out var` | inline, copy in, copy back after the call |
| `in` | inline, copy in only |
| Named / optional / `params` | argument-to-parameter binding, defaults as constants, packing |
| Local functions | inlined like methods; captures resolve to enclosing slots |
| Auto properties | backing field slot |
| Properties with bodies | accessor bodies inlined on get and set |
| `?.` | null test plus guarded block; void forms produce no temporary |
| `??` / `??=` | control flow |
| `is null` / `is not null` | equality against an object-typed null constant |
| `switch` / `when` | equality chain, guard evaluated after the match |
| `typeof(T)` | `System.Type` heap constant |
| `GetComponent<T>()` | Udon's `SystemType` overload with an invisible type parameter |
| Cross-behaviour `SendCustomEvent` | extern on the real receiver, not `this` |
| User defined enums | lowered to the underlying integer, so `switch`, `==`, casts and `[Flags]` are integer operations |
| Extern enums (`KeyCode`, `Space`, ...) | keep their real Udon enum type; the registry exposes no operators for them, so compare as `(int)` |
| Extern struct initialisers | `new Color(...)`, `new Vector3(...)` are evaluated in the editor and baked as values |

### Across behaviours

Each program owns its heap, so another behaviour's members are reached through the only door Udon
provides: `GetProgramVariable` and `SetProgramVariable` on its backing `UdonBehaviour`. Field access is
lowered to those calls; methods are inlined with the other behaviour bound as `this`. Both keep the
C# shape:

```csharp
public class Door : BetterUdonBehaviour
{
    public Lever lever;              // drag the Lever component in the inspector

    public void Open()
    {
        if (!lever.pulled)
            return;

        lever.uses = lever.uses + 1; // field: read + write the Lever's heap
        lever.Total = 5;             // auto property
        int marks = lever.Play(3);   // method: arguments and a return value
    }
}
```

- **Fields** read and write the receiver's heap. Reading a struct copies it; writing a struct stores a
  copy, so value semantics hold across behaviours.
- **Methods are inlined**: the body is compiled into the caller with the receiver as `this`, so
  arguments, return values, nested calls and private fields all work, and the call returns
  immediately. The body only ever touches the receiver's variables.
- **Properties** work: an auto property reads/writes its backing variable, a property with accessor
  bodies is inlined the same way as a method. Indexers cannot cross behaviours.
- **Accessibility still applies.** From outside a type only its accessible members can be named (C#
  checks that); inside an inlined body of that type, private fields are reachable because they are
  heap variables of the receiver. Public fields that BetterUdon does not export (`List<T>`,
  `[BetterUdonNoExport]`, auto properties) are still reachable through their symbol; any other field
  is a compile error that names it.
- **Unity members and events follow `this`.** `transform`, `gameObject`, `enabled`,
  `SendCustomEvent`, `GetProgramVariable` and `SetProgramVariable` inside an inlined body act on the
  receiver.
- **References survive a build.** A public field typed as a BetterUdon behaviour is stored as that
  behaviour's backing `UdonBehaviour`, because the C# component is removed from a built world.
- **`GameObject.GetComponent<T>()` works** for BetterUdon behaviours: the lookup is done on the
  backing `UdonBehaviour` and then verified against a hidden type marker, so a different behaviour on
  that object yields `null`, exactly like `GetComponent` should.
- **A GameObject can host several BetterUdon behaviours.** Each has its own backing `UdonBehaviour`,
  and a typed `GetComponent<T>()` scans them and returns the one whose type marker matches (or null).
- **`GetComponentInChildren`/`InParent`** are rejected for behaviour types because Udon cannot search
  by behaviour type; pass the `GameObject` and call `GetComponent` on it.
- **Explicit events remain available.** When the call must be asynchronous (a one-way notification, or
  code that runs on the other behaviour's own schedule), use `SendCustomEvent`; a method call is
  synchronous and runs in the caller's execution.

---

## 7. Networking

Synced variables use the same mechanism as the rest of Udon: the assembly declares
`.sync <symbol>, <interpolation>` in the data section, the assembler turns those statements into the
program's sync metadata table, and the client serialises exactly the heap variables listed there.

```csharp
public class Door : BetterUdonBehaviour
{
    [BetterUdonSynced] public bool open;
    [BetterUdonSynced] public SyncSample state;

    public void Toggle()
    {
        TakeOwnership();      // Networking.SetOwner(Networking.LocalPlayer, gameObject)
        open = !open;
        RequestSerialization();
    }
}
```

That is the whole script. There are no serialisation callbacks to implement: the compiler generates the
entry points that pack structs into their mirrors and unpack them, and Udon calls them around the
network transfer. `OnPreSerialization`, `OnDeserialization(DeserializationResult)` and
`OnPostSerialization(SerializationResult)` are available when a script wants to observe or adjust the
values, but overriding them is optional.

The one manual step is the Udon contract itself: in manual sync mode values are sent when the owner
calls `RequestSerialization()`, after taking ownership.

The compiler enforces the rules the Udon serialiser has:

- A synced field must be an exported public variable. `List<T>`, auto properties and
  `[BetterUdonNoExport]` fields cannot be synced.
- The field's type must be one Udon can serialise (`UdonNetworkTypes.CanSync`), which excludes
  `object`, `List<T>` and Unity object references. User structs and struct arrays are accepted: they
  are flattened automatically (see below).
- Synced fields are stored as their precise Udon type (`%SystemString`, `%SystemInt32`, ...) instead of
  the `SystemObject` slot that other reference-typed fields use, because the serialiser works from the
  symbol type.
- Interpolation is requested per field, `[BetterUdonSynced(BetterUdonSyncMode.Linear)]` or `Smooth`.
  It selects **continuous** sync for the whole behaviour, and continuous sync cannot carry arrays, so
  that combination is a compile error.

The behaviour's sync mode is set on the backing `UdonBehaviour` when the program is installed: manual
when it has plain synced variables, continuous when any field asks for interpolation. Programs without
synced variables are left alone, since `SendCustomNetworkEvent` also needs a sync mode.

#### Choosing the mode

`[BetterUdonBehaviourSyncMode(...)]` forces it, like UdonSharp's attribute of the same name:

```csharp
[BetterUdonBehaviourSyncMode(BetterUdonBehaviourSyncMode.NoVariableSync)]
public class Doorbell : BetterUdonBehaviour
{
    public void Ring() => SendCustomNetworkEvent(NetworkEventTarget.All, nameof(OnRung));
    public void OnRung() { }
}
```

| Mode | Effect |
| --- | --- |
| `Any` | default: derived from the synced fields |
| `Manual` | forces manual sync; interpolation is rejected |
| `Continuous` | forces continuous sync; arrays and structs are rejected |
| `None` | nothing is synced and network events cannot be sent |
| `NoVariableSync` | no synced variables, but network events still work |

#### Network events

`SendCustomNetworkEvent(NetworkEventTarget.All, "Open")` sends an event to the owner (`Owner`) or to
every player (`All`). This works on any behaviour with a sync mode other than `None`, including
`NoVariableSync` ones.

Network calls can carry up to eight parameters when the receiver marks its entry point
`[NetworkCallable]`:

```csharp
[NetworkCallable(30)]                 // max events per second
public void Apply(int amount, string label) { ... }

public void Send() => SendCustomNetworkEvent(NetworkEventTarget.All, nameof(Apply), 9, "net");
```

The compiler validates the same restrictions UdonSharp does (public, non-static, void, at most eight
parameters, no `ref`/`out`, `params` or defaults, no overloads) and publishes the metadata the client
uses to map arguments to heap variables. Each parameter is a symbol named `<name>__param`; the
metadata carries the same names, so the receiving entry point reads exactly the values that were sent.

#### Reacting to synced values

`OnChange` names a parameterless method or a property setter that runs after values arrive, like
UdonSharp's `FieldChangeCallback`:

```csharp
[BetterUdonSynced(OnChange = nameof(OnHealthChanged))] public int health;

private void OnHealthChanged()
{
    // health already holds the value that arrived
    UpdateBar();
}
```

The callback runs on `OnDeserialization`, after struct mirrors are unpacked and before the user's
`OnDeserialization` body. A direct `SetProgramVariable` writes the heap without running it (Udon has no
property-change hook on the heap).

`OnPreSerialization`, `OnDeserialization(DeserializationResult)` and
`OnPostSerialization(SerializationResult)` are ordinary Udon events, so overriding them becomes an
entry point. None of them are required for sync to work. Note that an owner does not receive its own
serialisation: `OnDeserialization` only runs for the players that received the data, which the
editor's single-player ClientSim cannot reproduce.

### Structs and struct arrays

A user struct is an `object[]` of its fields on the heap (section 4), and `object[]` is not one of
Udon's syncable types. BetterUdon therefore flattens the struct itself, so declaring the field is
enough:

```csharp
[BetterUdonSynced] public SyncSample sample;
[BetterUdonSynced] public SyncSample[] samples;
```

For a `struct` with fields `hits` (int), `seconds` (float) and `nested` (another struct with `active`
and `position`), the compiler declares hidden synced variables and mirrors the values through them:

```
__sync_sample_hits                 %SystemInt32            .sync
__sync_sample_seconds              %SystemSingle           .sync
__sync_sample_nested_active        %SystemBoolean          .sync
__sync_sample_nested_position      %UnityEngineVector3     .sync

__sync_samples_length              %SystemInt32            .sync
__sync_samples_hits                %SystemInt32Array       .sync
__sync_samples_seconds             %SystemSingleArray      .sync
__sync_samples_nested_active       %SystemBooleanArray     .sync
__sync_samples_nested_position     %UnityEngineVector3Array .sync
```

The struct is packed into the mirrors in a generated `_onPreSerialization` entry point (after any user
code, and before the client serialises) and unpacked at the top of `_onDeserialization`, before user
code runs. Both entry points are generated whenever the script does not declare them, so
`[BetterUdonSynced]` really is all a script needs to write. If a script does declare
`OnPreSerialization` / `OnDeserialization`, the generated packing is woven around the user code and
the callbacks see consistent struct values.

Rules the compiler enforces:

- A struct (or struct array) is always **manual** sync: Udon interpolates single values only, so
  combining a synced struct with `[BetterUdonSynced(BetterUdonSyncMode.Linear)]` is an error.
- Every leaf field must be a syncable scalar. Nested structs are flattened recursively; a leaf of
  type `object`, `List<T>`, another array or a Unity object reference is a compile error that names
  the field (`'BadLeafStruct.bad' has type 'System.Collections.Generic.List<int>', which Udon cannot synchronise.`).
- A null struct array packs as length 0 and unpacks as an empty array, so a missing assignment is safe.
- The mirrors are ordinary exported Udon variables, so they are visible in the program asset under
  **Udon Variables** (collapsed by default) and are stored with the scene. They are compiler output;
  do not edit them.

---

## 8. Validation

Every extern signature is checked against the set of node definitions the installed SDK exposes. This
turns the most common failure mode — an extern that does not exist — into a compile error that names
the exact signature, instead of a runtime halt.

A compiled program is also serialised and deserialised once, with the same serializer Udon uses to
store it, before it is written to its asset. The serializer fails *softly*: a value it cannot write
is logged and the whole symbol table is lost, which only shows up when the scene loads. Checking the
round trip turns that into a compile error that names the asset.

`Tools > BetterUdon > Run Diagnostics` compiles every behaviour in the project and writes
`Library/BetterUdonReport.txt` containing the generated assembly, diagnostics, entry points and
assembled symbol table.

The package ships with two behaviours used as an executable specification: one exercises struct
semantics, one exercises the language features. Together they assert **60+ properties**, and are run
inside the editor against the real VM.

---

## 9. Limits

Some limits are properties of the target and cannot be compiled away:

- **Recursion needs `[RecursiveMethod]`.** A marked method is compiled as a jumpable block with a
  manual frame stack, so it can call itself; unmarked recursion is still a compile error. Mutual
  recursion between two marked methods, `ref`/`out` parameters on a recursive method, and calling a
  recursive method on another behaviour are not supported.
- **No open generics at runtime.** A `T` cannot escape into a field or a runtime-constructed type,
  because the heap has no per-slot type identity and the VM cannot instantiate a generic type.
- **No delegates or lambdas.** No callable object model exists to attach them to.
- **No `try` / `catch`, `throw`, `async`, `yield`, `lock`, `unsafe`.**
- **No user generic methods or generic structs** (yet). The call site knows the type arguments, so
  monomorphisation is possible; it is a front-end feature that has not been implemented.
- **Collections beyond `List<T>`.** `Dictionary<K,V>` is feasible with linear probing but not
  implemented.
- **Inspector limitation.** Auto properties and `List<T>` fields are real heap variables but their
  backings are not Unity-serialisable, so they start at their default value rather than an authored
  one.
- **Unity object references are not baked into the program.** A `Renderer` or `UdonBehaviour[]` field
  is a public variable; the heap starts without it and Udon fills it from the behaviour's public
  variable table on load. Udon stores programs without an object table, so such values cannot be
  heap defaults.

Two runtime behaviours are worth knowing when authoring:

- A custom event raised from `Start` is **dropped**: `RunEventAdvanced` returns false while
  `!_hasDoneStart`.
- Dispatch is asynchronous; a value read immediately after `SendCustomEvent` still shows the old state.

---

## 10. Conclusion

The Udon runtime is a general-purpose heap machine with a small instruction set and a registry of
callable functions. Most of what UdonSharp forbids is not forbidden by that machine — it is absent
because the transpiler has no representation for it.

BetterUdon shows that a compiler with a real semantic front end and a deliberate lowering strategy can
support user defined value types with correct copy semantics, generic container types implemented in
the target language, and a broad slice of modern C#, while still producing programs that the stock VM
executes unmodified.
