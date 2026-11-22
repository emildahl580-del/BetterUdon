# AOT expansion plan

Goal: cover as much C# as possible by lowering to ordinary Udon assembly. No interpreter,
no custom runtime, no scene helper required for core language features.

This file is the working checklist for the `feature-expansion` branch. Keep it updated as
items land, and add new findings under the relevant section rather than keeping them in chat.

Status values: `[x]` done and verified, `[~]` in progress, `[ ]` not started.

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
- [ ] Constraints that require interface member calls are blocked on closed-world dispatch.
- [ ] Deduplication policy: share reference-type instantiations where the representation is
      already `object`; specialise value-type instantiations.
- [ ] Code-size guard and diagnostic for pathological instantiation counts.
- [x] Tests: identity, swap, arrays, nested generic calls, struct arguments/returns,
      multiple instantiations in one behaviour.

## 3. Closed-world dispatch

Use the fact that all project types are visible at compile time.

- [x] Build a project-wide table of BetterUdon types, interfaces and overrides.
- [x] Interface calls and `virtual`/`override`/`abstract` calls lowered to a type-tag switch
      that inlines each implementation against the receiver.
- [~] Delegates and events represented as target + method ID + closure value; invocation
      dispatches synchronously when the target is known, otherwise through the generated table.
      `Action`, `Action<T>` and `Func<...,TReturn>` method groups, multicast `+=`/`-=`,
      multi-subscriber events and non-capturing lambdas on this behaviour are working.
- [x] Typed delegate parameters and return values (`Action<T>`, `Func<T,R>`).
- [ ] Cross-behaviour delegate targets: synchronous inlining where possible; document the
      asynchronous event boundary when it is not.
- [~] Lambdas and closures. Non-capturing lambdas work; capturing lambdas are rejected.
- [ ] `is` / `as` / casts against known BetterUdon and user types.
- [x] Diagnostics for open-world or missing implementations.
- [x] Virtual/interface properties.
- [x] Virtual/interface indexers.
- [~] Tests: interface dispatch, virtual override, method arguments and return values, virtual property
      get and set, virtual indexer, parameterless and typed Action/Func method groups, multicast and
      events, null delegates, non-capturing lambdas.
- [ ] Tests: struct implementation, null target, capturing lambda/closure, cross-behaviour delegate.

## 4. Collections

Implement the collection library in lowered code, not as a runtime service.

- [~] `Dictionary<K,V>`: insert, lookup, remove, resize, `TryGetValue`, enumeration.
      Linear search, indexer, `Add`, `ContainsKey`, `Remove`, `Count`, `Clear` and `TryGetValue` work;
      `Keys`/`Values` are still pending (dictionary `KeyValuePair` foreach works).
- [x] `HashSet<T>`: add, remove, contains, clear, to-array.
- [x] `Queue<T>`: enqueue, dequeue, peek, count, contains, to-array, grow.
- [x] `Stack<T>`: push, pop, peek, count, contains, to-array, grow.
- [x] `KeyValuePair<K,V>` and dictionary `foreach`.
- [ ] `Capacity`, `TrimExcess`, constructors with comparer (where meaningful).
- [~] Struct keys and values with member-wise equality/hash. Struct dictionary values, queue/stack elements are verified;
      struct dictionary keys still need a test.
- [~] Share the existing `List<T>` resize and element-copy paths. Queue/Stack/HashSet share them;
      Dictionary has its own two-array growth.
- [x] Tests: insert/lookup/remove/resize, string keys, struct values, queue, stack, set, growth.

## 5. Iterators and async state machines

- [x] `yield return` and `yield break` lowered to a state machine with a heap-resident state.
- [~] `IEnumerable<T>` / `IEnumerator<T>` / non-generic `IEnumerable` lowering. Direct `foreach`
      over an iterator call is supported; manual enumerator objects are not.
- [x] `foreach` over compiler-generated iterators.
- [x] Iterator methods with arguments and locals.
- [ ] `try`/`finally` cleanup around `yield` (currently rejected or unsafe).
- [ ] `async` / `await` lowered to state machines driven by `Update` or delayed events.
- [ ] Optional scheduler helper for time-slicing long operations; core lowering must work
      without a scene object.
- [x] Tests: iterator sequence, nested loops, argument-passing, struct elements, `yield break`.

## 6. After the core set

Reassess what pure AOT can still cover once the above land:

- [ ] `try`/`catch`/`finally` over compiler-generated errors (guarded operations).
- [ ] `using`/`IDisposable` lowering.
- [ ] Pattern matching breadth (`switch` expressions, property patterns, list patterns).
- [ ] `record` structs and generated equality/`ToString`.
- [ ] More BCL helpers that map to existing externs or lowered code.
- [ ] Document which remaining features genuinely require an interpreter or native runtime.

## Rules

- Stock Udon only: no custom runtime, no native code, no Unity Jobs, no compute shaders.
- Core language features must not require a helper GameObject.
- Every new feature needs a compile probe and a runtime assertion against the real VM.
- Prefer a clear compile diagnostic over silently generating slow or wrong code.
- Keep `Documentation~/index.md`, `README.md` and `CHANGELOG.md` in sync when features land.
