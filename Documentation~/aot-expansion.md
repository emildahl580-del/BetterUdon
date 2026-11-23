# AOT expansion plan

Goal: cover as much C# as possible by lowering to ordinary Udon assembly. No interpreter,
no custom runtime, no scene helper required for core language features.

This file is the working checklist for the `feature-expansion` branch. Keep it updated as
items land, and add new findings under the relevant section rather than keeping them in chat.

Status values: `[x]` done and verified, `[~]` in progress, `[—]` out of scope or blocked by the
project's C# 9 language level.

## 1. Mutual recursion

- [x] Allocate recursive parameter and return slots before any block is emitted.
- [x] Save and restore the caller's frame when the target is another recursive method.
- [x] Forward calls to a recursive method whose block is not emitted yet.
- [x] Reject static, generic, inherited and cross-behaviour recursive methods with clear diagnostics.
- [x] Verify direct recursion, mutual recursion, locals, struct returns and 600 alternating frames.

## 2. Monomorphisation

Make generic code compile once per concrete instantiation.

- [x] Type-substitution helper for type parameters, arrays and nested constructed types.
- [x] Substitute expression, local, parameter, field and return types while inlining.
- [x] Generic methods: emit one instantiation per concrete type-argument list at the call site.
- [x] Generic methods calling other generic methods, including nested substitution.
- [x] `default(T)`, `new T()`, `typeof(T)`, `T[]`, `List<T>` and `ref`/`out T` inside generic bodies.
- [x] Generic structs: layout and field access per `Struct<T>` instantiation.
- [x] Generic constraints that only need type substitution (`where T : struct`, `class`, `new()`).
- [~] Constraints that require interface member calls. Numeric and user-struct
      `IComparable<T>.CompareTo` are lowered/inlined; other constrained interface calls are still
      pending.
- [x] Deduplication policy: reference-type instantiations are not shared; call-site inlining is
      retained, with a hard code-size guard below.
- [x] Code-size guard: programs over 20,000 slots or 256 generic instantiations report a compile
      error.
- [x] Tests: identity, swap, arrays, nested generic calls, struct arguments/returns,
      multiple instantiations in one behaviour.

## 3. Closed-world dispatch

Use the fact that all project types are visible at compile time.

- [x] Build a project-wide table of BetterUdon types, interfaces and overrides.
- [x] Interface calls and `virtual`/`override`/`abstract` calls lowered to a type-tag switch
      that inlines each implementation against the receiver.
- [x] Delegates and events represented as target + method ID + closure value; invocation
      dispatches synchronously when the target is known, otherwise through the generated table.
      `Action`, `Action<T>` and `Func<...,TReturn>` method groups, multicast `+=`/`-=`,
      multi-subscriber events, non-capturing and capturing lambdas, and cross-behaviour targets work.
- [x] Typed delegate parameters and return values (`Action<T>`, `Func<T,R>`).
- [x] Cross-behaviour delegate targets.
- [x] Lambdas and closures. Captured values are snapshotted at delegate creation and written back
      after invocation.
- [x] `is` / `as` / casts against known BetterUdon types and interfaces (BCL runtime type tests remain limited).
- [x] Diagnostics for open-world or missing implementations.
- [x] Virtual/interface properties.
- [x] Virtual/interface indexers.
- [x] Tests: interface dispatch, virtual override, method arguments and return values, virtual property
      get and set, virtual indexer, parameterless and typed Action/Func method groups, multicast and
      events, null delegates, non-capturing/capturing lambdas, cross-behaviour delegates and `is`/`as`.
- [ ] Tests: struct implementation, null target.

## 4. Collections

Implement the collection library in lowered code, not as a runtime service.

- [x] `Dictionary<K,V>`: insert, lookup, remove, resize, `TryGetValue`, enumeration.
      Linear search, indexer, `Add`, `ContainsKey`, `Remove`, `Count`, `Clear`, `TryGetValue`,
      `Keys`/`Values` and `KeyValuePair` foreach work.
- [x] `HashSet<T>`: add, remove, contains, clear, to-array.
- [x] `Queue<T>`: enqueue, dequeue, peek, count, contains, to-array, grow.
- [x] `Stack<T>`: push, pop, peek, count, contains, to-array, grow.
- [x] `KeyValuePair<K,V>` and dictionary `foreach`.
- [x] `Keys`/`Values` as stored collection values with `Count`, `Contains`, `ToArray`, `CopyTo`
      and local-variable `foreach`.
- [x] `Capacity` and `TrimExcess` for `List<T>` and `TrimExcess` for the other collections. The
      Unity profile does not expose `Queue<T>.Capacity` or `Dictionary<K,V>.Capacity`.
- [x] Struct keys and values with member-wise equality/hash. Struct dictionary keys/values, queue/stack
      elements are verified.
- [x] Share the existing `List<T>` resize and element-copy paths. Queue/Stack/HashSet share them;
      Dictionary has its own two-array growth.
- [x] Tests: insert/lookup/remove/resize, string and struct keys, struct values, queue, stack, set, growth.

## 5. Iterators and async state machines

- [x] `yield return` and `yield break` lowered to a state machine with a heap-resident state.
- [—] `IEnumerable<T>` / `IEnumerator<T>` / non-generic `IEnumerable` lowering. Direct `foreach`
      over an iterator call is supported; manual enumerator objects are out of scope.
- [x] `foreach` over compiler-generated iterators.
- [x] Iterator methods with arguments and locals.
- [—] `try`/`finally` cleanup around `yield` (out of scope with async/coroutines).
- [—] `async` / `await` lowered to state machines driven by `Update` or delayed events.
- [—] Optional scheduler helper for time-slicing long operations.
- [x] Tests: iterator sequence, nested loops, argument-passing, struct elements, `yield break`.

## 6. After the core set

Reassess what pure AOT can still cover once the above land:

- [—] `try`/`catch`/`finally` over VM errors. The VM halts and does not unwind; a software error model
      would be a separate runtime contract.
- [—] `using`/`IDisposable` lowering.
- [~] Pattern matching breadth. Switch expressions with constant/relational/`when`/discard patterns,
      type/declaration patterns and property patterns work. List patterns require C# 11 while the
      project compiles as C# 9.
- [—] `record` structs and generated equality/`ToString`. `record struct` requires C# 10; this project
      compiles at C# 9.0 (CS8773).
- [~] More BCL helpers that map to existing externs or lowered code.
- [—] Async/coroutine scheduling and manual enumerators, excluded by project direction.

## 7. Output optimization

- [x] Peephole cleanup: NOPs, self copies, push/pop pairs and jumps to the next instruction.
- [x] Unreachable code after an unconditional jump or halt, plus unused compiler slots.
- [x] Copy propagation from constants, event arguments and `this`; dead copy removal.
- [x] Control-flow and liveness analysis with copy coalescing and same-typed slot colouring.
- [x] Inspector toggles stored on the program asset; toggling recompiles and redraws the complexity
      bar immediately. `Recommended` is the default for new and existing assets.
- [x] SAFETY: exported, synced, field, constant, parameter and return-address slots are never shared or
      removed. Unknown extern arity or a stack carried across a recursive jump disables the lifetime
      passes for that program instead of guessing.

## 8. Cross-behaviour cloning

- [x] `Instantiate(BetterUdonBehaviour)` lowers to backing `GameObject` → `VRCInstantiate` →
      type-marker `GetComponent` on the clone, returning the cloned behaviour reference.
- [x] `Instantiate(GameObject)` returns the clone GameObject.
- [x] `Destroy(clone.gameObject)` removes the spawned object; `Destroy(clone)` keeps Unity's
      component-only behaviour.
- [—] `Instantiate` overloads with parent / position / rotation. Instantiate first, then set the
      transform; add the overloads when the transform composition is worth the extra slots.

## Rules

- Stock Udon only: no custom runtime, no native code, no Unity Jobs, no compute shaders.
- Core language features must not require a helper GameObject.
- Every new feature needs a compile probe and a runtime assertion against the real VM.
- Prefer a clear compile diagnostic over silently generating slow or wrong code.
- Keep `Documentation~/index.md`, `README.md` and `CHANGELOG.md` in sync when features land.
