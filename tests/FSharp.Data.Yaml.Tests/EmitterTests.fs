module FSharp.Data.Yaml.Tests.EmitterTests

open Xunit
open FSharp.Data

// ---------------------------------------------------------------------
// Basic scalar emission
// ---------------------------------------------------------------------

[<Fact>]
let ``a plain-safe string emits unquoted`` () =
    Assert.Equal("hello\n", YamlValue.String("hello").ToString(YamlSaveOptions.None))

[<Fact>]
let ``a Number emits as plain digits and round-trips`` () =
    let v = YamlValue.Number 1985m
    Assert.Equal("1985\n", v.ToString(YamlSaveOptions.None))
    Assert.Equal(v, YamlValue.Parse(v.ToString(YamlSaveOptions.None)))

[<Fact>]
let ``a Float emits and round-trips`` () =
    // 0.1 is not exactly decimal-representable to 28 significant digits combined with the
    // exponent shift here — chosen so it resolves back as Float, not Number.
    let v = YamlValue.Float 1.7976931348623157e+300
    let text = v.ToString(YamlSaveOptions.None)
    match YamlValue.Parse text with
    | YamlValue.Float f -> Assert.Equal(1.7976931348623157e+300, f)
    | other -> failwithf "Expected Float, got %A" other

[<Fact>]
let ``Float special values emit as core-schema literals`` () =
    Assert.Equal(".inf\n", YamlValue.Float(System.Double.PositiveInfinity).ToString(YamlSaveOptions.None))
    Assert.Equal("-.inf\n", YamlValue.Float(System.Double.NegativeInfinity).ToString(YamlSaveOptions.None))
    Assert.Equal(".nan\n", YamlValue.Float(nan).ToString(YamlSaveOptions.None))

[<Fact>]
let ``a Boolean emits as true or false`` () =
    Assert.Equal("true\n", YamlValue.Boolean(true).ToString(YamlSaveOptions.None))
    Assert.Equal("false\n", YamlValue.Boolean(false).ToString(YamlSaveOptions.None))

[<Fact>]
let ``a Timestamp emits and round-trips`` () =
    let v = YamlValue.Timestamp(System.DateTimeOffset(2024, 1, 30, 12, 0, 0, System.TimeSpan.Zero))
    let text = v.ToString(YamlSaveOptions.None)
    Assert.Equal(v, YamlValue.Parse text)

[<Fact>]
let ``Null emits as the literal null`` () =
    Assert.Equal("null\n", YamlValue.Null.ToString(YamlSaveOptions.None))

// ---------------------------------------------------------------------
// Quoting-rule triggers (block style)
// ---------------------------------------------------------------------

let private roundTripsAsString (text: string) =
    let v = YamlValue.String text
    let emitted = v.ToString(YamlSaveOptions.None)
    Assert.Equal(v, YamlValue.Parse emitted)
    emitted

[<Fact>]
let ``the string "true" is quoted so it round-trips as String`` () =
    let emitted = roundTripsAsString "true"
    Assert.Contains("'true'", emitted)

[<Fact>]
let ``the string "123" is quoted so it round-trips as String`` () =
    let emitted = roundTripsAsString "123"
    Assert.Contains("'123'", emitted)

[<Fact>]
let ``the string "null" is quoted so it round-trips as String`` () =
    let emitted = roundTripsAsString "null"
    Assert.Contains("'null'", emitted)

[<Fact>]
let ``the string "~" is quoted so it round-trips as String`` () =
    let emitted = roundTripsAsString "~"
    Assert.Contains("'~'", emitted)

[<Fact>]
let ``the empty string is quoted so it round-trips as String, not Null`` () =
    let emitted = roundTripsAsString ""
    Assert.Equal("''\n", emitted)

[<Fact>]
let ``leading whitespace forces quoting`` () = roundTripsAsString "  leading" |> ignore

[<Fact>]
let ``trailing whitespace forces quoting`` () = roundTripsAsString "trailing  " |> ignore

[<Theory>]
[<InlineData("-leads with dash")>]
[<InlineData("?leads with question")>]
[<InlineData(":leads with colon")>]
[<InlineData("#leads with hash")>]
[<InlineData("&leads with amp")>]
[<InlineData("*leads with star")>]
[<InlineData("!leads with bang")>]
[<InlineData("|leads with pipe")>]
[<InlineData(">leads with gt")>]
[<InlineData("'leads with quote")>]
[<InlineData("\"leads with dquote")>]
[<InlineData("%leads with percent")>]
[<InlineData("@leads with at")>]
[<InlineData("`leads with backtick")>]
[<InlineData("[leads with bracket")>]
[<InlineData("]leads with rbracket")>]
[<InlineData("{leads with brace")>]
[<InlineData("}leads with rbrace")>]
[<InlineData(",leads with comma")>]
let ``a leading indicator character forces quoting`` (text: string) = roundTripsAsString text |> ignore

[<Fact>]
let ``a colon-space inside the string forces quoting`` () = roundTripsAsString "key: value" |> ignore

[<Fact>]
let ``a space-hash inside the string forces quoting`` () = roundTripsAsString "text #not a comment" |> ignore

[<Fact>]
let ``a string ending in a colon forces quoting`` () = roundTripsAsString "ends:" |> ignore

[<Fact>]
let ``control characters force double-quoted escaping`` () =
    let text = "a\u0001b\u0007c"
    let emitted = roundTripsAsString text
    Assert.StartsWith("\"", emitted.TrimEnd('\n'))
    Assert.Contains("\\x01", emitted)
    Assert.Contains("\\a", emitted)

[<Fact>]
let ``a normal identifier-like string is NOT quoted`` () =
    let emitted = YamlValue.String("hello_world-123abc").ToString(YamlSaveOptions.None)
    Assert.Equal("hello_world-123abc\n", emitted)

// ---------------------------------------------------------------------
// Block-style mapping / sequence, nested
// ---------------------------------------------------------------------

[<Fact>]
let ``a flat mapping emits key colon value lines`` () =
    let v =
        YamlValue.Mapping [| YamlValue.String "name", YamlValue.String "Tomas"; YamlValue.String "born", YamlValue.Number 1985m |]

    let expected = "name: Tomas\nborn: 1985\n"
    Assert.Equal(expected, v.ToString(YamlSaveOptions.None))

[<Fact>]
let ``a flat sequence emits dash-prefixed lines`` () =
    let v = YamlValue.Sequence [| YamlValue.String "a"; YamlValue.String "b" |]
    Assert.Equal("- a\n- b\n", v.ToString(YamlSaveOptions.None))

[<Fact>]
let ``a nested mapping under a key is indented on following lines`` () =
    let v =
        YamlValue.Mapping
            [| YamlValue.String "outer",
               YamlValue.Mapping [| YamlValue.String "inner", YamlValue.String "value" |] |]

    let expected = "outer:\n  inner: value\n"
    Assert.Equal(expected, v.ToString(YamlSaveOptions.None))

[<Fact>]
let ``a nested sequence under a key is indented on following lines`` () =
    let v =
        YamlValue.Mapping
            [| YamlValue.String "items", YamlValue.Sequence [| YamlValue.String "a"; YamlValue.String "b" |] |]

    let expected = "items:\n  - a\n  - b\n"
    Assert.Equal(expected, v.ToString(YamlSaveOptions.None))

[<Fact>]
let ``deep nesting round-trips`` () =
    let v =
        YamlValue.Mapping
            [| YamlValue.String "services",
               YamlValue.Mapping
                   [| YamlValue.String "web",
                      YamlValue.Mapping
                          [| YamlValue.String "ports", YamlValue.Sequence [| YamlValue.Number 80m; YamlValue.Number 8080m |] |] |] |]

    let emitted = v.ToString(YamlSaveOptions.None)
    Assert.Equal(v, YamlValue.Parse emitted)

[<Fact>]
let ``empty mapping and sequence emit as inline flow markers`` () =
    let v =
        YamlValue.Mapping
            [| YamlValue.String "m", YamlValue.Mapping [||]
               YamlValue.String "s", YamlValue.Sequence [||] |]

    let expected = "m: {}\ns: []\n"
    Assert.Equal(expected, v.ToString(YamlSaveOptions.None))

// ---------------------------------------------------------------------
// Flow style
// ---------------------------------------------------------------------

[<Fact>]
let ``Flow style emits compact JSON-like output`` () =
    let v =
        YamlValue.Mapping
            [| YamlValue.String "a", YamlValue.Number 1m
               YamlValue.String "b", YamlValue.Sequence [| YamlValue.Number 1m; YamlValue.Number 2m |] |]

    Assert.Equal("{a: 1, b: [1, 2]}\n", v.ToString(YamlSaveOptions.Flow))

[<Fact>]
let ``Flow style round-trips through the parser`` () =
    let v =
        YamlValue.Mapping
            [| YamlValue.String "a", YamlValue.Number 1m
               YamlValue.String "b", YamlValue.Sequence [| YamlValue.String "x"; YamlValue.String "y" |] |]

    let emitted = v.ToString(YamlSaveOptions.Flow)
    Assert.Equal(v, YamlValue.Parse emitted)

// ---------------------------------------------------------------------
// indentationSpaces
// ---------------------------------------------------------------------

[<Fact>]
let ``indentationSpaces controls the nested indent width`` () =
    let v =
        YamlValue.Mapping [| YamlValue.String "items", YamlValue.Sequence [| YamlValue.String "a" |] |]

    Assert.Equal("items:\n    - a\n", v.ToString(YamlSaveOptions.None, 4))
    Assert.Equal("items:\n  - a\n", v.ToString(YamlSaveOptions.None, 2))

// ---------------------------------------------------------------------
// Multi-line strings
// ---------------------------------------------------------------------

[<Fact>]
let ``a multi-line string emits as a literal block scalar and round-trips`` () =
    let v = YamlValue.Mapping [| YamlValue.String "text", YamlValue.String "line one\nline two\n" |]
    let emitted = v.ToString(YamlSaveOptions.None)
    Assert.Contains("|2", emitted)
    Assert.Equal(v, YamlValue.Parse emitted)

[<Fact>]
let ``a multi-line string with no trailing newline uses the strip chomping indicator`` () =
    let v = YamlValue.Mapping [| YamlValue.String "text", YamlValue.String "line one\nline two" |]
    let emitted = v.ToString(YamlSaveOptions.None)
    Assert.Contains("|2-", emitted)
    Assert.Equal(v, YamlValue.Parse emitted)

[<Fact>]
let ``a multi-line string with extra trailing blank lines uses the keep chomping indicator`` () =
    let v = YamlValue.Mapping [| YamlValue.String "text", YamlValue.String "line one\nline two\n\n\n" |]
    let emitted = v.ToString(YamlSaveOptions.None)
    Assert.Contains("|2+", emitted)
    Assert.Equal(v, YamlValue.Parse emitted)

[<Fact>]
let ``a multi-line string containing a non-empty all-space line falls back to double-quoted`` () =
    let v = YamlValue.Mapping [| YamlValue.String "text", YamlValue.String "one\n   \ntwo" |]
    let emitted = v.ToString(YamlSaveOptions.None)
    Assert.Contains("\"one\\n   \\ntwo\"", emitted)
    Assert.Equal(v, YamlValue.Parse emitted)

[<Fact>]
let ``a multi-line string containing a control character falls back to double-quoted`` () =
    let v = YamlValue.Mapping [| YamlValue.String "text", YamlValue.String "one\ntwo\u0001three" |]
    let emitted = v.ToString(YamlSaveOptions.None)
    Assert.Contains("\\x01", emitted)
    Assert.Equal(v, YamlValue.Parse emitted)

// ---------------------------------------------------------------------
// Non-scalar mapping keys
// ---------------------------------------------------------------------

[<Fact>]
let ``a Sequence key emits using the explicit question-mark form and round-trips`` () =
    let v =
        YamlValue.Mapping [| YamlValue.Sequence [| YamlValue.Number 1m; YamlValue.Number 2m |], YamlValue.String "value" |]

    let emitted = v.ToString(YamlSaveOptions.None)
    Assert.Contains("? ", emitted)
    Assert.Equal(v, YamlValue.Parse emitted)

[<Fact>]
let ``a Mapping key emits using the explicit question-mark form and round-trips`` () =
    let v =
        YamlValue.Mapping
            [| YamlValue.Mapping [| YamlValue.String "x", YamlValue.Number 1m |], YamlValue.String "value" |]

    let emitted = v.ToString(YamlSaveOptions.None)
    Assert.Contains("? ", emitted)
    Assert.Equal(v, YamlValue.Parse emitted)

// ---------------------------------------------------------------------
// ExplicitDocumentMarkers
// ---------------------------------------------------------------------

[<Fact>]
let ``ExplicitDocumentMarkers emits a leading marker and round-trips`` () =
    let v = YamlValue.String "hello"
    let emitted = v.ToString(YamlSaveOptions.ExplicitDocumentMarkers)
    Assert.StartsWith("---\n", emitted)
    Assert.Equal(v, YamlValue.Parse emitted)

[<Fact>]
let ``ExplicitDocumentMarkers emits a trailing end marker`` () =
    let v = YamlValue.String "hello"
    let emitted = v.ToString(YamlSaveOptions.ExplicitDocumentMarkers)
    Assert.Contains("...", emitted)

// ---------------------------------------------------------------------
// DisableFormatting
// ---------------------------------------------------------------------

[<Fact>]
let ``DisableFormatting omits the cosmetic trailing newline Flow style would otherwise add`` () =
    // Flow style is the one case where the rendered content doesn't already end with a newline
    // of its own, so this is the one case where DisableFormatting has a visible effect on the
    // trailing newline — see the doc comment on `YamlEmitter.render`'s `core` computation for why
    // it never *removes* a newline the content produced on its own (block style, or a `Flow`
    // value followed by trailing comments, always already end with one, and that one is left
    // alone even under DisableFormatting since it can be chomp-significant).
    let v = YamlValue.String "hello"
    Assert.False((v.ToString(YamlSaveOptions.Flow ||| YamlSaveOptions.DisableFormatting)).EndsWith("\n"))
    Assert.True((v.ToString(YamlSaveOptions.Flow)).EndsWith("\n"))

[<Fact>]
let ``DisableFormatting never strips a trailing newline that is chomp-significant`` () =
    // Regression test: a value ending the whole document with a `|` literal block scalar whose
    // trailing-newline count is part of its value must round-trip exactly even under
    // DisableFormatting — this was a real bug caught by RoundTripTests where naively stripping
    // "the" trailing newline corrupted the last blank line of the block scalar's content.
    let v = YamlValue.Mapping [| YamlValue.Number 0m, YamlValue.String "\n" |]
    let emitted = v.ToString(YamlSaveOptions.DisableFormatting)
    Assert.Equal(v, YamlValue.Parse emitted)

// ---------------------------------------------------------------------
// YamlDocument comment re-emission
// ---------------------------------------------------------------------

[<Fact>]
let ``YamlDocument.ToString re-emits leading and trailing comments`` () =
    let doc = YamlDocument.Parse "# a note\nname: Tomas   # given name\nage: 30\n"
    let emitted = doc.ToString(YamlSaveOptions.None)
    Assert.Contains("# a note", emitted)
    Assert.Contains("# given name", emitted)

[<Fact>]
let ``YamlDocument.ToString re-emits Trailing comments after the last node`` () =
    let doc = YamlDocument.Parse "name: Tomas\n# closing remark\n"
    let emitted = doc.ToString(YamlSaveOptions.None)
    Assert.Contains("# closing remark", emitted)

[<Fact>]
let ``SuppressComments drops comments from YamlDocument.ToString`` () =
    let doc = YamlDocument.Parse "# a note\nname: Tomas   # given name\nage: 30\n# closing\n"
    let emitted = doc.ToString(YamlSaveOptions.SuppressComments)
    Assert.DoesNotContain("# a note", emitted)
    Assert.DoesNotContain("# given name", emitted)
    Assert.DoesNotContain("# closing", emitted)
    Assert.Equal("name: Tomas\nage: 30\n", emitted)

// ---------------------------------------------------------------------
// Expansion budget
// ---------------------------------------------------------------------

[<Fact>]
let ``emitting a pathological shared-reference structure raises the budget exception`` () =
    let rec build (n: int) (leaf: YamlValue) : YamlValue =
        if n = 0 then
            leaf
        else
            let inner = build (n - 1) leaf
            YamlValue.Sequence [| inner; inner |]

    let big = build 40 (YamlValue.String "leaf")

    Assert.Throws<YamlEmitBudgetExceededException>(fun () -> big.ToString(YamlSaveOptions.None) |> ignore)
    |> ignore
