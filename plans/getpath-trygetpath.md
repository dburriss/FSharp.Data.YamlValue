# Add `GetPath` / `TryGetPath` accepting `YamlPath` or the string DSL

## Context

`YamlBuilders.fs` already gives `YamlValue` path-addressed *writes* — `SetPath`/`RemovePath`,
each with a `YamlPath` overload and a string-DSL overload (`"services.web.ports[0]"`) parsed by
the internal `YamlPathDsl.parse`. There's no path-addressed *read* yet: `YamlExtensions.fs` only
has single-hop lookups (`TryGetProperty`/`GetProperty` for a mapping key, the `Item` indexers,
`?`). To read a nested value today you either chain several single-hop calls or don't have an
option-returning way to do it at all. This adds `GetPath`/`TryGetPath`, mirroring `SetPath`'s two
overloads and `TryGetProperty`/`GetProperty`'s `Try*`-returns-option / non-`Try*`-throws
convention, so callers can do `doc.TryGetPath("services.web.ports[0]")` or build a path with the
fluent `YamlPath` builder.

## Design

Add four new members to the existing `YamlValue with` block in
`src/FSharp.Data.Yaml/YamlBuilders.fs` (right after `RemovePath`, before the closing of the
`type YamlValue with` block), reusing `YamlPath` / `YamlPathDsl.parse` already defined earlier in
that file:

```fsharp
/// Reads the value at `path`. Returns `None` if any step of `path` does not exist (a missing
/// mapping key, an out-of-range sequence index, or descending into a scalar/`Null`).
member this.TryGetPath(path: YamlPath) : YamlValue option =
    let rec go (steps: YamlPathStep list) (node: YamlValue) : YamlValue option =
        match steps with
        | [] -> Some node
        | Key k :: rest ->
            match node with
            | YamlValue.Mapping properties ->
                properties |> Array.tryFind (fun (pk, _) -> pk = k) |> Option.bind (fun (_, v) -> go rest v)
            | _ -> None
        | Index idx :: rest ->
            match node with
            | YamlValue.Sequence es when idx >= 0 && idx < es.Length -> go rest es.[idx]
            | _ -> None
    go path.Steps this

/// Reads the value at the string-path DSL location `path` (e.g. `"services.web.ports[0]"`).
/// Returns `None` under the same conditions as the `YamlPath` overload.
member this.TryGetPath(path: string) : YamlValue option =
    this.TryGetPath(YamlPath.OfSteps(YamlPathDsl.parse path))

/// Reads the value at `path`, raising if any step does not exist. See `TryGetPath` for the
/// `None`/missing conditions this instead surfaces as an exception.
member this.GetPath(path: YamlPath) : YamlValue =
    match this.TryGetPath(path) with
    | Some v -> v
    | None -> failwithf "YamlValue.GetPath: path not found: %A" path.Steps

/// Reads the value at the string-path DSL location `path`, raising if not found.
member this.GetPath(path: string) : YamlValue =
    match this.TryGetPath(path) with
    | Some v -> v
    | None -> failwithf "YamlValue.GetPath: path not found: %s" path
```

Notes matching existing conventions:
- Mirrors `SetPath`'s no-`overwriteScalars`-equivalent-needed read path: descending into a scalar
  or `Null` simply yields `None`/failure, no special-casing needed.
- `GetPath`'s error uses `failwithf` like `SetProperty`/`SetPath` do elsewhere in this file (not a
  custom exception type — consistent with the rest of `YamlBuilders.fs`).
- No new module/file needed — everything reuses `YamlPath`, `YamlPathStep`, `YamlPathDsl.parse`
  already private/internal to `YamlBuilders.fs`.

## Files to change

- `src/FSharp.Data.Yaml/YamlBuilders.fs` — add the four members above.
- `tests/FSharp.Data.Yaml.Tests/BuilderTests.fs` — add a `GetPath`/`TryGetPath` test section
  alongside the existing `SetPath`/`RemovePath` tests (lines ~145–258), covering: found nested
  key, found nested index, missing key → `None`, out-of-range index → `None`, descending through
  a scalar → `None`, string-DSL parity with `YamlPath` builder, `GetPath` throwing on missing
  path, malformed DSL string still raises `FormatException` (already covered by `YamlPathDsl.parse`
  itself, just confirm `GetPath("bad[")` surfaces it the same way `SetPath` does).
- `docs/reference.md` — extend the `YamlBuilders` module table (lines ~235–290) with
  `GetPath`/`TryGetPath` rows next to `SetPath`/`RemovePath`, same string-DSL cross-reference.
- `README.md` — optionally add one line to the existing "SetProperty/SetPath/..." pitch
  (lines ~219–243) mentioning `GetPath`/`TryGetPath`, matching the existing example style.

## Verification

- `dotnet build` — zero warnings (warnaserror is set).
- `dotnet test --filter FullyQualifiedName~BuilderTests` — new tests green, existing
  `SetPath`/`RemovePath` tests unaffected.
- `dotnet test` — full suite still green.
