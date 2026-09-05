module FSharp.Data.Yaml.Tests.FlowTests

open System
open Xunit
open FSharp.Data

// ---------------------------------------------------------------------
// Bare top-level scalars (JSON permits top-level scalars too)
// ---------------------------------------------------------------------

[<Fact>]
let ``bare plain scalar document resolves`` () =
    Assert.Equal(YamlValue.Number 42m, YamlValue.Parse "42")

[<Fact>]
let ``bare quoted scalar document`` () =
    Assert.Equal(YamlValue.String "hello", YamlValue.Parse "\"hello\"")

[<Fact>]
let ``empty document resolves to Null`` () =
    Assert.Equal(YamlValue.Null, YamlValue.Parse "")

[<Fact>]
let ``whitespace-only document resolves to Null`` () =
    Assert.Equal(YamlValue.Null, YamlValue.Parse "   \n  # just a comment\n")

// ---------------------------------------------------------------------
// Flow sequences
// ---------------------------------------------------------------------

[<Fact>]
let ``empty flow sequence`` () =
    Assert.Equal(YamlValue.Sequence [||], YamlValue.Parse "[]")

[<Fact>]
let ``flow sequence of plain scalars`` () =
    let expected = YamlValue.Sequence [| YamlValue.Number 1m; YamlValue.Number 2m; YamlValue.Number 3m |]
    Assert.Equal(expected, YamlValue.Parse "[1, 2, 3]")

[<Fact>]
let ``flow sequence trailing comma`` () =
    let expected = YamlValue.Sequence [| YamlValue.Number 1m; YamlValue.Number 2m |]
    Assert.Equal(expected, YamlValue.Parse "[1, 2,]")

[<Fact>]
let ``nested flow sequences`` () =
    let expected =
        YamlValue.Sequence
            [| YamlValue.Sequence [| YamlValue.Number 1m; YamlValue.Number 2m |]
               YamlValue.Sequence [| YamlValue.Sequence [| YamlValue.Number 3m |] |] |]
    Assert.Equal(expected, YamlValue.Parse "[[1, 2], [[3]]]")

// ---------------------------------------------------------------------
// Flow mappings
// ---------------------------------------------------------------------

[<Fact>]
let ``empty flow mapping`` () =
    Assert.Equal(YamlValue.Mapping [||], YamlValue.Parse "{}")

[<Fact>]
let ``flow mapping of string-keyed entries`` () =
    let expected =
        YamlValue.Mapping
            [| (YamlValue.String "a", YamlValue.Number 1m)
               (YamlValue.String "b", YamlValue.Number 2m) |]
    Assert.Equal(expected, YamlValue.Parse "{a: 1, b: 2}")

[<Fact>]
let ``flow mapping trailing comma`` () =
    let expected = YamlValue.Mapping [| (YamlValue.String "a", YamlValue.Number 1m) |]
    Assert.Equal(expected, YamlValue.Parse "{a: 1,}")

[<Fact>]
let ``flow mapping key-only entry is implicit null`` () =
    let expected = YamlValue.Mapping [| (YamlValue.String "a", YamlValue.Null) |]
    Assert.Equal(expected, YamlValue.Parse "{a}")

[<Fact>]
let ``flow mapping non-string key`` () =
    let expected = YamlValue.Mapping [| (YamlValue.Number 1m, YamlValue.String "a") |]
    Assert.Equal(expected, YamlValue.Parse "{1: a}")

[<Fact>]
let ``deeply nested flow mappings and sequences`` () =
    let doc = "{a: {b: {c: [1, [2, {d: 3}]]}}}"
    let parsed = YamlValue.Parse doc
    match parsed with
    | YamlValue.Mapping [| (YamlValue.String "a", YamlValue.Mapping [| (YamlValue.String "b", YamlValue.Mapping [| (YamlValue.String "c", YamlValue.Sequence [| YamlValue.Number 1m; YamlValue.Sequence [| YamlValue.Number 2m; YamlValue.Mapping [| (YamlValue.String "d", YamlValue.Number 3m) |] |] |]) |]) |]) |] -> ()
    | other -> failwithf "unexpected shape: %A" other

[<Fact>]
let ``multi-line flow mapping across several lines`` () =
    let doc = "{\n  name: Tomas,\n  born: 1985,\n  siblings: [Anna, Petr]\n}"
    let expected =
        YamlValue.Mapping
            [| (YamlValue.String "name", YamlValue.String "Tomas")
               (YamlValue.String "born", YamlValue.Number 1985m)
               (YamlValue.String "siblings", YamlValue.Sequence [| YamlValue.String "Anna"; YamlValue.String "Petr" |]) |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``compact flow-pair shorthand inside a sequence`` () =
    let expected =
        YamlValue.Sequence
            [| YamlValue.Mapping [| (YamlValue.String "a", YamlValue.String "b") |]
               YamlValue.String "c" |]
    Assert.Equal(expected, YamlValue.Parse "[a: b, c]")

// ---------------------------------------------------------------------
// Flow scalars of all resolved types
// ---------------------------------------------------------------------

[<Fact>]
let ``flow sequence of all resolved scalar types`` () =
    let doc = "[hello, 42, 3.14, true, false, null, 2024-01-30]"
    let expected =
        YamlValue.Sequence
            [| YamlValue.String "hello"
               YamlValue.Number 42m
               YamlValue.Number 3.14m
               YamlValue.Boolean true
               YamlValue.Boolean false
               YamlValue.Null
               YamlValue.Timestamp(DateTimeOffset(2024, 1, 30, 0, 0, 0, TimeSpan.Zero)) |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``flow mapping value with internal space is one plain scalar`` () =
    let expected = YamlValue.Mapping [| (YamlValue.String "a", YamlValue.String "b c") |]
    Assert.Equal(expected, YamlValue.Parse "{a: b c}")

[<Fact>]
let ``plain scalar containing a non-terminating colon`` () =
    let expected = YamlValue.Sequence [| YamlValue.String "12:30:00" |]
    Assert.Equal(expected, YamlValue.Parse "[12:30:00]")

[<Fact>]
let ``single-quoted scalar with doubled quote escape`` () =
    Assert.Equal(YamlValue.String "it's", YamlValue.Parse "'it''s'")

[<Fact>]
let ``double-quoted scalar always stays String even if it looks numeric`` () =
    Assert.Equal(YamlValue.String "42", YamlValue.Parse "\"42\"")

// ---------------------------------------------------------------------
// JSON corpus
// ---------------------------------------------------------------------

[<Fact>]
let ``JSON object with primitives`` () =
    let json = """{"name": "Tomas", "age": 30, "active": true, "misc": null}"""
    let expected =
        YamlValue.Mapping
            [| (YamlValue.String "name", YamlValue.String "Tomas")
               (YamlValue.String "age", YamlValue.Number 30m)
               (YamlValue.String "active", YamlValue.Boolean true)
               (YamlValue.String "misc", YamlValue.Null) |]
    Assert.Equal(expected, YamlValue.Parse json)

[<Fact>]
let ``JSON nested object and array`` () =
    let json = """{"items": [1, 2, 3], "nested": {"a": [true, false]}}"""
    let expected =
        YamlValue.Mapping
            [| (YamlValue.String "items", YamlValue.Sequence [| YamlValue.Number 1m; YamlValue.Number 2m; YamlValue.Number 3m |])
               (YamlValue.String "nested",
                YamlValue.Mapping [| (YamlValue.String "a", YamlValue.Sequence [| YamlValue.Boolean true; YamlValue.Boolean false |]) |]) |]
    Assert.Equal(expected, YamlValue.Parse json)

[<Fact>]
let ``JSON escaped strings`` () =
    let json = "\"line1\\nline2\\ttabbed\\\"quoted\\\"\\\\backslash\""
    let expected = YamlValue.String "line1\nline2\ttabbed\"quoted\"\\backslash"
    Assert.Equal(expected, YamlValue.Parse json)

[<Fact>]
let ``JSON unicode escape`` () =
    let json = "\"\\u00e9\""
    Assert.Equal(YamlValue.String "\u00e9", YamlValue.Parse json)

[<Fact>]
let ``JSON numbers - integers negative decimals exponents`` () =
    Assert.Equal(YamlValue.Number 0m, YamlValue.Parse "0")
    Assert.Equal(YamlValue.Number -17m, YamlValue.Parse "-17")
    Assert.Equal(YamlValue.Number 3.14m, YamlValue.Parse "3.14")
    Assert.Equal(YamlValue.Number -0.5m, YamlValue.Parse "-0.5")
    // 1.5e10 = 15000000000, exactly representable in decimal, so it resolves to Number.
    Assert.Equal(YamlValue.Number 15000000000m, YamlValue.Parse "1.5e10")
    Assert.Equal(YamlValue.Number 2e2m, YamlValue.Parse "2E2")

[<Fact>]
let ``JSON true false null`` () =
    Assert.Equal(YamlValue.Boolean true, YamlValue.Parse "true")
    Assert.Equal(YamlValue.Boolean false, YamlValue.Parse "false")
    Assert.Equal(YamlValue.Null, YamlValue.Parse "null")

[<Fact>]
let ``JSON empty object and array`` () =
    Assert.Equal(YamlValue.Mapping [||], YamlValue.Parse "{}")
    Assert.Equal(YamlValue.Sequence [||], YamlValue.Parse "[]")

[<Fact>]
let ``JSON array of objects`` () =
    let json = """[{"id": 1}, {"id": 2}]"""
    let expected =
        YamlValue.Sequence
            [| YamlValue.Mapping [| (YamlValue.String "id", YamlValue.Number 1m) |]
               YamlValue.Mapping [| (YamlValue.String "id", YamlValue.Number 2m) |] |]
    Assert.Equal(expected, YamlValue.Parse json)

// ---------------------------------------------------------------------
// Error cases
// ---------------------------------------------------------------------

[<Fact>]
let ``unclosed flow mapping raises`` () =
    Assert.Throws<YamlParseException>(fun () -> YamlValue.Parse "{a: 1" |> ignore) |> ignore

[<Fact>]
let ``unclosed flow sequence raises`` () =
    Assert.Throws<YamlParseException>(fun () -> YamlValue.Parse "[1, 2" |> ignore) |> ignore

[<Fact>]
let ``unclosed single-quoted scalar raises`` () =
    Assert.Throws<YamlParseException>(fun () -> YamlValue.Parse "'unterminated" |> ignore) |> ignore

[<Fact>]
let ``unclosed double-quoted scalar raises`` () =
    Assert.Throws<YamlParseException>(fun () -> YamlValue.Parse "\"unterminated" |> ignore) |> ignore

[<Fact>]
let ``missing separator between mapping entries raises`` () =
    // After "a" (implicit-null key) the parser expects ',' or '}', not another value.
    Assert.Throws<YamlParseException>(fun () -> YamlValue.Parse "{\"a\" \"b\": 1}" |> ignore) |> ignore

[<Fact>]
let ``bare colon is not a value raises`` () =
    Assert.Throws<YamlParseException>(fun () -> YamlValue.Parse "[:]" |> ignore) |> ignore

[<Fact>]
let ``trailing content after document raises`` () =
    Assert.Throws<YamlParseException>(fun () -> YamlValue.Parse "[1] [2]" |> ignore) |> ignore

[<Fact>]
let ``invalid escape sequence in double-quoted scalar raises with reasonable position`` () =
    let ex = Assert.Throws<YamlParseException>(fun () -> YamlValue.Parse "\"bad \\q escape\"" |> ignore)
    Assert.Equal(1, ex.Line)
