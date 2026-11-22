# BetterUdon

A C# to Udon compiler for VRChat worlds, with user defined structs, `List<T>` and more.

UdonSharp is deliberately limited: no structs, no collections, no `ref`/`out`. BetterUdon compiles a
larger slice of C# into ordinary Udon assembly, assembled by VRChat's own toolchain and executed by the
stock Udon VM — nothing custom runs at world runtime.

```csharp
using BetterUdon;
using System.Collections.Generic;

// A normal C# struct. BetterUdon gives it real value semantics.
public struct Weapon
{
    public string name;
    public int damage;

    public Weapon(string name, int damage)
    {
        this.name = name;
        this.damage = damage;
    }

    public void Upgrade(int amount)
    {
        damage += amount;          // mutates this value
    }
}

public class PlayerInventory : BetterUdonBehaviour
{
    [BetterUdonSynced] public int coins;

    public List<Weapon> bag;       // created in Start
    public Weapon equipped;

    private void Start()
    {
        bag = new List<Weapon>();
        bag.Add(new Weapon("Sword", 10));
        bag.Add(new Weapon("Bow", 6));

        // Structs are copied by value: this is a copy of the sword.
        equipped = bag[0];
        equipped.Upgrade(5);       // equipped.damage becomes 15; bag[0].damage stays 10
    }

    public void Equip(int slot)
    {
        equipped = bag[slot];      // copy the chosen weapon into `equipped`
        RequestSerialization();
    }

    public bool Buy(Weapon weapon, int price)
    {
        if (coins < price)
            return false;

        coins -= price;
        bag.Add(weapon);
        RequestSerialization();
        return true;
    }
}
```

Notice that `Weapon` is just a struct, `List<Weapon>` is just a list, and the synced `coins` field is a plain int. The only BetterUdon-specific parts are the base class and the `[BetterUdonSynced]` attribute. Collections are created in `Start` because field initializers must be compile-time constants.

## Install

BetterUdon is a Unity package. Install it from the git repository:

1. Open **Window > Package Manager**.
2. Press **+** → **Add package from git URL…**
3. Paste the URL and press **Add**:

   ```
   https://github.com/emildahl580-del/BetterUdon.git
   ```

   To pin a release, append the tag:

   ```
   https://github.com/emildahl580-del/BetterUdon.git#v1.6.1
   ```

Requires **VRChat SDK – Worlds 3.5 or newer** in the project (`com.vrchat.worlds` is declared as a
`vpmDependencies` entry, so VCC and the package manager understand the requirement).

### Alternative installs

- **Local clone** — `git clone https://github.com/emildahl580-del/BetterUdon.git Packages/com.betterudon`
  in your project. Unity treats any folder in `Packages/` as an embedded package.
- **Zip** — grab `BetterUdon-1.6.1.zip` from the
  [releases page](https://github.com/emildahl580-del/BetterUdon/releases) and extract it into `Packages/`.
- **Copy** — copy this folder into your project's `Packages/` directory.

### Updating

Package Manager keeps the checkout under `Library/PackageCache`. Use **Update** in the package's
context menu, or change the tag in `Packages/manifest.json` to move to a specific release.

## Use

1. Add a component deriving from `BetterUdon.BetterUdonBehaviour` to a GameObject. The inspector
   creates the backing `UdonBehaviour` and the generated program asset.
2. Write C#. Public fields become the Udon program's public variables.
3. Press **Recompile**, or `Tools > BetterUdon > Compile All Programs`.
4. Press Play — ClientSim runs the generated program.

`Tools > BetterUdon > Run Diagnostics` compiles every behaviour and writes the generated assembly,
diagnostics, entry points and heap values to `Library/BetterUdonReport.txt`.

## Supported

- **Structs** with real value semantics, constructors, mutating methods, nesting, arrays and
  member-wise `Equals`.
- **`List<T>`** — `Count`, `Capacity` (read), indexer, `Add`, `AddRange`, `Insert`, `RemoveAt`, `Remove`,
  `Clear`, `Contains`, `IndexOf`, `ToArray`, `foreach`.
- **Other collections** — `Queue<T>` (`Enqueue`/`Dequeue`/`Peek`), `Stack<T>` (`Push`/`Pop`/`Peek`),
  `HashSet<T>` (`Add`/`Remove`/`Contains`), and `Dictionary<K,V>` (indexer, `Add`, `ContainsKey`,
  `TryGetValue`, `Remove`, `Count`) with struct/string keys and values.
- **Iterators** — `yield return` and `yield break` lowered to a synchronous state machine for direct
  `foreach` over a local iterator method.
- `ref` / `out` / `in`, `out var`, named arguments, optional arguments, `params`.
- Local functions with captures; properties with explicit or expression-bodied accessors.
- `?.`, `??=`, `is null` / `is not null`, `switch` with `when`, `for` / `while` / `do` / `foreach`.
- `typeof(T)`, `GetComponent<T>()` and friends.
- Cross-behaviour `SendCustomEvent` and `Get`/`SetProgramVariable`.
- **Cross-behaviour fields** — `GetComponent<Other>()` (or an inspector reference) then `other.value`
  reads and writes the other behaviour's public variables, with structs copied by value.
- **Cross-behaviour methods and properties** — `int hits = other.GetHits()`, `other.DoThing(a, b)` and
  `other.Total` work synchronously; the method body is inlined against the other behaviour, so
  arguments, return values and private state all behave like C#.
- **Network sync** — `[BetterUdonSynced]` on fields, including user structs and struct arrays (the
  compiler generates the synced mirrors and the serialisation entry points), with interpolation modes
  and `[BetterUdonSynced(OnChange = nameof(...))]` callbacks.
- **Network events** — `SendCustomNetworkEvent`, local or remote, with `[NetworkCallable]` entry
  points taking up to eight parameters.
- **`[BetterUdonBehaviourSyncMode]`** — `Any`, `None`, `NoVariableSync`, `Continuous`, `Manual`,
  validated the way UdonSharp validates them.
- **Recursion** - `[RecursiveMethod]` compiles a method once and keeps a growable frame stack at
  runtime, so direct and mutual recursion with arguments and return values works at practical depths.
- **Several behaviours on one GameObject** — including two components of the same type. Each proxy
  gets its own backing `UdonBehaviour`, and typed `GetComponent<T>()` returns the matching one.
- **Inheritance** — fields of base behaviours become variables; base methods and fields are inlined.
- **Virtual and interface calls** — closed-world dispatch over the receiver's type marker for methods
  and properties; each implementation is inlined with the receiver bound, so calls are synchronous.
- **Generic methods and structs** — monomorphised per instantiation, including struct layouts,
  `default(T)`, `new T()`, `typeof(T)`, `T[]`, `ref`/`out T` and nested generic calls.
- **Udon events** — any `Event_*` node (`OnPlayerJoined`, `OnPickup`, …) matched by method name.

### UdonSharp compatibility

Everything UdonSharp can express is covered, plus the language features above:

| UdonSharp feature | BetterUdon |
| --- | --- |
| `[UdonSynced]` + interpolation modes | `[BetterUdonSynced]` (same modes) |
| `[FieldChangeCallback]` | `[BetterUdonSynced(OnChange = nameof(...))]` |
| `[UdonBehaviourSyncMode]` | `[BetterUdonBehaviourSyncMode]` (`Any`, `None`, `NoVariableSync`, `Continuous`, `Manual`) |
| `SendCustomNetworkEvent` | `SendCustomNetworkEvent`, including `[NetworkCallable]` parameters |
| `[RecursiveMethod]` | `[RecursiveMethod]` (manual frame stack, `JUMP_INDIRECT` return) |
| Several behaviours on one GameObject | supported, one backing `UdonBehaviour` each, duplicates included |
| Udon events (`OnPlayerJoined`, …) | any `Event_*` node, matched by method name |
| `SendCustomEvent`, delayed variants, `Get`/`SetProgramVariable`, `RequestSerialization`, `Networking.*` | ✓ |
| Inheritance between behaviours | ✓ (fields of base behaviours become variables) |

Remaining differences: `[RecursiveMethod]` is still required for recursion (direct and mutual
recursion between marked methods is supported), and `#if UDONSHARP` dual-compilation is not emulated.

### Sync mode is per GameObject

Udon stores the sync mode on the GameObject, not on the behaviour: setting it applies to every
`UdonBehaviour` there. Several BetterUdon behaviours on one object therefore share one mode, so a
`Manual` behaviour next to a `Continuous` one needs separate GameObjects.

## Not supported

Recursion without `[RecursiveMethod]`, runtime/open generics, generic classes and generic behaviour
components, delegates and lambdas, `try`/`catch`, `async`/`await`, manual `IEnumerator` objects,
`yield` inside `try`/`finally`, and dictionary `foreach`/`TryGetValue`/`Keys`/`Values`. These are
tracked in `Documentation~/aot-expansion.md`.

## What's new

**1.6.1** is a bug sweep: array `foreach`, base-behaviour method calls, recursive methods returning a
struct, frame-stack growth and backing selection for two components of the same type on one object are
all fixed. **1.6.0** added `[RecursiveMethod]`, several behaviours per GameObject and
`[NetworkCallable]` parameters. See [CHANGELOG.md](CHANGELOG.md) for the full history.

## Documentation

`Documentation~/index.md` is a paper covering the runtime model, the struct and `List<T>` lowering
strategies, the compiler architecture and the remaining limits.
`Documentation~/aot-expansion.md` is the working checklist for the current AOT expansion.

## License

MIT. See `LICENSE.md`.
