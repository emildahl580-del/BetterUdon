# BetterUdon

A C# to Udon compiler for VRChat worlds, with user defined structs, `List<T>` and more.

UdonSharp is deliberately limited: no structs, no collections, no `ref`/`out`. BetterUdon compiles a
larger slice of C# into ordinary Udon assembly, assembled by VRChat's own toolchain and executed by the
stock Udon VM — nothing custom runs at world runtime.

```csharp
using BetterUdon;
using System.Collections.Generic;

[System.Serializable]
public struct InventorySlot
{
    public int itemId;
    public float durability;

    public InventorySlot(int itemId, float durability)
    {
        this.itemId = itemId;
        this.durability = durability;
    }

    public void Wear(float amount) => durability -= amount;
}

public class Player : BetterUdonBehaviour
{
    public InventorySlot[] slots = new InventorySlot[4];
    public List<InventorySlot> backpack;

    private void Start()
    {
        slots[0] = new InventorySlot(100, 1f);
        backpack = new List<InventorySlot>();
        backpack.Add(new InventorySlot(7, 0.5f));

        InventorySlot copy = slots[0];   // deep copy
        copy.Wear(0.5f);                 // does not touch slots[0]
    }

    public void Interact()
    {
        backpack[0].Wear(0.1f);          // value semantics per element
    }
}
```

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
- **`List<T>`** — `Count`, `Capacity`, indexer, `Add`, `AddRange`, `Insert`, `RemoveAt`, `Remove`,
  `Clear`, `Contains`, `IndexOf`, `ToArray`, `foreach`.
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
  compiler generates the synced mirrors and the serialisation entry points).

### UdonSharp compatibility

Everything UdonSharp can express is covered, plus the language features above:

| UdonSharp feature | BetterUdon |
| --- | --- |
| `[UdonSynced]` + interpolation modes | `[BetterUdonSynced]` (same modes) |
| `[FieldChangeCallback]` | `[BetterUdonSynced(OnChange = nameof(...))]` |
| `[UdonBehaviourSyncMode]` | `[BetterUdonBehaviourSyncMode]` (`Any`, `None`, `NoVariableSync`, `Continuous`, `Manual`) |
| `SendCustomNetworkEvent` | `SendCustomNetworkEvent`, including `[NetworkCallable]` parameters |
| `[RecursiveMethod]` | `[RecursiveMethod]` (manual frame stack, `JUMP_INDIRECT` return) |
| Several behaviours on one GameObject | supported, one backing `UdonBehaviour` each |
| Udon events (`OnPlayerJoined`, …) | any `Event_*` node, matched by method name |
| `SendCustomEvent`, delayed variants, `Get`/`SetProgramVariable`, `RequestSerialization`, `Networking.*` | ✓ |
| Inheritance between behaviours | ✓ (fields of base behaviours become variables) |

Remaining differences: recursion is limited to direct self-recursion between `[RecursiveMethod]`
methods (no mutual recursion), and `#if UDONSHARP` dual-compilation is not emulated.

## Not supported

Recursion (Udon has no call stack), open generics at runtime, delegates and lambdas, `try`/`catch`,
`async`, `yield`, user generic methods and `Dictionary<K,V>` (both feasible, not implemented).

## Documentation

`Documentation~/index.md` is a paper covering the runtime model, the struct and `List<T>` lowering
strategies, the compiler architecture and the remaining limits.

## License

MIT. See `LICENSE.md`.
