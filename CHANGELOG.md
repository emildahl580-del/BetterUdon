# Changelog

## 1.3.0

### Added

- Calling a public, parameterless `void` method on another BetterUdon behaviour (`lever.Pull()`)
  compiles to `SendCustomEvent` on that behaviour. It is asynchronous, because Udon has no call stack:
  the method runs on the other behaviour after the caller yields.
- Cross-behaviour call errors now explain the alternative instead of a generic message: methods that
  take arguments or return a value say to pass data through public fields.

## 1.2.0

### Added

- Cross-behaviour field access. `other.value` on another BetterUdon behaviour reads and writes its
  public variables through Udon's `GetProgramVariable`/`SetProgramVariable`, so no manual
  `GetProgramVariable("value")` calls are needed. Structs are copied on read and write, keeping value
  semantics. Fields must be public variables; anything else is a compile error naming the field.
- `GameObject.GetComponent<T>()` for a BetterUdon behaviour resolves the backing `UdonBehaviour` and
  verifies it against a hidden type marker, so the wrong behaviour yields `null` instead of the wrong
  heap. `GetComponentInChildren`/`InParent` for behaviour types are rejected with guidance.
- Public fields typed as a BetterUdon behaviour are stored as the backing `UdonBehaviour`, which is
  what survives in a built world (the C# component is removed).

### Fixed

- `GetComponent<T>` / `GetComponentInChildren<T>` error paths returned no value, which crashed the
  compiler with a `NullReferenceException` instead of reporting the diagnostic.
- Assigning a field through another instance of the same behaviour wrote to `this`, because the
  lvalue path ignored the receiver.
- A struct field's heap default was declared as an array type, which made Udon's serialiser lose the
  symbol table when the stored program was read back. Object slots keep their symbol type now.

## 1.1.0

### Added

- Network synced variables. `[BetterUdonSynced]` marks a public field; the compiler emits Udon's
  `.sync` metadata, stores the field with its precise Udon type so the serialiser can encode it, and
  switches the backing `UdonBehaviour` to manual sync. `[BetterUdonSynced(BetterUdonSyncMode.Linear)]`
  and `Smooth` select continuous sync with interpolation.
- Synced user structs and struct arrays. The compiler flattens each struct into hidden `__sync_*`
  variables (one per leaf field, one array per leaf plus a length for struct arrays), packs them in a
  generated `_onPreSerialization` and unpacks them at the start of `_onDeserialization`. No mirror
  fields and no serialisation callbacks are needed in user code: the entry points are generated when
  absent, and user overrides are wrapped around the generated packing. Nested structs, `Vector3`,
  `bool`, and changing array lengths work.
- `RequestSerialization()`, `OnPreSerialization()`, `OnDeserialization(DeserializationResult)` and
  `OnPostSerialization(SerializationResult)` on `BetterUdonBehaviour`; the event overrides become
  entry points like `Start` and `Update`.
- `TakeOwnership()` is now callable from compiled code (it was only usable from editor code before).

### Changed

- Inspector layout, so the generated artifacts no longer blend into the script's own fields. On a
  behaviour the order is now: the fields the script exposes, a boxed **BetterUdon** section (source,
  Recompile, Copy Assembly, Program Asset, compile errors), then a separate **Program** box containing
  collapsed **Assembly** and **Disassembly** foldouts. The program asset inspector follows the same
  rhythm: BetterUdon tools, then Public Variables, then the Program box.
- Assembly and disassembly are read-only monospaced blocks inside their own box instead of editable
  text areas mixed in with the public variables.

### Validated at compile time

- A synced field must be an exported public variable, so `List<T>`, auto properties and hidden fields
  are rejected.
- The field type must satisfy `UdonNetworkTypes.CanSync`, and interpolation must be supported by the
  type (`CanSyncLinear` / `CanSyncSmooth`).
- Continuous sync cannot carry arrays, so a synced array on a behaviour that needs interpolation is
  an error.
- A synced struct's leaves must be syncable scalars. A leaf of type `object`, `List<T>`, an array or
  a Unity object reference is rejected with the field path (`'MyStruct.field' has type ...`).
- Synced structs are manual only: requesting interpolation on the same behaviour is an error.

## 1.0.1

Fixes found by building the two sample games. Several of these corrupted the stored program instead of
failing loudly, which surfaced as `SerializationException: Member 'Type' was not found` and
`NullReferenceException` inside `UdonSymbolTable` when the scene loaded.

### Fixed

- `typeof(T)` heap constants and `GetComponent<T>()` now work. The constant was written with the
  runtime type of a `System.Type` instance, which the Odin serializer cannot write; it is declared as
  `System.Type` instead.
- Heap default values never contain `UnityEngine.Object` references (including arrays of them). Those
  belong to the behaviour's public variable table; baking them into the program made the stored
  program unreadable.
- Field initialisers that construct an extern struct (`new Color(...)`, `new Vector3(...)`) are now
  evaluated for real. They used to silently become the struct's default value (a transparent black
  `Color`, for example).
- The type of an object creation expression is taken from the creation itself, because the type
  reference node can be unresolved in the compilation that the field initialiser is evaluated with.
- Every compiled program is serialised and deserialised once before it is stored, so a value the
  serializer cannot round-trip becomes a compile error on the asset instead of a runtime failure.

### Added

- User defined enums: fields, parameters, returns, locals, `switch`, comparisons, casts, `[Flags]`
  and bitwise operators. They are lowered to their underlying integer, so they round-trip through
  the inspector too. Extern enums (`KeyCode`, `Space`, ...) keep their real type because the SDK
  wrappers cast to them; Udon exposes no operators for those, so compare them as `(int)`.

## 1.0.0

First release.

### Compiler

- Roslyn front end over the project's own C# sources (semantic model, real overload resolution).
- Emits Udon assembly (`.data_start` / `.code_start`) assembled by VRChat's `UAssembly` assembler.
- Every emitted extern signature is validated against Udon's node definition table at compile time.
- Heap size computed from the symbol and extern counts instead of Udon's 512 slot default.

### Language features

- User defined `struct`s with real value semantics: copy on assign / pass / return / field read,
  in place writes through lvalues, struct constructors, struct methods that mutate `this`,
  nested structs, arrays of structs and `Equals` (member-wise).
- `List<T>`: `Count`, `Capacity`, indexer, `Add`, `AddRange`, `Insert`, `RemoveAt`, `Remove`,
  `Clear`, `Contains`, `IndexOf`, `ToArray` and `foreach`.
- `ref` / `out` / `in` parameters, `out var`, named arguments, optional arguments, `params`.
- Local functions with captures.
- Auto properties, and properties with explicit `get` / `set` bodies or expression bodies.
- `?.` null-conditional member access and calls, `??=`, `is null` / `is not null`.
- `switch` with `when` guards, `for` / `while` / `do` / `foreach`, `break` / `continue`.
- `typeof(T)` and `GetComponent<T>()` / `GetComponentInChildren<T>()` / `GetComponentInParent<T>()`.
- Cross-behaviour `SendCustomEvent` and `Get`/`SetProgramVariable` with the real receiver.
- String interpolation and concatenation, numeric conversions, unary operators.

### Tooling

- `BetterUdonProgramAsset` owns compilation and serialisation, and draws the generated assembly.
- `Tools > BetterUdon > Compile All Programs` and `Run Diagnostics` (full compiler report).
- Compiles before play mode and before world builds; strips proxy components from built scenes.
