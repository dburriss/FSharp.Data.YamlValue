module FSharp.Data.YamlValue.Tests.ConformanceTests

// Curated conformance suite, per Phase 11 of plans/yamlvalue-parser.md — a hand-authored
// substitute for github.com/yaml/yaml-test-suite (no network access available here), covering
// the same spirit: flow/block collections, all scalar types (including the classic edge cases),
// block scalars, anchors/aliases/merge keys, multi-document streams, and deliberately invalid
// input.
//
// Data-file convention (documented here since the task called for picking and noting one):
//   - Paired files, not directory-per-case: `data/<case>.yaml` alongside `data/<case>.json`.
//     Simpler to enumerate and simpler to open side-by-side than a directory per case.
//   - A case with a `.json` sibling is a "JSON-equivalence" case: since YAML 1.2 is a JSON
//     superset and our own scalar resolution is shared code, the `.json` file's content is
//     itself parsed with `YamlValue.Parse` (never a separate hand-written JSON->YamlValue
//     converter) and compared structurally to the `.yaml` file's parse. This only works for
//     cases whose expected shape is itself valid, unquoted-scalar-free-of-ambiguity JSON.
//   - A case with NO `.json` sibling is a "hand-written-expected" case: its expected `YamlValue`
//     is written directly in this file as an explicit `[<Fact>]`. Used for anything JSON can't
//     represent equivalently — timestamps, the Norway-problem strings, octal/hex ints, block
//     scalars, anchors/aliases/merge keys, and multi-document streams. Individual `[<Fact>]`s
//     rather than a generic theory, per the plan's suggestion, because a failure then points
//     straight at the specific conformance concern instead of a shared assertion helper.
//   - A case file named with an `error-` prefix is deliberately invalid YAML: no `.json` sibling,
//     and `YamlValue.Parse` on it must raise `YamlParseException`. Enumerated generically since
//     the only assertion needed is "it throws".
//
// All conformance tests carry `[<Trait("Category", "Conformance")>]` so that
// `dotnet test --filter Category=Conformance` selects exactly this file.

open System
open System.Collections.Generic
open System.IO
open Xunit
open FSharp.Data

/// Directory containing the curated case files, resolved relative to the test assembly's output
/// directory (populated there by the `<None Include="data/**/*" CopyToOutputDirectory=.../>`
/// item in the .fsproj).
let private dataDir = Path.Combine(AppContext.BaseDirectory, "data")

let private readData (fileName: string) : string =
    File.ReadAllText(Path.Combine(dataDir, fileName))

/// Case names (without extension) that have a `.yaml` + `.json` pair, for the JSON-equivalence
/// comparison. Kept as an explicit list (rather than discovered by scanning for pairs) so the
/// list itself documents exactly what's covered, and so a typo'd file name fails loudly instead
/// of silently vanishing from the suite.
let private jsonEquivalenceCases =
    [ "flow-empty-mapping"
      "flow-empty-sequence"
      "flow-nested"
      "flow-mixed"
      "block-mapping-simple"
      "block-sequence-simple"
      "block-nested-mapping-sequence"
      "block-compact-notation"
      "scalar-integers"
      "scalar-negative-numbers"
      "scalar-float-e-notation"
      "scalar-string-various"
      "scalar-null"
      "scalar-boolean-true-false"
      "mixed-realworld-config" ]

type JsonEquivalenceCaseData() =
    interface IEnumerable<obj[]> with
        member _.GetEnumerator() : IEnumerator<obj[]> =
            (jsonEquivalenceCases |> Seq.map (fun name -> [| box name |])).GetEnumerator()
    interface System.Collections.IEnumerable with
        member this.GetEnumerator() : System.Collections.IEnumerator =
            ((this :> IEnumerable<obj[]>).GetEnumerator() :> System.Collections.IEnumerator)

/// Every `data/error-*.yaml` file present on disk, discovered rather than hand-listed since the
/// only assertion applied to each is uniform ("it throws").
type ErrorCaseData() =
    interface IEnumerable<obj[]> with
        member _.GetEnumerator() : IEnumerator<obj[]> =
            (Directory.GetFiles(dataDir, "error-*.yaml")
             |> Seq.map (fun path -> [| box (Path.GetFileName path) |])).GetEnumerator()
    interface System.Collections.IEnumerable with
        member this.GetEnumerator() : System.Collections.IEnumerator =
            ((this :> IEnumerable<obj[]>).GetEnumerator() :> System.Collections.IEnumerator)

// ---------------------------------------------------------------------
// JSON-equivalence cases — flow/block collections, JSON-representable scalars
// ---------------------------------------------------------------------

[<Theory>]
[<Trait("Category", "Conformance")>]
[<ClassData(typeof<JsonEquivalenceCaseData>)>]
let ``yaml case parses to the same value as its JSON oracle`` (caseName: string) =
    let yamlText = readData (caseName + ".yaml")
    let jsonText = readData (caseName + ".json")
    let actual = YamlValue.Parse yamlText
    let expected = YamlValue.Parse jsonText
    Assert.Equal(expected, actual)

// ---------------------------------------------------------------------
// Hand-written-expected cases
// ---------------------------------------------------------------------

[<Fact>]
[<Trait("Category", "Conformance")>]
let ``Norway problem: yes/no/on/off stay strings, never booleans`` () =
    match YamlValue.Parse(readData "scalar-norway-problem.yaml") with
    | YamlValue.Mapping pairs ->
        let get k = pairs |> Array.find (fun (key, _) -> key = YamlValue.String k) |> snd
        Assert.Equal(YamlValue.String "yes", get "a")
        Assert.Equal(YamlValue.String "no", get "b")
        Assert.Equal(YamlValue.String "on", get "c")
        Assert.Equal(YamlValue.String "off", get "d")
        Assert.Equal(YamlValue.String "YES", get "e")
        Assert.Equal(YamlValue.String "NO", get "f")
    | other -> failwithf "Expected a Mapping, got %A" other

[<Fact>]
[<Trait("Category", "Conformance")>]
let ``octal and hex plain scalars resolve to their decimal Number value`` () =
    let expected =
        YamlValue.Mapping [| (YamlValue.String "oct", YamlValue.Number 15M); (YamlValue.String "hex", YamlValue.Number 255M) |]
    Assert.Equal(expected, YamlValue.Parse(readData "scalar-octal-hex.yaml"))

[<Fact>]
[<Trait("Category", "Conformance")>]
let ``a bare date resolves to a Timestamp at midnight UTC`` () =
    let expected =
        YamlValue.Mapping
            [| (YamlValue.String "released", YamlValue.Timestamp(DateTimeOffset(2024, 1, 30, 0, 0, 0, TimeSpan.Zero))) |]
    Assert.Equal(expected, YamlValue.Parse(readData "scalar-timestamp-date.yaml"))

[<Fact>]
[<Trait("Category", "Conformance")>]
let ``an ISO datetime with an explicit offset resolves to a Timestamp preserving that offset`` () =
    let expected =
        YamlValue.Mapping
            [| (YamlValue.String "ts",
                YamlValue.Timestamp(DateTimeOffset(2024, 1, 30, 9, 15, 0, TimeSpan.FromHours(2.0)))) |]
    Assert.Equal(expected, YamlValue.Parse(readData "scalar-timestamp-datetime-offset.yaml"))

[<Fact>]
[<Trait("Category", "Conformance")>]
let ``a space-separated timestamp with fractional seconds and offset resolves correctly`` () =
    match YamlValue.Parse(readData "scalar-timestamp-space-separated.yaml") with
    | YamlValue.Mapping [| (_, YamlValue.Timestamp ts) |] ->
        Assert.Equal(2001, ts.Year)
        Assert.Equal(12, ts.Month)
        Assert.Equal(14, ts.Day)
        Assert.Equal(21, ts.Hour)
        Assert.Equal(59, ts.Minute)
        Assert.Equal(43, ts.Second)
        Assert.Equal(TimeSpan.FromHours(-5.0), ts.Offset)
    | other -> failwithf "Expected a single Timestamp entry, got %A" other

[<Fact>]
[<Trait("Category", "Conformance")>]
let ``literal block scalar with default (clip) chomping keeps a single trailing newline`` () =
    let expected =
        YamlValue.Mapping [| (YamlValue.String "text", YamlValue.String "line one\nline two\n") |]
    Assert.Equal(expected, YamlValue.Parse(readData "block-scalar-literal-clip.yaml"))

[<Fact>]
[<Trait("Category", "Conformance")>]
let ``literal block scalar with strip chomping (|-) drops all trailing newlines`` () =
    let expected =
        YamlValue.Mapping [| (YamlValue.String "text", YamlValue.String "line one\nline two") |]
    Assert.Equal(expected, YamlValue.Parse(readData "block-scalar-literal-strip.yaml"))

[<Fact>]
[<Trait("Category", "Conformance")>]
let ``literal block scalar with keep chomping (|+) preserves all trailing newlines`` () =
    let expected =
        YamlValue.Mapping [| (YamlValue.String "text", YamlValue.String "line one\nline two\n\n\n") |]
    Assert.Equal(expected, YamlValue.Parse(readData "block-scalar-literal-keep.yaml"))

[<Fact>]
[<Trait("Category", "Conformance")>]
let ``folded block scalar with default (clip) chomping folds line breaks into spaces`` () =
    let expected =
        YamlValue.Mapping [| (YamlValue.String "text", YamlValue.String "line one line two\n") |]
    Assert.Equal(expected, YamlValue.Parse(readData "block-scalar-folded-clip.yaml"))

[<Fact>]
[<Trait("Category", "Conformance")>]
let ``folded block scalar with strip chomping (>-) folds line breaks and drops trailing newlines`` () =
    let expected =
        YamlValue.Mapping [| (YamlValue.String "text", YamlValue.String "line one line two") |]
    Assert.Equal(expected, YamlValue.Parse(readData "block-scalar-folded-strip.yaml"))

[<Fact>]
[<Trait("Category", "Conformance")>]
let ``an alias shares the anchored subtree`` () =
    let expected =
        let shared = YamlValue.Sequence [| YamlValue.Number 1M; YamlValue.Number 2M |]
        YamlValue.Mapping [| (YamlValue.String "base", shared); (YamlValue.String "copy", shared) |]
    Assert.Equal(expected, YamlValue.Parse(readData "anchor-alias-basic.yaml"))

[<Fact>]
[<Trait("Category", "Conformance")>]
let ``a merge key folds the anchored mapping's keys into the merging mapping`` () =
    let defaultsPairs =
        [| (YamlValue.String "restart", YamlValue.String "always")
           (YamlValue.String "logging", YamlValue.String "json-file") |]
    // The merging mapping's own keys come first (in source order), with the merged-in keys from
    // the anchored mapping appended after — matching the parser's actual merge-key semantics.
    let webPairs =
        [| (YamlValue.String "image", YamlValue.String "nginx")
           (YamlValue.String "restart", YamlValue.String "always")
           (YamlValue.String "logging", YamlValue.String "json-file") |]
    let expected =
        YamlValue.Mapping
            [| (YamlValue.String "defaults", YamlValue.Mapping defaultsPairs)
               (YamlValue.String "web", YamlValue.Mapping webPairs) |]
    Assert.Equal(expected, YamlValue.Parse(readData "anchor-merge-key.yaml"))

[<Fact>]
[<Trait("Category", "Conformance")>]
let ``a multi-document stream splits into separate documents on ---`` () =
    let docs = YamlValue.ParseMultiple(readData "multi-document-stream.yaml") |> Seq.toList
    let expected =
        [ YamlValue.Mapping [| (YamlValue.String "kind", YamlValue.String "Service"); (YamlValue.String "name", YamlValue.String "web") |]
          YamlValue.Mapping [| (YamlValue.String "kind", YamlValue.String "Deployment"); (YamlValue.String "name", YamlValue.String "web") |] ]
    Assert.Equal<YamlValue list>(expected, docs)

// ---------------------------------------------------------------------
// Error cases — must raise YamlParseException
// ---------------------------------------------------------------------

[<Theory>]
[<Trait("Category", "Conformance")>]
[<ClassData(typeof<ErrorCaseData>)>]
let ``deliberately invalid YAML raises YamlParseException`` (fileName: string) =
    let text = readData fileName
    Assert.Throws<YamlParseException>(fun () -> YamlValue.Parse text |> ignore) |> ignore

// ---------------------------------------------------------------------
// Manual smoke test (Part 3 of Phase 11) — docker-compose-shaped YAML with anchors and merge
// keys, round-tripped through the emitter and re-parsed, as a permanent regression check.
// ---------------------------------------------------------------------

[<Fact>]
[<Trait("Category", "Conformance")>]
let ``docker-compose-shaped YAML with anchors and merge keys round-trips through ToString`` () =
    let composeYaml = """
x-common-env: &common-env
  RAILS_ENV: production
  LOG_LEVEL: info

services:
  web:
    image: myapp:latest
    ports:
      - "3000:3000"
    environment:
      <<: *common-env
      SERVICE_NAME: web
  worker:
    image: myapp:latest
    command: ["bundle", "exec", "sidekiq"]
    environment:
      <<: *common-env
      SERVICE_NAME: worker
"""

    let v1 = YamlValue.Parse composeYaml

    // Sanity: the merge key actually merged the anchored keys into both services.
    let services = match v1 with YamlValue.Mapping p -> (p |> Array.find (fun (k, _) -> k = YamlValue.String "services") |> snd) | _ -> failwith "expected mapping"
    let web = match services with YamlValue.Mapping p -> (p |> Array.find (fun (k, _) -> k = YamlValue.String "web") |> snd) | _ -> failwith "expected mapping"
    let webEnv = match web with YamlValue.Mapping p -> (p |> Array.find (fun (k, _) -> k = YamlValue.String "environment") |> snd) | _ -> failwith "expected mapping"
    match webEnv with
    | YamlValue.Mapping envPairs ->
        Assert.Contains((YamlValue.String "RAILS_ENV", YamlValue.String "production"), envPairs)
        Assert.Contains((YamlValue.String "SERVICE_NAME", YamlValue.String "web"), envPairs)
    | other -> failwithf "Expected environment to be a Mapping, got %A" other

    // NB: a bare `.ToString()` call resolves to the compiler-generated structural ToString (the
    // `_Print`/`StructuredFormatDisplay` debug representation), not the YAML emitter — see the
    // note on `YamlEmitterExtensions.ToString` in YamlEmitter.fs. `ToString(YamlSaveOptions.None)`
    // is the call site that reliably picks the real emitter.
    let printed = v1.ToString(YamlSaveOptions.None)
    let v2 = YamlValue.Parse printed

    Assert.Equal(v1, v2)
