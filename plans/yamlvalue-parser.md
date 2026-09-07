# YamlValue — a FSharp.Data-style YAML parser

Status: Done

## Context

`/Users/devon.burriss/Documents/ws/dburriss/fsharp-yaml` is empty. The goal is a greenfield F# library that gives YAML the same untyped, ergonomic document API that `FSharp.Data`'s `JsonValue` gives JSON: a discriminated union you can pattern match, `Parse`/`Load`, a `?` dynamic operator, `AsInteger()`-style accessors, and a faithful `ToString()`.

The existing .NET options don't fit this shape. `YamlDotNet`/`SharpYaml` are C# object-mapping libraries; `Legivel` and `YAMLicious` are typed decoder libraries in the Thoth style. None expose a matchable YAML DOM in the `JsonValue` idiom.

Per the decisions made: the parser is **hand-written with zero dependencies** (self-contained, Fable-friendly, no YamlDotNet), supports **anchors/aliases, multi-document streams, non-string keys and comment round-tripping**, uses a **JsonValue-shaped DU plus `Timestamp`**, and ships **parser + emitter + extensions**.

Toolchain confirmed: .NET SDK 10.0.301 (8.0/9.0 also installed).

### One resolved tension

The scalar-model option showed `Mapping of (string * YamlValue)[]`, but *non-string mapping keys* was also selected. The explicit feature choice wins: **`Mapping of (YamlValue * YamlValue)[]`**. `?`, `GetProperty` and `Properties` project the string-keyed subset (matching `YamlValue.String k`), so the common path stays exactly as ergonomic as `JsonValue`. Say the word if you'd rather have string keys and lose `1: a` fidelity.

### Scope reality check

YAML 1.2 is a much larger spec than JSON — indentation-sensitive block context, five scalar styles, anchors, tags, directives. This is a genuinely large build, not a weekend port of `JsonValue.fs`. The plan is therefore phased so each phase ends with something runnable and tested, and phase 3 alone already parses all valid JSON.

---

## Target shape

```fsharp
namespace FSharp.Data

[<RequireQualifiedAccess>]
[<StructuredFormatDisplay("{_Print}")>]
type YamlValue =
    | String    of string
    | Number    of decimal
    | Float     of float
    | Boolean   of bool
    | Timestamp of DateTimeOffset
    | Mapping   of properties: (YamlValue * YamlValue)[]
    | Sequence  of elements: YamlValue[]
    | Null
```

Usage target:

```fsharp
open FSharp.Data
open FSharp.Data.YamlExtensions

let info = YamlValue.Parse """
name: Tomas          # given name
born: 1985
siblings: [Anna, Petr]
"""
info?name.AsString()          // "Tomas"
info?born.AsInteger()         // 1985
for s in info?siblings do printfn "%s" (s.AsString())
```

---

## Layout

```
fsharp-yaml/
  .gitignore                       (dotnet new gitignore)
  Directory.Build.props            shared TFMs, LangVersion, warnaserror, nullable
  FSharp.Data.Yaml.sln
  README.md
  src/FSharp.Data.Yaml/
    FSharp.Data.Yaml.fsproj        netstandard2.0;net8.0 — no PackageReferences
    YamlValue.fs                   DU, save options, paths, comments, exception
    YamlReader.fs                  char cursor: line/col, indent, lookahead
    YamlScalar.fs                  core-schema plain-scalar resolution
    YamlParser.fs                  recursive-descent block + flow parser
    YamlEmitter.fs                 WriteTo / ToString
    YamlExtensions.fs              As* accessors, `?`, indexers, enumerator
  tests/FSharp.Data.Yaml.Tests/
    FSharp.Data.Yaml.Tests.fsproj  xunit + FsCheck.Xunit
    ScalarTests.fs  FlowTests.fs  BlockTests.fs  BlockScalarTests.fs
    AnchorTests.fs  DocumentTests.fs  CommentTests.fs
    EmitterTests.fs  RoundTripTests.fs  ConformanceTests.fs
    data/                          curated yaml-test-suite cases
```

`git init` first — the directory is not currently a repo.

---

## Design decisions

### Comments live outside the DU

Round-tripping comments must not pollute pattern matching. Comments are carried in a **side table keyed by path**, and the DU stays pure:

```fsharp
type YamlPathStep = Key of YamlValue | Index of int
type YamlPath     = YamlPathStep list                  // root-first

type YamlNodeComments = { Leading: string list; Trailing: string option }

type YamlDocument =
    { Value:      YamlValue
      Comments:   Map<YamlPath, YamlNodeComments>
      Directives: (string * string) list
      Trailing:   string list }                        // comments after the last node
```

- `YamlValue.Parse` → plain `YamlValue`, comments discarded (the common case).
- `YamlDocument.Parse` → value **plus** comments; `doc.ToString()` re-emits them.

This is the only clean way to satisfy both "comment round-tripping" and "a DU that's pleasant to match on".

### Scalar resolution (YAML core schema + timestamp)

Applied to **plain scalars only** — quoted and block scalars are always `String`, and an explicit tag always overrides.

| Pattern | Case |
|---|---|
| `null`, `Null`, `NULL`, `~`, empty | `Null` |
| `true/True/TRUE`, `false/False/FALSE` | `Boolean` |
| `[-+]?[0-9]+`, `0o[0-7]+`, `0x[0-9a-fA-F]+` | `Number` |
| `.inf`, `-.inf`, `.nan` | `Float` |
| other floats | `Number` if exactly decimal-representable, else `Float` |
| `YYYY-MM-DD`, ISO-8601 datetime (`T` or space separator, optional fraction/offset) | `Timestamp` |
| anything else | `String` |

Note the core schema deliberately does **not** treat `yes`/`no`/`on`/`off` as booleans — the classic "Norway problem". These stay `String`.

### Emitter quoting is a correctness concern, not cosmetics

The emitter must quote any string that would resolve back to a different case — `"true"`, `"123"`, `"null"`, `"2024-01-01"`, `""` — plus strings with leading/trailing whitespace, a leading indicator char (`- ? : # & * ! | > ' " % @ \` [ ] { } ,`), or containing `: ` / ` #` / control chars. `RoundTripTests` is the guard: `Parse(v.ToString()) = v` as an FsCheck property over generated `YamlValue`s.

Multi-line strings emit as a `|` literal block scalar where legal, otherwise double-quoted with `\n`.

### Alias safety

Aliases share immutable subtrees rather than copying, so `&a [*a, *a]` costs nothing at parse time. Two guards regardless:
- **Recursive anchors** (an alias to an anchor still being constructed) are a parse error — unrepresentable in an immutable tree.
- **Expansion budget** in the emitter, since a shared-subtree graph can still expand exponentially on output (billion-laughs).

Merge keys (`<<: *base`) are supported and on by default — near-universal in docker-compose/GitLab CI — with a parse option to disable.

---

## Phases

Each phase ends green. Order is chosen so the parser is usable early.

**0 — Scaffold.** `git init`, solution, both projects, `Directory.Build.props`, `.gitignore`, README skeleton. `dotnet build && dotnet test` passes on an empty suite.

**1 — `YamlValue.fs`.** The DU, `YamlSaveOptions` (`[<Flags>]`: `None`, `DisableFormatting`, `Flow`, `SuppressComments`, `ExplicitDocumentMarkers`), the path/comment/document types, `_Print`, and `YamlParseException(message, line, column, snippet)` with a caret-pointing snippet.

**2 — `YamlReader.fs` + `YamlScalar.fs`.** Char cursor with line/column tracking, indentation measurement, lookahead, and comment skipping. Scalar resolution per the table above, plus quoted-scalar unescaping: single-quoted `''`, double-quoted `\x \u \U \0 \a \b \t \n \v \f \r \e \" \\ \N \_ \L \P` and escaped-newline folding. → `ScalarTests`.

**3 — Flow parser.** `{...}`, `[...]`, quoted and plain flow scalars, nesting, trailing commas. Milestone: **every valid JSON document parses**. → `FlowTests`, seeded with JSON cases.

**4 — Block parser.** Block mappings and sequences by indent, arbitrary nesting, compact notation (`- key: v`, `- - a`), explicit `? key` / `: value` pairs, flow nodes embedded in block context, non-string keys. This is the largest phase. → `BlockTests`.

**5 — Block & multi-line scalars.** `|` literal and `>` folded with chomping (`-`/`+`) and explicit indent indicators; multi-line plain and quoted scalar folding rules. → `BlockScalarTests`.

**6 — Anchors, aliases, tags.** `&anchor` / `*alias` with the two guards above; merge keys; core tags (`!!str !!int !!float !!bool !!null !!timestamp !!map !!seq`) overriding resolution, unknown tags falling back to resolution. → `AnchorTests`.

**7 — Documents & directives.** `---` / `...`, `%YAML`, `%TAG`; `ParseMultiple`, `Load(stream | TextReader | uri, ?encoding)`, `AsyncLoad`, `TryParse`. → `DocumentTests`.

**8 — Comment capture.** Attach leading/trailing comments to node paths during parse; populate `YamlDocument`. `YamlValue.Parse` stays comment-free. → `CommentTests`.

**9 — `YamlEmitter.fs`.** `WriteTo(writer, saveOptions, ?indentationSpaces)`, `ToString()` overloads on both `YamlValue` and `YamlDocument`. Block style by default with 2-space indent; `Flow` produces JSON-like compact output. Quoting rules, block-scalar emission for multi-line strings, explicit `? key` for non-scalar keys, comment re-emission from the path map. → `EmitterTests`, `RoundTripTests`.

**10 — `YamlExtensions.fs`.** Mirroring `JsonExtensions`: `AsBoolean`, `AsInteger`, `AsInteger64`, `AsDecimal`, `AsFloat`, `AsString`, `AsDateTime`, `AsDateTimeOffset`, `AsTimeSpan`, `AsGuid`, `AsArray`, `TryGetProperty`, `GetProperty`, `Properties`, `InnerText` — all with the `?cultureInfo` parameter FSharp.Data uses. Plus the `?` operator, `.[string]` / `.[int]` indexers, `GetEnumerator()`, and YAML-friendly aliases `AsSequence` / `AsMapping`.

**11 — Conformance & docs.** Curated cases from [yaml-test-suite](https://github.com/yaml/yaml-test-suite) under `tests/data` as the correctness oracle (each case carries `yaml` + expected `json`); FsCheck round-trip properties; README with the `JsonValue`-parity table and examples.

---

## Verification

```bash
dotnet build                      # zero warnings (warnaserror in Directory.Build.props)
dotnet test                       # full suite
dotnet test --filter Category=Conformance   # yaml-test-suite subset
```

Three independent correctness signals, not just unit tests:

1. **JSON superset** — YAML 1.2 is a JSON superset, so a corpus of JSON documents must parse to values matching what `JsonValue` produces (structurally, modulo case names). Cheap, high-coverage, available from phase 3.
2. **Round-trip property** — FsCheck generates arbitrary `YamlValue`s; `Parse(v.ToString(opts)) = v` must hold for every `YamlSaveOptions` combination. This is what catches emitter quoting bugs.
3. **yaml-test-suite** — each curated case's `yaml` input must parse to its expected `json` output; `error:`-marked cases must raise `YamlParseException`.

Manual smoke test at the end — parse a real-world file (a `docker-compose.yml`, exercising anchors and merge keys) in `dotnet fsi`, print it, re-parse the output, confirm equality.

---

## Resolved item

**Package name.** `FSharp.Data.Yaml` is taken on NuGet (v1.0.0, ~615 downloads). The *namespace* `FSharp.Data` remains the right home for API familiarity; the shipped package id is `FSharp.Data.YamlValue` (set via `<PackageId>` in `src/FSharp.Data.Yaml/FSharp.Data.Yaml.fsproj`), named after the type it exports. The repo/project/assembly names stay `FSharp.Data.Yaml` — only the published NuGet package id differs.
