module FSharp.Data.Yaml.Tests.YamlValueTests

open System
open Xunit
open FSharp.Data

[<Fact>]
let ``DU cases construct and pattern-match`` () =
    let values =
        [ YamlValue.String "hello"
          YamlValue.Number 42M
          YamlValue.Float 3.14
          YamlValue.Boolean true
          YamlValue.Timestamp(DateTimeOffset(2024, 1, 30, 0, 0, 0, TimeSpan.Zero))
          YamlValue.Mapping [| YamlValue.String "k", YamlValue.String "v" |]
          YamlValue.Sequence [| YamlValue.Number 1M; YamlValue.Number 2M |]
          YamlValue.Null ]

    for v in values do
        match v with
        | YamlValue.String s -> Assert.Equal("hello", s)
        | YamlValue.Number n -> Assert.Equal(42M, n)
        | YamlValue.Float f -> Assert.Equal(3.14, f)
        | YamlValue.Boolean b -> Assert.True(b)
        | YamlValue.Timestamp _ -> ()
        | YamlValue.Mapping props -> Assert.Single(props) |> ignore
        | YamlValue.Sequence elems -> Assert.Equal(2, elems.Length)
        | YamlValue.Null -> ()

[<Fact>]
let ``Is* properties reflect the active case`` () =
    Assert.True((YamlValue.String "x").IsString)
    Assert.True((YamlValue.Number 1M).IsNumber)
    Assert.True((YamlValue.Float 1.0).IsFloat)
    Assert.True((YamlValue.Boolean true).IsBoolean)
    Assert.True((YamlValue.Timestamp DateTimeOffset.UtcNow).IsTimestamp)
    Assert.True((YamlValue.Mapping [||]).IsMapping)
    Assert.True((YamlValue.Sequence [||]).IsSequence)
    Assert.True(YamlValue.Null.IsNull)
    Assert.False((YamlValue.String "x").IsNumber)

[<Fact>]
let ``structural equality holds for equivalent trees`` () =
    let a = YamlValue.Mapping [| YamlValue.String "a", YamlValue.Number 1M |]
    let b = YamlValue.Mapping [| YamlValue.String "a", YamlValue.Number 1M |]
    Assert.Equal<YamlValue>(a, b)

[<Fact>]
let ``_Print gives a readable structured display`` () =
    let v = YamlValue.Mapping [| YamlValue.String "name", YamlValue.String "Tomas" |]
    let text = sprintf "%A" v
    Assert.Contains("name", text)
    Assert.Contains("Tomas", text)

[<Fact>]
let ``YamlParseException formats a caret-pointing snippet`` () =
    let source = "key: [1, 2\nkey2: value"
    let ex = YamlParseException("unexpected end of flow sequence", source, 1, 11)

    Assert.Equal(1, ex.Line)
    Assert.Equal(11, ex.Column)
    Assert.Equal("key: [1, 2\n          ^", ex.Snippet)
    Assert.Contains("unexpected end of flow sequence", ex.Message)
    Assert.Contains("line 1, column 11", ex.Message)
    Assert.Contains(ex.Snippet, ex.Message)

[<Fact>]
let ``YamlParseException direct constructor accepts a precomputed snippet`` () =
    let ex = YamlParseException("bad indentation", 3, 5, "  foo\n    ^")
    Assert.Equal(3, ex.Line)
    Assert.Equal(5, ex.Column)
    Assert.Equal("  foo\n    ^", ex.Snippet)

[<Fact>]
let ``YamlPath and comments model round-trip through equality`` () =
    let path: YamlPath = [ Key(YamlValue.String "services"); Key(YamlValue.String "web"); Index 0 ]
    let comments = { Leading = [ "# a comment" ]; Trailing = Some "# trailing" }
    let doc =
        { Value = YamlValue.Null
          Comments = Map.ofList [ path, comments ]
          Directives = [ "YAML", "1.2" ]
          Trailing = [ "# eof comment" ] }

    Assert.Equal(YamlValue.Null, doc.Value)
    Assert.Equal(comments, doc.Comments.[path])
    Assert.Equal<(string * string) list>([ "YAML", "1.2" ], doc.Directives)
    Assert.Equal<string list>([ "# eof comment" ], doc.Trailing)

[<Theory>]
[<InlineData(YamlSaveOptions.None, 0)>]
[<InlineData(YamlSaveOptions.DisableFormatting, 1)>]
[<InlineData(YamlSaveOptions.Flow, 2)>]
[<InlineData(YamlSaveOptions.SuppressComments, 4)>]
[<InlineData(YamlSaveOptions.ExplicitDocumentMarkers, 8)>]
let ``YamlSaveOptions has the documented flag values`` (option: YamlSaveOptions) (expected: int) =
    Assert.Equal(expected, int option)

[<Fact>]
let ``YamlSaveOptions flags combine`` () =
    let combined = YamlSaveOptions.Flow ||| YamlSaveOptions.SuppressComments
    Assert.True(combined.HasFlag(YamlSaveOptions.Flow))
    Assert.True(combined.HasFlag(YamlSaveOptions.SuppressComments))
    Assert.False(combined.HasFlag(YamlSaveOptions.ExplicitDocumentMarkers))
