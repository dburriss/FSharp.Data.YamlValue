# FSharp.Data.Yaml

A YAML document API for F#, in the shape of [`FSharp.Data`'s `JsonValue`](https://fsprojects.github.io/FSharp.Data/library/JsonValue.html).

Parse YAML into a discriminated union you can pattern match, navigate with the `?` dynamic
operator, read with `AsInteger()`-style accessors, and write back out — including comments.

**Zero dependencies.** The parser is hand-written, so the package pulls in nothing but FSharp.Core.

> **Status: in development.** The API below is the design being built to; see
> [`plans/yamlvalue-parser.md`](plans/yamlvalue-parser.md) for the phased plan. Sections marked
> _(planned)_ are not implemented yet.

## Why

The existing .NET options solve a different problem. `YamlDotNet` and `SharpYaml` are C#
object-mapping libraries; `Legivel` and `YAMLicious` are typed decoder libraries in the Thoth
style. All of them want you to know your schema up front.

This library gives you the *untyped* document — the thing you reach for when you're exploring a
config file, munging someone else's `docker-compose.yml`, or writing a tool that has to handle
YAML it has never seen. Exactly what `JsonValue` does for JSON.

## Install

```sh
dotnet add package FSharp.Data.Yaml
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

config.ToString()
// name: web
// port: 8080
// tags:
//   - http
//   - public

config.ToString(YamlSaveOptions.Flow)
// {name: web, port: 8080, tags: [http, public]}
```

The emitter quotes any string that would otherwise read back as a different type — `"true"`,
`"8080"`, `"null"`, `"2024-01-30"`, the empty string — so `Parse(v.ToString()) = v` always holds.

### Comments _(planned)_

Comments are deliberately kept out of `YamlValue` so that pattern matching stays clean. When you
need them, parse a `YamlDocument` instead — it pairs the value with a path-keyed comment table.

```fsharp
let doc = YamlDocument.Parse """
# Service configuration
name: web     # the public name
port: 8080
"""

doc.Value?name.AsString()   // "web"
doc.ToString()              // round-trips, comments and all
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

## Differences from `JsonValue`

| `JsonValue` | `YamlValue` | Note |
|---|---|---|
| `Record of (string * JsonValue)[]` | `Mapping of (YamlValue * YamlValue)[]` | YAML permits non-string keys |
| `Array` | `Sequence` | YAML's term |
| — | `Timestamp of DateTimeOffset` | native YAML scalar type |
| `JsonSaveOptions` | `YamlSaveOptions` | block vs. flow style, comment suppression |
| `Request` / `RequestAsync` | — | omitted; would require an HTTP dependency |
| — | `YamlDocument` | comments, directives, multi-document streams |

Everything else — `Parse`, `TryParse`, `ParseMultiple`, `Load`, `AsyncLoad`, `WriteTo`, the `?`
operator and the whole `As*` accessor family — matches `JsonValue` name for name.

## Documentation

Full API reference: [`docs/reference.md`](docs/reference.md).

## License

MIT
