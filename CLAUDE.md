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

## Commands

```bash
dotnet build                                # zero warnings — warnaserror is set in Directory.Build.props
dotnet test                                 # full suite
dotnet test --filter Category=Conformance   # yaml-test-suite subset only
dotnet test --filter FullyQualifiedName~ScalarTests   # a single test class, as an example
scripts/fable-test.sh [js|python|all]       # the shared suite under Fable (see docs/fable.md)
```

## Architecture

```
src/FSharp.Data.YamlValue/
  YamlValue.fs        DU, YamlSaveOptions, YamlPath/YamlNodeComments, YamlParseException, _Print
  YamlPortable.fs      the few non-portable BCL calls, each with a `#if FABLE_COMPILER` branch
  YamlReader.fs        char cursor: line/col tracking, indentation measurement, lookahead;
                        SpanNode/SpanRecorder (position tracking, null on the plain Parse path)
  YamlScalar.fs        core-schema plain-scalar resolution + quoted-scalar unescaping
  YamlParser.fs        recursive-descent parser: flow, then block, then block/multi-line scalars
  YamlPositions.fs     ParseWithPositions / ParseWithMetadata: span + timestamp-text side tables
  YamlEmitter.fs       WriteTo / ToString, quoting rules, block vs. flow style
  YamlExtensions.fs    As* accessors, `?` operator, indexers, GetEnumerator
tests/FSharp.Data.YamlValue.Tests/
  *Tests.fs            one file per concern (Scalar, Flow, Block, BlockScalar, Anchor, Document,
                        Comment, Emitter, RoundTrip, Conformance, Position)
  data/                 curated yaml-test-suite cases (yaml input + expected json output)
tests/FSharp.Data.YamlValue.Fable/
  compiles the SAME test files under Fable through an Xunit shim; Runner/data are generated
  by scripts/fable-gen-runner.fsx (gitignored)
```

Target framework: `netstandard2.0;net8.0`, no `PackageReference`s in the main library — test
project uses xunit + FsCheck.Xunit. Toolchain: .NET SDK 10.0.301 (8.0/9.0 also installed).
Also compiles with Fable 5.21.0 (JS and Python) from the same source; see `docs/fable.md`.

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
  `: ` / ` #` / control chars, `<<` (merge key) and `...` (document end). A `Float` that plain
  text can't round-trip (integral, `0.5`, `-0.0`) is emitted tagged, `!!float 1.0`. This is what
  `RoundTripTests` (an FsCheck property: `Parse(v.ToString(opts)) = v`) exists to catch — treat a round-trip failure as a quoting bug in
  the emitter before assuming the parser is wrong.
- **Anchors/aliases resolve during parsing** into a plain tree with shared (not copied) immutable
  subtrees. A recursive anchor (alias to an anchor still under construction) is a parse error —
  it can't be represented in an immutable tree. The emitter enforces an expansion budget on output
  since a shared-subtree graph can still expand exponentially (billion-laughs). Merge keys
  (`<<: *base`) are on by default.

### Positions, timestamp text and Fable (additive; `Parse` and the DU are untouched)

- **`ParseWithPositions` / `ParseWithMetadata`** return side tables keyed by RFC 6901 JSON Pointer
  over the *expanded* tree: `Span`s, and the original text of every `Timestamp`
  (`YamlParsed.TimestampText`) since the DU only holds a `DateTimeOffset`. Spans are recorded by a
  `SpanRecorder` on the `Cursor` (null on plain `Parse`, so it costs a null check). Every node-
  producing parser site records exactly one node; omitted values get a zero-width node via
  `ensureNode`. Aliases and merged-in keys are positioned at the alias / `<<` site.
  Keep `Parse` behaviour and performance unchanged when touching the parser.
- **A quoted `"<<"` key is literal**, only the plain `<<` key merges (the parser tracks which keys
  were quoted).
- **Fable**: one source, `#if FABLE_COMPILER` branches live in `YamlPortable.fs` (plus the loaders
  and `WriteTo(TextWriter)` compiled out). Avoid in library code: `Uri.IsHexDigit`,
  `Char.ConvertFromUtf32`, `BigInteger.Pow`/`.Zero`/`.One`, `String.CompareOrdinal`, number/date
  parsing overloads taking `NumberStyles`/culture, `DateTimeOffset(DateTime, offset)`, backslashes
  in `///` comments, and a unit `()` arm in a multi-case `match` (Fable Python drops it). Run
  `scripts/fable-test.sh` after parser/scalar/emitter changes.

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
