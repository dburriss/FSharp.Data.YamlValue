# FSharp.Data.Yaml

A YAML document API for F#, in the shape of [`FSharp.Data`'s `JsonValue`](https://fsprojects.github.io/FSharp.Data/library/JsonValue.html).

Parse YAML into a discriminated union you can pattern match, navigate with the `?` dynamic
operator, read with `AsInteger()`-style accessors, and write back out — including comments.

**Zero dependencies.** The parser is hand-written, so the package pulls in nothing but FSharp.Core.

> **Status: feature-complete.** All phases of [`plans/yamlvalue-parser.md`](plans/yamlvalue-parser.md)
> are implemented — flow and block parsing, all scalar types, block/multi-line scalars,
> anchors/aliases/merge keys, multi-document streams and directives, comment capture via
> `YamlDocument`, the emitter, and the `As*`/`?` extension API. Every example below runs against
> the current implementation.

## Supported features

- **Flow and block YAML**, arbitrarily nested — `{...}`/`[...]` flow collections and indentation-based
  block mappings/sequences, including compact notation (`- key: value`) and explicit `? key` / `: value`
  pairs.
- **All YAML core-schema scalar types** plus `Timestamp` — strings, integers (decimal/octal/hex),
  floats (including `.inf`/`-.inf`/`.nan`), booleans, null, and ISO-8601 dates/date-times — with the
  Norway problem (`yes`/`no`/`on`/`off`) deliberately left as strings.
- **Block and multi-line scalars** — `|` literal and `>` folded styles, with `-`/`+` chomping and
  explicit indent indicators.
- **Anchors, aliases and merge keys** (`&anchor`, `*alias`, `<<: *anchor`), resolved during parsing
  into a plain tree with shared (not copied) subtrees. Recursive anchors are a parse error.
- **Multi-document streams** (`---`/`...`) and `%YAML`/`%TAG` directives, via `ParseMultiple` and
  `YamlDocument`.
- **Comment round-tripping** through the separate `YamlDocument` type, which pairs a `YamlValue`
  with a path-keyed comment table — `YamlValue.Parse` itself stays comment-free.
- **Round-tripping emission** — `ToString`/`WriteTo` in block or flow style, with quoting rules
  that guarantee `Parse(v.ToString(YamlSaveOptions.None)) = v` (the property `RoundTripTests` checks
  via FsCheck).

## Why

The existing .NET options solve a different problem. `YamlDotNet` and `SharpYaml` are C#
object-mapping libraries; `Legivel` and `YAMLicious` are typed decoder libraries in the Thoth
style. All of them want you to know your schema up front.

This library gives you the *untyped* document — the thing you reach for when you're exploring a
config file, munging someone else's `docker-compose.yml`, or writing a tool that has to handle
YAML it has never seen. Exactly what `JsonValue` does for JSON.

## Install

```sh
dotnet add package FSharp.Data.YamlValue
```

## Quick start

```fsharp
open FSharp.Data
open FSharp.Data.YamlExtensions

let info =
    YamlValue.Parse """
    name: Tomas          # given name
    born: 1985
    siblings:
      - Anna
      - Petr
    """

info?name.AsString()     // "Tomas"
info?born.AsInteger()    // 1985

for sibling in info?siblings do
    printfn "%s" (sibling.AsString())
```

## Examples

### Pattern matching

The whole point of the DU — you can take the document apart directly.

```fsharp
let rec describe value =
    match value with
    | YamlValue.String s    -> sprintf "string %s" s
    | YamlValue.Number n    -> sprintf "number %M" n
    | YamlValue.Float f     -> sprintf "float %f" f
    | YamlValue.Boolean b   -> sprintf "boolean %b" b
    | YamlValue.Timestamp t -> sprintf "timestamp %O" t
    | YamlValue.Null        -> "null"
    | YamlValue.Sequence xs -> xs |> Array.map describe |> String.concat ", "
    | YamlValue.Mapping ps  ->
        ps
        |> Array.map (fun (k, v) -> sprintf "%s = %s" (describe k) (describe v))
        |> String.concat "; "
```

### Scalar types

Plain scalars are resolved using the YAML core schema. Quoted and block scalars are always
strings, so you can always force a string by quoting it.

```fsharp
YamlValue.Parse "port: 8080"        // Mapping [| String "port", Number 8080M |]
YamlValue.Parse "port: '8080'"      // Mapping [| String "port", String "8080"  |]
YamlValue.Parse "debug: true"       // Boolean true
YamlValue.Parse "country: no"       // String "no"      <- not a boolean, see below
YamlValue.Parse "released: 2024-01-30"  // Timestamp 2024-01-30T00:00:00+00:00
YamlValue.Parse "missing: ~"        // Null
```

The core schema deliberately does **not** treat `yes`/`no`/`on`/`off` as booleans. This is the
so-called *Norway problem* — under YAML 1.1, the country code `NO` silently parsed as `false`.
Only `true`/`false` (and their capitalised forms) are booleans here.

### Non-string keys

YAML allows any node as a mapping key, so `Mapping` holds `(YamlValue * YamlValue)[]`:

```fsharp
YamlValue.Parse "1: one"
// Mapping [| Number 1M, String "one" |]

YamlValue.Parse "? [a, b]\n: pair"
// Mapping [| Sequence [| String "a"; String "b" |], String "pair" |]
```

The `?` operator, `GetProperty` and `Properties` all work on the string-keyed subset, so the
common case stays as convenient as `JsonValue`. Use `Entries` when you need every key.

### Anchors, aliases and merge keys

Anchors are resolved during parsing, so what you get back is a plain tree.

```fsharp
let compose =
    YamlValue.Parse """
    defaults: &defaults
      restart: always
      logging: json-file

    services:
      web:
        <<: *defaults
        image: nginx
      db:
        <<: *defaults
        image: postgres
    """

compose?services?web?restart.AsString()   // "always"  (merged from *defaults)
compose?services?web?image.AsString()     // "nginx"
```

Merge keys (`<<:`) are on by default. A recursive anchor is a parse error, since it cannot be
represented as an immutable tree.

### Multi-document streams

```fsharp
let docs = YamlValue.ParseMultiple """
kind: Service
name: web
---
kind: Deployment
name: web
"""

docs |> Seq.map (fun d -> d?kind.AsString())   // seq ["Service"; "Deployment"]
```

### Loading

```fsharp
YamlValue.Load "config.yml"
YamlValue.Load "https://example.org/config.yml"
YamlValue.Load(File.OpenRead "config.yml")

async {
    let! config = YamlValue.AsyncLoad "https://example.org/config.yml"
    return config?version.AsString()
}
```

### Writing YAML

```fsharp
let config =
    YamlValue.Mapping [|
        YamlValue.String "name",  YamlValue.String "web"
        YamlValue.String "port",  YamlValue.Number 8080M
        YamlValue.String "tags",  YamlValue.Sequence [| YamlValue.String "http"
                                                        YamlValue.String "public" |]
    |]

config.ToString(YamlSaveOptions.None)
// name: web
// port: 8080
// tags:
//   - http
//   - public

config.ToString(YamlSaveOptions.Flow)
// {name: web, port: 8080, tags: [http, public]}
```

> **A `ToString()` gotcha.** F#/.NET type extensions can't override the virtual `ToString()`
> declared on the original type, so a genuinely bare `x.ToString()` call (no arguments at all)
> resolves to the compiler-generated structural formatter from `[<StructuredFormatDisplay>]`
> (handy in F# Interactive and debugger views: `{name: "web"; port: 8080}`), not the YAML emitter.
> Pass an argument — `x.ToString(YamlSaveOptions.None)` or `x.ToString(2)` — to reliably reach the
> real emitter. The same applies to `YamlDocument.ToString()`.

The emitter quotes any string that would otherwise read back as a different type — `"true"`,
`"8080"`, `"null"`, `"2024-01-30"`, the empty string — so
`Parse(v.ToString(YamlSaveOptions.None)) = v` always holds.

### Building and editing

`YamlValue` has no write API of its own — same as `JsonValue`, it's immutable. `YamlBuilders`
adds an opt-in `SetProperty`/`SetPath`/`RemoveProperty`/`RemovePath` API that returns a new
`YamlValue` rather than mutating in place, with a string-path DSL for nested edits:

```fsharp
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
        .RemovePath("services.web.ports[0]")
```

`SetPath` auto-vivifies missing intermediate mappings/sequences (`mkdir -p`-style) and pads a
sequence with `YamlValue.Null` when the index is beyond its current length. `RemovePath` is a
no-op when the path doesn't exist, and splices sequence elements out rather than leaving a
`Null` hole. See `docs/reference.md` for the full API, including the `YamlPath` fluent builder
for non-string keys — useful from C#, where DU cases are less natural to construct directly.

### Comments

Comments are deliberately kept out of `YamlValue` so that pattern matching stays clean. When you
need them, parse a `YamlDocument` instead — it pairs the value with a path-keyed comment table.

```fsharp
let doc = YamlDocument.Parse """
# Service configuration
name: web     # the public name
port: 8080
"""

doc.Value?name.AsString()                  // "web"
doc.ToString(YamlSaveOptions.None)         // round-trips, comments and all
```

`YamlValue.Parse` discards comments; `YamlDocument.Parse` preserves them.

## How YAML maps onto `YamlValue`

| YAML | `YamlValue` |
|---|---|
| `null`, `Null`, `NULL`, `~`, empty | `Null` |
| `true`, `True`, `TRUE`, `false`, `False`, `FALSE` | `Boolean` |
| `123`, `-4`, `0o17`, `0xFF` | `Number` |
| `1.5`, `6.02e23` | `Number` if exactly representable as `decimal`, else `Float` |
| `.inf`, `-.inf`, `.nan` | `Float` |
| `2024-01-30`, `2024-01-30T09:15:00Z` | `Timestamp` |
| `'quoted'`, `"quoted"`, `|` and `>` blocks | `String` |
| anything else plain | `String` |
| `key: value` mappings, flow `{}` | `Mapping` |
| `- item` sequences, flow `[]` | `Sequence` |

Explicit tags (`!!str`, `!!int`, `!!bool`, …) override resolution.

## `JsonValue` parity

`YamlValue` is deliberately shaped like [`FSharp.Data.JsonValue`](https://fsprojects.github.io/FSharp.Data/library/JsonValue.html)
— if you know one, you already mostly know the other. This table maps every `JsonValue` concept
to its `YamlValue` equivalent, plus the YAML-only additions that don't have a `JsonValue`
counterpart.

| `JsonValue` | `YamlValue` | Note |
|---|---|---|
| `Parse(text)` | `Parse(text)` | Same name and shape. Comments are discarded; use `YamlDocument.Parse` to keep them. |
| `TryParse(text)` | `TryParse(text)` | Same name and shape. |
| — | `ParseMultiple(text)` | YAML-only: splits a `---`-separated multi-document stream. |
| `Load(stream \| reader \| uri)` | `Load(stream \| reader \| uri)` | Same name and shape. |
| `AsyncLoad(uri)` | `AsyncLoad(uri)` | Same name and shape. |
| `Request` / `RequestAsync` | — | Omitted; would require an HTTP dependency. |
| `String of string` | `String of string` | Same. |
| `Number of decimal` | `Number of decimal` | Same. |
| `Float of float` | `Float of float` | Same; also used for `.inf`/`-.inf`/`.nan`. |
| `Boolean of bool` | `Boolean of bool` | Same, but core-schema only — `yes`/`no`/`on`/`off` stay strings. |
| `Null` | `Null` | Same. |
| `Record of (string * JsonValue)[]` | `Mapping of (YamlValue * YamlValue)[]` | YAML permits non-string keys, so `Mapping` keys are `YamlValue`, not `string`. |
| `Array of JsonValue[]` | `Sequence of YamlValue[]` | YAML's term for the same shape. |
| — | `Timestamp of DateTimeOffset` | YAML-only: a native scalar type for ISO-8601 dates/date-times. |
| `( ? )` operator | `( ? )` operator | Same — `value?propertyName`, string-keyed only. |
| `AsBoolean`, `AsInteger`, `AsInteger64`, `AsDecimal`, `AsFloat`, `AsString`, `AsDateTime`, `AsGuid` | Same names | Identical signatures, including the `?cultureInfo` parameter. |
| `AsArray` | `AsArray` (alias `AsSequence`) | `AsSequence` is a YAML-flavoured alias for the same accessor. |
| `Properties` | `Properties` | String-keyed pairs only, same as `JsonValue`. |
| — | `Entries` | YAML-only: every mapping pair, including non-string keys. |
| `GetProperty`, `TryGetProperty` | Same names | Same signatures. |
| `InnerText` | `InnerText` | Same. |
| `ToString(?saveOptions)` | `ToString(saveOptions, ?indentationSpaces)` / `ToString(?indentationSpaces)` | Same idea; YAML adds an indentation parameter. See the `ToString()` gotcha above. |
| `WriteTo(writer, saveOptions)` | `WriteTo(writer, saveOptions, ?indentationSpaces)` | Same idea. |
| `JsonSaveOptions` | `YamlSaveOptions` | Adds `Flow` (JSON-style output) and `ExplicitDocumentMarkers` (`---`/`...`) to `JsonValue`'s formatting flags. |
| — | `YamlDocument` | YAML-only: pairs a `YamlValue` with comments, directives and multi-document support that `JsonValue` has no equivalent for. |
| — | Anchors / aliases (`&anchor`, `*alias`) | YAML-only: resolved during parsing into a shared (not copied) immutable tree. |
| — | Merge keys (`<<: *anchor`) | YAML-only: on by default. |

## Differences from `JsonValue`

The same information as above, condensed to the parts that actually differ:

| `JsonValue` | `YamlValue` | Note |
|---|---|---|
| `Record of (string * JsonValue)[]` | `Mapping of (YamlValue * YamlValue)[]` | YAML permits non-string keys |
| `Array` | `Sequence` | YAML's term |
| — | `Timestamp of DateTimeOffset` | native YAML scalar type |
| `JsonSaveOptions` | `YamlSaveOptions` | block vs. flow style, comment suppression |
| `Request` / `RequestAsync` | — | omitted; would require an HTTP dependency |
| — | `YamlDocument` | comments, directives, multi-document streams, anchors/aliases, merge keys |

Everything else — `Parse`, `TryParse`, `ParseMultiple`, `Load`, `AsyncLoad`, `WriteTo`, the `?`
operator and the whole `As*` accessor family — matches `JsonValue` name for name.

## Documentation

Full API reference: [`docs/reference.md`](docs/reference.md).

## License

MIT
