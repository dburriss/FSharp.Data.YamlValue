# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Status

Feature parity with FSharp.Data.Json with additional DSL for GetPath and SetPath.

## What this project is

`FSharp.Data.YamlValue` gives YAML the same untyped, ergonomic document API that `FSharp.Data`'s
`JsonValue` gives JSON: a discriminated union (`YamlValue`) you pattern match on, `Parse`/`Load`,
a `?` dynamic operator, `AsInteger()`-style accessors, and a round-tripping `ToString()`. The
parser is **hand-written with zero dependencies** (no YamlDotNet, Fable-friendly) and additionally
supports anchors/aliases, multi-document streams, non-string mapping keys, and comment
round-tripping via a separate `YamlDocument` type.

Full target API: `docs/reference.md`. Usage examples and the YAML→`YamlValue` mapping table:
`README.md`.

## Planned commands (once scaffolded per Phase 0)

```bash
dotnet build                                # zero warnings — warnaserror is set in Directory.Build.props
dotnet test                                 # full suite
dotnet test --filter Category=Conformance   # yaml-test-suite subset only
dotnet test --filter FullyQualifiedName~ScalarTests   # a single test class, as an example
```

## Architecture (per the plan)

```
src/FSharp.Data.YamlValue/
  YamlValue.fs        DU, YamlSaveOptions, YamlPath/YamlNodeComments, YamlParseException, _Print
  YamlReader.fs        char cursor: line/col tracking, indentation measurement, lookahead
  YamlScalar.fs        core-schema plain-scalar resolution + quoted-scalar unescaping
  YamlParser.fs        recursive-descent parser: flow, then block, then block/multi-line scalars
  YamlEmitter.fs       WriteTo / ToString, quoting rules, block vs. flow style
  YamlExtensions.fs    As* accessors, `?` operator, indexers, GetEnumerator
tests/FSharp.Data.YamlValue.Tests/
  *Tests.fs            one file per concern (Scalar, Flow, Block, BlockScalar, Anchor, Document,
                        Comment, Emitter, RoundTrip, Conformance)
  data/                 curated yaml-test-suite cases (yaml input + expected json output)
```

Target framework: `netstandard2.0;net8.0`, no `PackageReference`s in the main library — test
project uses xunit + FsCheck.Xunit. Toolchain: .NET SDK 10.0.301 (8.0/9.0 also installed).

### Core design decisions (see plan for full rationale)

- **`Mapping of (YamlValue * YamlValue)[]`, not `(string * YamlValue)[]`** — YAML permits any
  node as a mapping key. `?`, `GetProperty`, and `Properties` project only the string-keyed subset
  (matching `YamlValue.String k`); `Entries` exposes every pair including non-string keys.
- **Comments live outside the DU.** `YamlValue.Parse` discards comments and stays pattern-match
  clean. `YamlDocument` pairs a `YamlValue` with a `Map<YamlPath, YamlNodeComments>` side table
  keyed by path from the root, plus directives and trailing comments. Never add a comment field
  to `YamlValue` itself.
- **Scalar resolution** applies to *plain* scalars only — quoted and block scalars are always
  `String`, and an explicit tag (`!!str`, `!!int`, etc.) always overrides resolution. The core
  schema deliberately excludes `yes`/`no`/`on`/`off` as booleans (the "Norway problem"); only
  `true`/`false` and capitalized forms are `Boolean`. Numbers that don't fit exactly in `decimal`
  become `Float` instead of losing precision.
- **Emitter quoting is a correctness concern, not cosmetics.** Any string that would resolve back
  to a different case on reparse (`"true"`, `"123"`, `"null"`, `"2024-01-30"`, `""`) must be
  quoted, as must strings with leading/trailing whitespace, a leading indicator character, or
  `: ` / ` #` / control chars. This is what `RoundTripTests` (an FsCheck property:
  `Parse(v.ToString(opts)) = v`) exists to catch — treat a round-trip failure as a quoting bug in
  the emitter before assuming the parser is wrong.
- **Anchors/aliases resolve during parsing** into a plain tree with shared (not copied) immutable
  subtrees. A recursive anchor (alias to an anchor still under construction) is a parse error —
  it can't be represented in an immutable tree. The emitter enforces an expansion budget on output
  since a shared-subtree graph can still expand exponentially (billion-laughs). Merge keys
  (`<<: *base`) are on by default.

### Three independent correctness signals (not just unit tests)

1. **JSON superset** — since YAML 1.2 is a JSON superset, a JSON corpus must parse to values
   structurally matching `JsonValue` (available from Phase 3 onward).
2. **Round-trip property** — FsCheck-generated `YamlValue`s must satisfy
   `Parse(v.ToString(opts)) = v` for every `YamlSaveOptions` combination.
3. **yaml-test-suite conformance** — curated cases in `tests/.../data`, each with a `yaml` input
   and expected `json` output (or an `error:` marker expecting `YamlParseException`).

### Build order

The plan is phased so each phase leaves the suite green: DU/exception types → reader/scalar
resolution → flow parser (all valid JSON parses) → block parser (largest phase) → block/multi-line
scalars → anchors/aliases/tags → documents/directives/multi-doc/Load → comment capture → emitter →
extensions → conformance suite + docs. When resuming implementation, check which phase's files
exist to know where to pick up.
