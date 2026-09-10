# Examples

Runnable F# scripts (`.fsx`) that exercise `FSharp.Data.Yaml`, alongside the `.yml`
files they load. Good for playing with the API in F# Interactive.

## Running

Build the library once, then run any script with `dotnet fsi`:

```bash
dotnet build src/FSharp.Data.Yaml/FSharp.Data.Yaml.fsproj
dotnet fsi examples/quickstart.fsx
```

Each script `#load`s [`load-library.fsx`](load-library.fsx), which `#r`s the built
`net8.0` DLL — rebuild the library after code changes for the scripts to pick them up.

## Scripts

| Script | Demonstrates |
|---|---|
| [`quickstart.fsx`](quickstart.fsx) | `Parse`, the `?` operator, `As*` accessors |
| [`pattern-matching.fsx`](pattern-matching.fsx) | Taking a `YamlValue` apart with `match` |
| [`anchors-and-merge-keys.fsx`](anchors-and-merge-keys.fsx) | `&anchor`/`*alias`/`<<:` merge keys |
| [`aliases.fsx`](aliases.fsx) | Plain `*alias` reuse (no merge), shared subtrees, recursive-anchor errors |
| [`multi-document.fsx`](multi-document.fsx) | `ParseMultiple` over a `---`-separated stream |
| [`writing-yaml.fsx`](writing-yaml.fsx) | Building a `YamlValue` and emitting block/flow YAML |
| [`building-and-editing.fsx`](building-and-editing.fsx) | `YamlBuilders`' `SetProperty`/`SetPath`/`RemovePath` |
| [`string-path-dsl.fsx`](string-path-dsl.fsx) | The `SetPath`/`RemovePath`/`GetPath`/`TryGetPath` string-path DSL and the `YamlPath` fluent builder |
| [`comments.fsx`](comments.fsx) | `YamlDocument.Parse` and comment round-tripping |

## Data files

| File | Used by |
|---|---|
| `person.yml` | `quickstart.fsx`, `pattern-matching.fsx` |
| `docker-compose.yml` | `anchors-and-merge-keys.fsx`, `building-and-editing.fsx` |
| `aliases.yml` | `aliases.fsx` |
| `k8s-stream.yml` | `multi-document.fsx` |
| `commented-config.yml` | `comments.fsx` |
