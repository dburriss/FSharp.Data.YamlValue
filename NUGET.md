# FSharp.Data.YamlValue

A YAML document API for F#, in the shape of [`FSharp.Data`'s `JsonValue`](https://fsprojects.github.io/FSharp.Data/library/JsonValue.html).

Parse YAML into a discriminated union you can pattern match, navigate with the `?` dynamic
operator, read with `AsInteger()`-style accessors, and write back out — including comments.
Hand-written parser, zero dependencies, `netstandard2.0`/`net8.0`, AOT-compatible.

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

## Editing with `SetPath`/`GetPath`

`YamlBuilders` adds an immutable edit API with a string-path DSL, beyond what `JsonValue` offers:

```fsharp
open FSharp.Data.YamlValueBuilders

let doc = YamlValue.Parse """
name: myapp
services:
  web:
    image: nginx
"""

let updated = doc.SetPath("services.web.image", YamlValue.String "nginx:1.27")
updated.TryGetPath("services.web.image")   // Some (YamlValue.String "nginx:1.27")
```

## Learn more

- Full README, examples, and the `JsonValue`-parity table: [github.com/dburriss/FSharp.Data.YamlValue](https://github.com/dburriss/FSharp.Data.YamlValue)
- API reference: [`docs/reference.md`](https://github.com/dburriss/FSharp.Data.YamlValue/blob/main/docs/reference.md)
- Runnable example scripts: [`examples/`](https://github.com/dburriss/FSharp.Data.YamlValue/tree/main/examples)

## License

MIT
