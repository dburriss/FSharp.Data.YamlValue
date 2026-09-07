module FSharp.Data.Yaml.Tests.BuilderTests

open System
open Xunit
open FSharp.Data
open FSharp.Data.YamlExtensions
open FSharp.Data.Yaml.Builders

// ---------------------------------------------------------------------
// YamlPath fluent builder
// ---------------------------------------------------------------------

[<Fact>]
let ``YamlPath.Root has no steps`` () =
    Assert.Empty(YamlPath.Root.Steps)

[<Fact>]
let ``YamlPath builds a Key/Index chain`` () =
    let path = YamlPath.Root.Key("services").Key("web").Index(0)
    Assert.Equal<YamlPathStep list>(
        [ Key(YamlValue.String "services"); Key(YamlValue.String "web"); Index 0 ],
        path.Steps)

[<Fact>]
let ``YamlPath.Key accepts a non-string YamlValue key`` () =
    let path = YamlPath.Root.Key(YamlValue.Number 1M)
    Assert.Equal<YamlPathStep list>([ Key(YamlValue.Number 1M) ], path.Steps)

[<Fact>]
let ``YamlPath.OfSteps wraps a raw step list`` () =
    let steps = [ Key(YamlValue.String "a"); Index 2 ]
    Assert.Equal<YamlPathStep list>(steps, YamlPath.OfSteps(steps).Steps)

// ---------------------------------------------------------------------
// String-path DSL parsing
// ---------------------------------------------------------------------

[<Fact>]
let ``DSL parses dotted keys`` () =
    Assert.Equal<YamlPathStep list>(
        [ Key(YamlValue.String "services"); Key(YamlValue.String "web"); Key(YamlValue.String "image") ],
        YamlPathDsl.parse "services.web.image")

[<Fact>]
let ``DSL parses an index suffix without a dot`` () =
    Assert.Equal<YamlPathStep list>(
        [ Key(YamlValue.String "ports"); Index 0 ],
        YamlPathDsl.parse "ports[0]")

[<Fact>]
let ``DSL parses a leading index`` () =
    Assert.Equal<YamlPathStep list>([ Index 0; Key(YamlValue.String "name") ], YamlPathDsl.parse "[0].name")

[<Fact>]
let ``DSL parses a bracket-quoted key containing a dot`` () =
    Assert.Equal<YamlPathStep list>(
        [ Key(YamlValue.String "services"); Key(YamlValue.String "a.b"); Key(YamlValue.String "image") ],
        YamlPathDsl.parse "services[\"a.b\"].image")

[<Fact>]
let ``DSL unescapes quotes and backslashes in a bracket-quoted key`` () =
    Assert.Equal<YamlPathStep list>(
        [ Key(YamlValue.String "a\"b\\c") ],
        YamlPathDsl.parse "[\"a\\\"b\\\\c\"]")

[<Fact>]
let ``DSL empty string is the root path`` () =
    Assert.Empty(YamlPathDsl.parse "")

[<Fact>]
let ``DSL raises on a leading dot`` () =
    Assert.Throws<FormatException>(fun () -> YamlPathDsl.parse ".a" |> ignore) |> ignore

[<Fact>]
let ``DSL raises on an unterminated bracket`` () =
    Assert.Throws<FormatException>(fun () -> YamlPathDsl.parse "a[0" |> ignore) |> ignore

[<Fact>]
let ``DSL raises on a non-numeric bracket segment`` () =
    Assert.Throws<FormatException>(fun () -> YamlPathDsl.parse "a[x]" |> ignore) |> ignore

[<Fact>]
let ``DSL raises on an unterminated quoted key`` () =
    Assert.Throws<FormatException>(fun () -> YamlPathDsl.parse "[\"a" |> ignore) |> ignore

// ---------------------------------------------------------------------
// SetProperty / RemoveProperty
// ---------------------------------------------------------------------

[<Fact>]
let ``SetProperty replaces an existing key`` () =
    let m = YamlValue.Mapping [| (YamlValue.String "name", YamlValue.String "old") |]
    let updated = m.SetProperty("name", YamlValue.String "new")
    Assert.Equal<(YamlValue * YamlValue)[]>(
        [| (YamlValue.String "name", YamlValue.String "new") |],
        match updated with YamlValue.Mapping ps -> ps | _ -> failwith "expected Mapping")

[<Fact>]
let ``SetProperty appends a missing key`` () =
    let m = YamlValue.Mapping [| (YamlValue.String "a", YamlValue.Number 1M) |]
    let updated = m.SetProperty("b", YamlValue.Number 2M)
    match updated with
    | YamlValue.Mapping ps -> Assert.Equal(2, ps.Length)
    | _ -> failwith "expected Mapping"

[<Fact>]
let ``SetProperty on Null creates a single-entry mapping`` () =
    let updated = YamlValue.Null.SetProperty("a", YamlValue.Number 1M)
    Assert.Equal<(YamlValue * YamlValue)[]>(
        [| (YamlValue.String "a", YamlValue.Number 1M) |],
        match updated with YamlValue.Mapping ps -> ps | _ -> failwith "expected Mapping")

[<Fact>]
let ``SetProperty on a scalar raises`` () =
    Assert.Throws<Exception>(fun () -> YamlValue.String("x").SetProperty("a", YamlValue.Number 1M) |> ignore)
    |> ignore

[<Fact>]
let ``SetProperty does not mutate the original`` () =
    let original = YamlValue.Mapping [| (YamlValue.String "a", YamlValue.Number 1M) |]
    original.SetProperty("a", YamlValue.Number 2M) |> ignore
    Assert.Equal<(YamlValue * YamlValue)[]>(
        [| (YamlValue.String "a", YamlValue.Number 1M) |],
        match original with YamlValue.Mapping ps -> ps | _ -> failwith "expected Mapping")

[<Fact>]
let ``RemoveProperty drops an existing key`` () =
    let m = YamlValue.Mapping [| (YamlValue.String "a", YamlValue.Number 1M); (YamlValue.String "b", YamlValue.Number 2M) |]
    let updated = m.RemoveProperty("a")
    Assert.Equal<(YamlValue * YamlValue)[]>(
        [| (YamlValue.String "b", YamlValue.Number 2M) |],
        match updated with YamlValue.Mapping ps -> ps | _ -> failwith "expected Mapping")

[<Fact>]
let ``RemoveProperty is a no-op when the key is absent`` () =
    let m = YamlValue.Mapping [| (YamlValue.String "a", YamlValue.Number 1M) |]
    Assert.Equal(m, m.RemoveProperty("missing"))

[<Fact>]
let ``RemoveProperty is a no-op on a non-mapping`` () =
    let v = YamlValue.String "x"
    Assert.Equal(v, v.RemoveProperty("a"))

// ---------------------------------------------------------------------
// SetPath — auto-vivify, padding, overwriteScalars
// ---------------------------------------------------------------------

[<Fact>]
let ``SetPath auto-vivifies missing intermediate mappings from Null`` () =
    let updated = YamlValue.Null.SetPath("services.web.image", YamlValue.String "nginx")
    Assert.Equal(YamlValue.String "nginx", updated?services?web?image)

[<Fact>]
let ``SetPath auto-vivifies a sequence and pads with Null`` () =
    let updated = YamlValue.Null.SetPath("[2]", YamlValue.String "x")
    match updated with
    | YamlValue.Sequence es ->
        Assert.Equal(3, es.Length)
        Assert.Equal(YamlValue.Null, es.[0])
        Assert.Equal(YamlValue.Null, es.[1])
        Assert.Equal(YamlValue.String "x", es.[2])
    | _ -> failwith "expected Sequence"

[<Fact>]
let ``SetPath auto-vivifies a nested property holding a padded sequence`` () =
    let updated = YamlValue.Null.SetPath("items[2]", YamlValue.String "x")
    match updated?items with
    | YamlValue.Sequence es ->
        Assert.Equal(3, es.Length)
        Assert.Equal(YamlValue.Null, es.[0])
        Assert.Equal(YamlValue.String "x", es.[2])
    | _ -> failwith "expected Sequence"

[<Fact>]
let ``SetPath on an existing document updates a nested value`` () =
    let doc =
        YamlValue.Parse
            "name: myapp\nservices:\n  web:\n    image: nginx\n    ports: [80, 443]\n"

    let updated = doc.SetPath("services.web.image", YamlValue.String "nginx:1.27")
    Assert.Equal(YamlValue.String "nginx:1.27", updated?services?web?image)
    // sibling keys survive
    Assert.Equal(YamlValue.String "myapp", updated?name)

[<Fact>]
let ``SetPath does not mutate the original document`` () =
    let doc = YamlValue.Parse "a:\n  b: 1\n"
    doc.SetPath("a.b", YamlValue.Number 2M) |> ignore
    Assert.Equal(YamlValue.Number 1M, doc?a?b)

[<Fact>]
let ``SetPath overwrites a scalar in the way by default`` () =
    let doc = YamlValue.Parse "name: myapp\n"
    let updated = doc.SetPath("name.first", YamlValue.String "x")
    Assert.Equal(YamlValue.String "x", updated?name?first)

[<Fact>]
let ``SetPath with overwriteScalars = false raises instead of overwriting a scalar`` () =
    let doc = YamlValue.Parse "name: myapp\n"
    Assert.Throws<Exception>(fun () ->
        doc.SetPath("name.first", YamlValue.String "x", overwriteScalars = false) |> ignore)
    |> ignore

[<Fact>]
let ``SetPath via the YamlPath builder matches the string DSL`` () =
    let doc = YamlValue.Null
    let viaDsl = doc.SetPath("services.web", YamlValue.String "x")
    let viaBuilder = doc.SetPath(YamlPath.Root.Key("services").Key("web"), YamlValue.String "x")
    Assert.Equal(viaDsl, viaBuilder)

[<Fact>]
let ``SetPath at the empty path replaces the whole value`` () =
    let updated = YamlValue.Null.SetPath("", YamlValue.Number 42M)
    Assert.Equal(YamlValue.Number 42M, updated)

// ---------------------------------------------------------------------
// RemovePath — no-op, splice, no pruning
// ---------------------------------------------------------------------

[<Fact>]
let ``RemovePath removes a nested key`` () =
    let doc = YamlValue.Parse "services:\n  web:\n    image: nginx\n"
    let updated = doc.RemovePath("services.web.image")
    match updated?services?web with
    | YamlValue.Mapping ps -> Assert.Empty(ps)
    | _ -> failwith "expected an (empty) Mapping left in place"

[<Fact>]
let ``RemovePath splices a sequence element without leaving a hole`` () =
    let doc = YamlValue.Parse "ports: [80, 443, 8443]\n"
    let updated = doc.RemovePath("ports[1]")
    match updated?ports with
    | YamlValue.Sequence es -> Assert.Equal<YamlValue[]>([| YamlValue.Number 80M; YamlValue.Number 8443M |], es)
    | _ -> failwith "expected Sequence"

[<Fact>]
let ``RemovePath is a no-op when an intermediate key is missing`` () =
    let doc = YamlValue.Parse "a: 1\n"
    Assert.Equal(doc, doc.RemovePath("b.c"))

[<Fact>]
let ``RemovePath is a no-op when the index is out of range`` () =
    let doc = YamlValue.Parse "items: [1, 2]\n"
    Assert.Equal(doc, doc.RemovePath("items[5]"))

[<Fact>]
let ``RemovePath does not prune an emptied parent mapping`` () =
    let doc = YamlValue.Parse "services:\n  web: {}\n"
    let updated = doc.RemovePath("services.web")
    match updated?services with
    | YamlValue.Mapping ps -> Assert.Empty(ps)
    | _ -> failwith "expected an (empty) Mapping left in place"

[<Fact>]
let ``RemovePath does not mutate the original`` () =
    let doc = YamlValue.Parse "a:\n  b: 1\n"
    doc.RemovePath("a.b") |> ignore
    Assert.Equal(YamlValue.Number 1M, doc?a?b)
