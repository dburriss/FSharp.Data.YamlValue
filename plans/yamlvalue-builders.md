# YamlValue builders — set/remove API for immutable YamlValue trees

Status: Done

## Context

`YamlValue` (implemented per `plans/yamlvalue-parser.md`, now Done) is an immutable DU, same as
`FSharp.Data.JsonValue`. Like `JsonValue`, it has no write API at all — "setting" a field today
means manually rebuilding a `Mapping` array by hand. This plan adds an opt-in construction/editing
API: `SetProperty`/`RemoveProperty` for shallow mapping edits, and path-addressed
`SetPath`/`RemovePath` for nested edits, all returning new `YamlValue`s (never mutating in place).

This is new ground for the FSharp.Data family — `JsonValue` never added anything like this, so
there's no existing convention to match. The design was worked out by exploring concrete usage in
both F# and C#, landing on a string-path DSL as the primary, language-agnostic entry point, with a
fluent builder and raw path list available for programmatic construction.

Intended outcome: users can build up or edit a `YamlValue` document without hand-rolling array
surgery, from both F# and C#, without adding any mutability or complexity to the core `YamlValue`
DU or `YamlExtensions.fs`.

---

## Target API

New namespace/file, kept separate from `YamlExtensions.fs` so the read-only, pattern-match-clean
core stays as-is and this is purely opt-in:

```fsharp
namespace FSharp.Data.Yaml.Builders

open FSharp.Data

type YamlPathStep with
    // reuses the existing YamlPathStep/YamlPath types from YamlValue.fs (Key/Index, root-first list)

[<Class>]
type YamlPath =
    static member Root : YamlPath
    member Key   : name: string -> YamlPath
    member Key   : key: YamlValue -> YamlPath      // non-string keys
    member Index : index: int -> YamlPath
    member Steps : FSharp.Data.YamlPathStep list    // underlying root-first step list

[<Extension>]
type YamlBuilderExtensions =
    // shallow mapping edits
    static member SetProperty : x: YamlValue * name: string * value: YamlValue -> YamlValue
    static member RemoveProperty : x: YamlValue * name: string -> YamlValue

    // string-path DSL — primary entry point, e.g. "services.web.ports[0]"
    static member SetPath : x: YamlValue * path: string * value: YamlValue * ?overwriteScalars: bool -> YamlValue
    static member RemovePath : x: YamlValue * path: string -> YamlValue

    // programmatic path — YamlPath builder or raw step list
    static member SetPath : x: YamlValue * path: YamlPath * value: YamlValue * ?overwriteScalars: bool -> YamlValue
    static member RemovePath : x: YamlValue * path: YamlPath -> YamlValue
```

Usage:

```fsharp
open FSharp.Data
open FSharp.Data.Yaml.Builders

let doc = YamlValue.Parse """
name: myapp
services:
  web:
    image: nginx
    ports: [80, 443]
"""

let updated =
    doc
        .SetProperty("name", YamlValue.String "myapp2")
        .SetPath("services.web.image", YamlValue.String "nginx:1.27")
        .SetPath("services.web.ports[2]", YamlValue.Number 8443m)   // pads/appends
        .RemovePath("services.web.ports[0]")                        // splices, no hole

// non-string key, via the fluent builder instead of the DSL
let withIntKey =
    doc.SetPath(YamlPath.Root.Key("services").Index(0), YamlValue.String "x")
```

```csharp
using FSharp.Data;
using FSharp.Data.Yaml.Builders;

var updated = doc
    .SetProperty("name", YamlValue.NewString("myapp2"))
    .SetPath("services.web.image", YamlValue.NewString("nginx:1.27"));

var withIntKey = doc.SetPath(YamlPath.Root.Key("services").Index(0), YamlValue.NewString("x"));
```

---

## Design decisions

### Separate namespace/file, not YamlExtensions.fs

`YamlExtensions.fs` is the read-only, "know what this document looks like" half of the API
(`docs/reference.md`). Mutation-flavored construction is a distinct concern users should opt into
explicitly — mirrors the existing precedent of keeping comments out of `YamlValue` and into a
separate `YamlDocument` side structure. New file: `src/FSharp.Data.Yaml/YamlBuilders.fs`,
namespace `FSharp.Data.Yaml.Builders`.

### String-path DSL is the primary API, not the `YamlPathStep` DU

`YamlPath` (`YamlPathStep list`, `Key`/`Index`) already exists for `YamlDocument.Comments`, but
forcing C# callers through DU cases is unidiomatic there. Rather than two divergent
per-language surfaces, the DSL (`"services.web.ports[0]"`, dot-separated keys, `[n]` for sequence
indices) is equally terse in F# and C#, parses down to the existing `YamlPath`/`YamlPathStep`
types, and is the one documented in examples. The `YamlPath.Root.Key(...).Index(...)` fluent
builder and the raw `YamlPathStep list` remain available for non-string keys and
programmatically-constructed paths (loops, dynamic segments) — cases the string DSL cannot express.

**Escaping**: a key containing a literal `.` or `[` cannot be written as a bare DSL segment.
Follow the lodash `_.get`/`_.set` convention: bracket-quote such segments, e.g.
`services["a.b"].image`. Malformed paths raise at parse time (`FormatException`-style), not a
silent misparse.

### Auto-vivify missing intermediates, like `mkdir -p`

`SetPath` creates missing intermediate containers rather than throwing, so the API is usable to
build a document from scratch, not just edit an existing one:

- Missing/non-mapping node at a `Key` step → create `YamlValue.Mapping [||]` and recurse in.
- Missing/non-sequence node at an `Index` step → create `YamlValue.Sequence [||]`, padding with
  `YamlValue.Null` up to the index if it's beyond the current length (same as sparse-array-set in
  JS/Ruby).
- Existing **scalar** (non-`Null`) node encountered where a container is needed → overwritten by
  default (`mkdir -p`-like), gated behind `?overwriteScalars: bool = true` so callers can opt into
  a throw instead when silently clobbering a scalar would be a bug in their code.

### RemovePath: no-op on missing, splice (no holes) on sequence removal, no pruning

Deletion is the inverse of the above, but not symmetric everywhere:

- Path doesn't exist (missing key, out-of-range index) → **no-op**, return the value unchanged.
  Consistent with `TryGetProperty` returning `None` rather than throwing; `RemovePath` reads as
  "ensure this isn't there," not an assertion.
- `Key` step → drop the pair from the `Mapping` array, same as `RemoveProperty`.
- `Index` step → **splice** (shift subsequent elements down), never leave a `Null` hole — the only
  place gaps get created is `SetPath`'s pad-on-append, never removal.
- Emptied parent containers (mapping/sequence now `[||]`) are **left in place, not pruned**.
  Cascading auto-prune is a surprising side effect for what looks like a single removal, and an
  explicit empty container round-trips fine.

---

## Layout

```
src/FSharp.Data.Yaml/
  YamlBuilders.fs        YamlPath fluent builder, DSL parser, SetProperty/RemoveProperty,
                          SetPath/RemovePath (string-path and YamlPath overloads)
tests/FSharp.Data.Yaml.Tests/
  BuilderTests.fs         SetProperty/RemoveProperty, DSL parsing + escaping, auto-vivify,
                          padding, overwriteScalars, RemovePath no-op/splice/no-prune
```

Add `YamlBuilders.fs` to the `.fsproj` after `YamlExtensions.fs`. No new package dependencies.

---

## Phases

**1 — `YamlPath` fluent builder.** `YamlPath.Root`, `.Key(string)`, `.Key(YamlValue)`, `.Index(int)`,
`.Steps`, wrapping the existing `YamlPathStep list`. → covered by `BuilderTests`.

**2 — String-path DSL parser.** Parse `"a.b[0].c"`-style strings (dot keys, `[n]` indices,
bracket-quoted escaped keys) into `YamlPath`; malformed input raises. → `BuilderTests`.

**3 — `SetProperty` / `RemoveProperty`.** Shallow `Mapping` array rebuild: replace-if-present else
append; drop-if-present else no-op.

**4 — `SetPath` / `RemovePath` (YamlPath overload).** Recursive rebuild along the path:
auto-vivify + pad-with-`Null` + `overwriteScalars` gate on write; no-op + splice + no-prune on
remove. The string-path overloads are thin wrappers that parse then delegate here.

**5 — Docs.** Add a `YamlBuilders` section to `docs/reference.md` (mirroring the existing
`YamlExtensions` section's table format) and a short example in `README.md`.

---

## Verification

```bash
dotnet build                      # zero warnings (warnaserror in Directory.Build.props)
dotnet test --filter FullyQualifiedName~BuilderTests
dotnet test                       # full suite stays green
```

Cases to exercise directly, beyond typical unit tests:
- DSL round-trip: build a path via `YamlPath.Root.Key(...).Index(...)`, format it back to a DSL
  string (if a `ToString` is added) or at least confirm parsing the equivalent DSL string produces
  an equal `YamlPath`.
- Auto-vivify from `YamlValue.Null` / an empty document all the way to a multi-level nested value.
- `overwriteScalars = false` throws when a scalar is in the way; `true` (default) overwrites it.
- `RemovePath` on a path one level too deep / with an out-of-range index is a no-op, not an
  exception.
- C# smoke test: call `SetProperty`, `SetPath` (string overload), and `SetPath` with
  `YamlPath.Root.Key(...).Index(...)` from a small C# test/console snippet to confirm the surface
  is usable without F#-specific ceremony (optional args, `YamlValue.New*` factories).
