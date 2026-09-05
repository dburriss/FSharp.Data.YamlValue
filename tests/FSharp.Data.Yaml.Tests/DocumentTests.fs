module FSharp.Data.Yaml.Tests.DocumentTests

open System
open System.IO
open Xunit
open FSharp.Data

// `YamlExtensions` (the `?` operator, `GetProperty`, etc.) is Phase 10 — not implemented yet, so
// tests here look up mapping properties directly against the `(YamlValue * YamlValue)[]` shape.

/// Looks up a string-keyed property of a `YamlValue.Mapping`, failing the test if the value
/// isn't a mapping or doesn't contain the key.
let private prop (name: string) (value: YamlValue) : YamlValue =
    match value with
    | YamlValue.Mapping pairs ->
        match pairs |> Array.tryFind (fun (k, _) -> k = YamlValue.String name) with
        | Some(_, v) -> v
        | None -> failwithf "Mapping has no property '%s': %A" name value
    | other -> failwithf "Expected a Mapping to look up '%s', got %A" name other

// ---------------------------------------------------------------------
// `---` document-start marker
// ---------------------------------------------------------------------

[<Fact>]
let ``leading --- marker is optional and parses the same as without it`` () =
    let withMarker = YamlValue.Parse "---\nkey: value\n"
    let withoutMarker = YamlValue.Parse "key: value\n"
    Assert.Equal(withoutMarker, withMarker)

[<Fact>]
let ``--- marker with inline content on the same line`` () =
    let result = YamlValue.Parse "--- hello"
    Assert.Equal(YamlValue.String "hello", result)

[<Fact>]
let ``--- marker alone with no content is an empty (null) document`` () =
    let result = YamlValue.Parse "---\n"
    Assert.Equal(YamlValue.Null, result)

[<Fact>]
let ``a second document introduced by --- is an error for single-document Parse`` () =
    let ex =
        Assert.Throws<YamlParseException>(fun () -> YamlValue.Parse "a: 1\n---\nb: 2\n" |> ignore)
    Assert.Contains("ParseMultiple", ex.Message)

// ---------------------------------------------------------------------
// ParseMultiple — multi-document streams
// ---------------------------------------------------------------------

[<Fact>]
let ``ParseMultiple splits a stream on --- into separate documents`` () =
    let docs = YamlValue.ParseMultiple "a: 1\n---\nb: 2\n---\nc: 3\n" |> Seq.toList
    Assert.Equal(3, docs.Length)
    Assert.Equal(YamlValue.Number 1m, prop "a" docs.[0])
    Assert.Equal(YamlValue.Number 2m, prop "b" docs.[1])
    Assert.Equal(YamlValue.Number 3m, prop "c" docs.[2])

[<Fact>]
let ``ParseMultiple handles trailing ... end markers between documents`` () =
    let docs = YamlValue.ParseMultiple "a: 1\n...\n---\nb: 2\n...\n" |> Seq.toList
    Assert.Equal(2, docs.Length)
    Assert.Equal(YamlValue.Number 1m, prop "a" docs.[0])
    Assert.Equal(YamlValue.Number 2m, prop "b" docs.[1])

[<Fact>]
let ``ParseMultiple without any --- separators yields a single document`` () =
    let docs = YamlValue.ParseMultiple "a: 1\nb: 2\n" |> Seq.toList
    Assert.Equal(1, docs.Length)
    Assert.Equal(YamlValue.Number 1m, prop "a" docs.[0])

[<Fact>]
let ``ParseMultiple on an empty stream yields zero documents`` () =
    let docs = YamlValue.ParseMultiple "" |> Seq.toList
    Assert.Empty(docs)

[<Fact>]
let ``ParseMultiple supports scalar-only documents separated by ---`` () =
    let docs = YamlValue.ParseMultiple "one\n---\ntwo\n---\nthree\n" |> Seq.toList
    Assert.Equal<YamlValue list>(
        [ YamlValue.String "one"; YamlValue.String "two"; YamlValue.String "three" ],
        docs
    )

// ---------------------------------------------------------------------
// Directives — %YAML, %TAG
// ---------------------------------------------------------------------

[<Fact>]
let ``a %YAML directive followed by --- parses without error`` () =
    let result = YamlValue.Parse "%YAML 1.2\n---\nkey: value\n"
    Assert.Equal(YamlValue.String "value", prop "key" result)

[<Fact>]
let ``%YAML directive is captured in YamlDocument.Directives`` () =
    let doc = YamlDocument.Parse "%YAML 1.2\n---\nkey: value\n"
    Assert.Equal(YamlValue.String "value", prop "key" doc.Value)
    Assert.Contains(("YAML", "1.2"), doc.Directives)

[<Fact>]
let ``a %TAG directive followed by --- parses without error`` () =
    let result = YamlValue.Parse "%TAG !e! tag:example.com,2000:\n---\nkey: value\n"
    Assert.Equal(YamlValue.String "value", prop "key" result)

[<Fact>]
let ``%TAG directive is captured in YamlDocument.Directives`` () =
    let doc = YamlDocument.Parse "%TAG !e! tag:example.com,2000:\n---\nkey: value\n"
    Assert.Contains(("TAG", "!e! tag:example.com,2000:"), doc.Directives)

[<Fact>]
let ``directives are reset per document in a multi-document stream`` () =
    let docs = YamlDocument.ParseMultiple "%YAML 1.2\n---\na: 1\n---\nb: 2\n" |> Seq.toList
    Assert.Equal(2, docs.Length)
    Assert.Equal<(string * string) list>([ ("YAML", "1.2") ], docs.[0].Directives)
    Assert.Empty(docs.[1].Directives)

// ---------------------------------------------------------------------
// Directive error cases
// ---------------------------------------------------------------------

[<Fact>]
let ``a directive with no following --- is an error`` () =
    Assert.Throws<YamlParseException>(fun () -> YamlValue.Parse "%YAML 1.2\nkey: value\n" |> ignore)
    |> ignore

[<Fact>]
let ``a duplicate conflicting %YAML directive is an error`` () =
    Assert.Throws<YamlParseException>(fun () ->
        YamlValue.Parse "%YAML 1.1\n%YAML 1.2\n---\nkey: value\n" |> ignore)
    |> ignore

[<Fact>]
let ``a duplicate %TAG directive for the same handle is an error`` () =
    Assert.Throws<YamlParseException>(fun () ->
        YamlValue.Parse "%TAG !e! tag:one:\n%TAG !e! tag:two:\n---\nkey: value\n" |> ignore)
    |> ignore

[<Fact>]
let ``a malformed %YAML directive is an error`` () =
    Assert.Throws<YamlParseException>(fun () -> YamlValue.Parse "%YAML banana\n---\nkey: value\n" |> ignore)
    |> ignore

[<Fact>]
let ``a malformed %TAG directive is an error`` () =
    Assert.Throws<YamlParseException>(fun () -> YamlValue.Parse "%TAG justahandle\n---\nkey: value\n" |> ignore)
    |> ignore

[<Fact>]
let ``a directive appearing after the document's own --- marker is an error`` () =
    Assert.Throws<YamlParseException>(fun () ->
        YamlValue.Parse "---\n%YAML 1.2\nkey: value\n" |> ignore)
    |> ignore

// ---------------------------------------------------------------------
// Load / AsyncLoad / TryParse
// ---------------------------------------------------------------------

[<Fact>]
let ``Load from a file path parses the file's contents`` () =
    let path = Path.GetTempFileName()
    try
        File.WriteAllText(path, "key: value\n")
        let result = YamlValue.Load(path)
        Assert.Equal(YamlValue.String "value", prop "key" result)
    finally
        File.Delete(path)

[<Fact>]
let ``Load from a TextReader parses the reader's contents`` () =
    use reader = new StringReader("key: value\n")
    let result = YamlValue.Load(reader :> TextReader)
    Assert.Equal(YamlValue.String "value", prop "key" result)

[<Fact>]
let ``Load from a Stream parses the stream's contents`` () =
    use stream = new MemoryStream(Text.Encoding.UTF8.GetBytes("key: value\n"))
    let result = YamlValue.Load(stream :> Stream)
    Assert.Equal(YamlValue.String "value", prop "key" result)

[<Fact>]
let ``AsyncLoad from a file path parses the file's contents`` () =
    let path = Path.GetTempFileName()
    try
        File.WriteAllText(path, "key: value\n")
        let result = YamlValue.AsyncLoad(path) |> Async.RunSynchronously
        Assert.Equal(YamlValue.String "value", prop "key" result)
    finally
        File.Delete(path)

[<Fact>]
let ``YamlDocument.Load from a file path parses and captures directives`` () =
    let path = Path.GetTempFileName()
    try
        File.WriteAllText(path, "%YAML 1.2\n---\nkey: value\n")
        let doc = YamlDocument.Load(path)
        Assert.Equal(YamlValue.String "value", prop "key" doc.Value)
        Assert.Contains(("YAML", "1.2"), doc.Directives)
    finally
        File.Delete(path)

[<Fact>]
let ``TryParse returns Some for valid input`` () =
    match YamlValue.TryParse "key: value\n" with
    | Some result -> Assert.Equal(YamlValue.String "value", prop "key" result)
    | None -> failwith "Expected Some, got None"

[<Fact>]
let ``TryParse returns None for invalid input`` () =
    // Unclosed flow mapping — a genuine parse error.
    Assert.Equal(None, YamlValue.TryParse "{a: 1")

[<Fact>]
let ``TryParse returns None rather than throwing for a directive with no ---`` () =
    Assert.Equal(None, YamlValue.TryParse "%YAML 1.2\nkey: value\n")

[<Fact>]
let ``YamlDocument.TryParse returns None for invalid input`` () =
    Assert.Equal(None, YamlDocument.TryParse "{a: 1")
