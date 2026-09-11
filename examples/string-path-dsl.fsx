#load "load-library.fsx"
open FSharp.Data
open FSharp.Data.YamlValueBuilders
open FSharp.Data.YamlExtensions

// The string-path DSL used by SetPath/RemovePath: dot-separated segments address
// mapping keys, [n] addresses a sequence index, and a segment containing a literal
// '.', '[' or ']' is bracket-quoted, e.g. services["a.b"].image.

let doc = YamlValue.Parse """
name: myapp
services:
  web:
    image: nginx
    ports: [80, 443]
"""

// Plain dotted path + sequence index.
let withNewImage = doc.SetPath("services.web.image", YamlValue.String "nginx:1.27")
printfn "image: %s" (withNewImage?services?web?image.AsString())

// Auto-vivification: missing intermediate mappings/sequences are created (`mkdir -p`
// style), and a sequence is padded with Null when the index is beyond its length.
let vivified = doc.SetPath("services.cache.ports[2]", YamlValue.Number 6379M)
printfn "auto-vivified: %s" (vivified.ToString(YamlSaveOptions.Flow))

// A key containing a literal '.' needs bracket-quoting so it isn't read as a segment
// separator.
let dotted =
    (YamlValue.Parse "config: {}").SetPath("""config["a.b"]""", YamlValue.String "value")
printfn "dotted key: %s" (dotted.ToString(YamlSaveOptions.Flow))

// overwriteScalars defaults to true - a scalar in the way of a container step gets
// overwritten. Pass false to raise instead of silently replacing data.
let overwritten = doc.SetPath("services.web.image.tag", YamlValue.String "1.27")
printfn "overwritten scalar: %s" (overwritten.ToString(YamlSaveOptions.Flow))

try
    doc.SetPath("services.web.image.tag", YamlValue.String "1.27", overwriteScalars = false)
    |> ignore
    printfn "unexpected: should have raised"
with ex ->
    printfn "raised as expected: %s" ex.Message

// RemovePath splices sequence elements out rather than leaving a Null hole.
let onePort = doc.RemovePath("services.web.ports[0]")
printfn "remaining ports: %s" (onePort?services?web?ports.ToString(YamlSaveOptions.Flow))

// RemovePath is a no-op when the path doesn't exist.
let noop = doc.RemovePath("services.web.missing.deeply.nested")
printfn "no-op removal equal: %b" (noop = doc)

// The YamlPath fluent builder is the non-string-key escape hatch the DSL can't reach -
// useful from C#, where constructing YamlValue keys directly is unidiomatic.
let path = YamlPath.Root.Key("services").Key("web").Key("ports").Index(0)
let viaPath = doc.SetPath(path, YamlValue.String "8080")
printfn "via YamlPath: %s" (viaPath?services?web?ports.ToString(YamlSaveOptions.Flow))

// YamlPath.Key(YamlValue) is what lets you address a non-string mapping key at all.
let numericKeyed = YamlValue.Parse "1: one\n2: two"
let updated = numericKeyed.SetPath(YamlPath.Root.Key(YamlValue.Number 1M), YamlValue.String "uno")
printfn "non-string key updated: %s" (updated.ToString(YamlSaveOptions.Flow))

// GetPath/TryGetPath read the same string-path DSL or YamlPath back out, mirroring
// SetPath/RemovePath. GetPath raises on a missing path; TryGetPath returns None.
printfn "get: %s" (doc.GetPath("services.web.image").AsString())
printfn "get via YamlPath: %s" (doc.GetPath(path).AsString())

printfn "try get present: %b" (doc.TryGetPath("services.web.ports[1]") |> Option.isSome)
printfn "try get missing: %b" (doc.TryGetPath("services.web.missing") |> Option.isSome)

try
    doc.GetPath("services.web.missing") |> ignore
    printfn "unexpected: should have raised"
with ex ->
    printfn "GetPath raised as expected: %s" ex.Message
