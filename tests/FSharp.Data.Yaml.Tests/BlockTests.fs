module FSharp.Data.Yaml.Tests.BlockTests

open System
open Xunit
open FSharp.Data

// ---------------------------------------------------------------------
// Flat block mappings and sequences
// ---------------------------------------------------------------------

[<Fact>]
let ``simple flat block mapping`` () =
    let doc = "name: Tomas\nborn: 1985\n"
    let expected =
        YamlValue.Mapping
            [| (YamlValue.String "name", YamlValue.String "Tomas")
               (YamlValue.String "born", YamlValue.Number 1985m) |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``simple flat block sequence`` () =
    let doc = "- a\n- b\n- c\n"
    let expected = YamlValue.Sequence [| YamlValue.String "a"; YamlValue.String "b"; YamlValue.String "c" |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``block mapping with null values from missing content`` () =
    let doc = "a:\nb: 1\n"
    let expected =
        YamlValue.Mapping [| (YamlValue.String "a", YamlValue.Null); (YamlValue.String "b", YamlValue.Number 1m) |]
    Assert.Equal(expected, YamlValue.Parse doc)

// ---------------------------------------------------------------------
// Nested mappings/sequences at various indent widths
// ---------------------------------------------------------------------

[<Fact>]
let ``nested block mapping two-space indent`` () =
    let doc = "outer:\n  inner: 1\n  other: 2\n"
    let expected =
        YamlValue.Mapping
            [| (YamlValue.String "outer",
                YamlValue.Mapping
                    [| (YamlValue.String "inner", YamlValue.Number 1m)
                       (YamlValue.String "other", YamlValue.Number 2m) |]) |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``nested block mapping four-space indent`` () =
    let doc = "outer:\n    inner: 1\n    other: 2\n"
    let expected =
        YamlValue.Mapping
            [| (YamlValue.String "outer",
                YamlValue.Mapping
                    [| (YamlValue.String "inner", YamlValue.Number 1m)
                       (YamlValue.String "other", YamlValue.Number 2m) |]) |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``nested block mapping three-space indent`` () =
    let doc = "outer:\n   inner: 1\n"
    let expected =
        YamlValue.Mapping
            [| (YamlValue.String "outer", YamlValue.Mapping [| (YamlValue.String "inner", YamlValue.Number 1m) |]) |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``deeply nested block mappings`` () =
    let doc = "a:\n  b:\n    c:\n      d: 1\n"
    let parsed = YamlValue.Parse doc
    match parsed with
    | YamlValue.Mapping [| (YamlValue.String "a",
                            YamlValue.Mapping [| (YamlValue.String "b",
                                                  YamlValue.Mapping [| (YamlValue.String "c",
                                                                        YamlValue.Mapping [| (YamlValue.String "d", YamlValue.Number 1m) |]) |]) |]) |] -> ()
    | other -> failwithf "unexpected shape: %A" other

[<Fact>]
let ``block sequence of block sequences`` () =
    let doc = "- - 1\n  - 2\n- - 3\n"
    let expected =
        YamlValue.Sequence
            [| YamlValue.Sequence [| YamlValue.Number 1m; YamlValue.Number 2m |]
               YamlValue.Sequence [| YamlValue.Number 3m |] |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``nested block sequence under a mapping key`` () =
    let doc = "items:\n  - a\n  - b\n"
    let expected =
        YamlValue.Mapping
            [| (YamlValue.String "items", YamlValue.Sequence [| YamlValue.String "a"; YamlValue.String "b" |]) |]
    Assert.Equal(expected, YamlValue.Parse doc)

// ---------------------------------------------------------------------
// Compact notation
// ---------------------------------------------------------------------

[<Fact>]
let ``compact mapping entry in sequence`` () =
    let doc = "- a: 1\n  b: 2\n- c: 3\n"
    let expected =
        YamlValue.Sequence
            [| YamlValue.Mapping [| (YamlValue.String "a", YamlValue.Number 1m); (YamlValue.String "b", YamlValue.Number 2m) |]
               YamlValue.Mapping [| (YamlValue.String "c", YamlValue.Number 3m) |] |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``compact nested sequence entry`` () =
    let doc = "- - a\n  - b\n"
    let expected =
        YamlValue.Sequence [| YamlValue.Sequence [| YamlValue.String "a"; YamlValue.String "b" |] |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``compact notation combinations - sequence of compact mappings with nested sequence value`` () =
    let doc = "- name: a\n  tags:\n    - x\n    - y\n- name: b\n  tags: []\n"
    let expected =
        YamlValue.Sequence
            [| YamlValue.Mapping
                   [| (YamlValue.String "name", YamlValue.String "a")
                      (YamlValue.String "tags", YamlValue.Sequence [| YamlValue.String "x"; YamlValue.String "y" |]) |]
               YamlValue.Mapping
                   [| (YamlValue.String "name", YamlValue.String "b")
                      (YamlValue.String "tags", YamlValue.Sequence [||]) |] |]
    Assert.Equal(expected, YamlValue.Parse doc)

// ---------------------------------------------------------------------
// Sequences of mappings, mappings of sequences, deeply mixed nesting
// ---------------------------------------------------------------------

[<Fact>]
let ``sequence of mappings`` () =
    let doc = "- id: 1\n  name: a\n- id: 2\n  name: b\n"
    let expected =
        YamlValue.Sequence
            [| YamlValue.Mapping [| (YamlValue.String "id", YamlValue.Number 1m); (YamlValue.String "name", YamlValue.String "a") |]
               YamlValue.Mapping [| (YamlValue.String "id", YamlValue.Number 2m); (YamlValue.String "name", YamlValue.String "b") |] |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``mapping of sequences`` () =
    let doc = "fruits:\n  - apple\n  - pear\nveggies:\n  - carrot\n"
    let expected =
        YamlValue.Mapping
            [| (YamlValue.String "fruits", YamlValue.Sequence [| YamlValue.String "apple"; YamlValue.String "pear" |])
               (YamlValue.String "veggies", YamlValue.Sequence [| YamlValue.String "carrot" |]) |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``deeply mixed nesting`` () =
    let doc =
        "services:\n"
        + "  web:\n"
        + "    ports:\n"
        + "      - 80\n"
        + "      - 443\n"
        + "    env:\n"
        + "      - name: DEBUG\n"
        + "        value: \"true\"\n"
    let parsed = YamlValue.Parse doc
    match parsed with
    | YamlValue.Mapping [| (YamlValue.String "services",
                            YamlValue.Mapping [| (YamlValue.String "web",
                                                  YamlValue.Mapping
                                                      [| (YamlValue.String "ports", YamlValue.Sequence [| YamlValue.Number 80m; YamlValue.Number 443m |])
                                                         (YamlValue.String "env",
                                                          YamlValue.Sequence
                                                              [| YamlValue.Mapping
                                                                     [| (YamlValue.String "name", YamlValue.String "DEBUG")
                                                                        (YamlValue.String "value", YamlValue.String "true") |] |]) |]) |]) |] -> ()
    | other -> failwithf "unexpected shape: %A" other

// ---------------------------------------------------------------------
// Explicit `? key` / `: value` pairs
// ---------------------------------------------------------------------

[<Fact>]
let ``explicit key value pair on separate lines`` () =
    let doc = "? key\n: value\n"
    let expected = YamlValue.Mapping [| (YamlValue.String "key", YamlValue.String "value") |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``explicit key value pair on same line`` () =
    let doc = "? key : value\n"
    let expected = YamlValue.Mapping [| (YamlValue.String "key", YamlValue.String "value") |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``explicit key with flow collection key`` () =
    let doc = "? [1, 2]\n: pair\n"
    let expected =
        YamlValue.Mapping
            [| (YamlValue.Sequence [| YamlValue.Number 1m; YamlValue.Number 2m |], YamlValue.String "pair") |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``explicit key mixed with implicit entries`` () =
    let doc = "a: 1\n? b\n: 2\n"
    let expected =
        YamlValue.Mapping [| (YamlValue.String "a", YamlValue.Number 1m); (YamlValue.String "b", YamlValue.Number 2m) |]
    Assert.Equal(expected, YamlValue.Parse doc)

// ---------------------------------------------------------------------
// Flow nodes embedded in block context
// ---------------------------------------------------------------------

[<Fact>]
let ``block mapping value is a flow mapping`` () =
    let doc = "key: {a: 1, b: 2}\n"
    let expected =
        YamlValue.Mapping
            [| (YamlValue.String "key",
                YamlValue.Mapping [| (YamlValue.String "a", YamlValue.Number 1m); (YamlValue.String "b", YamlValue.Number 2m) |]) |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``block sequence entry is a flow sequence`` () =
    let doc = "- [1, 2, 3]\n- [4]\n"
    let expected =
        YamlValue.Sequence
            [| YamlValue.Sequence [| YamlValue.Number 1m; YamlValue.Number 2m; YamlValue.Number 3m |]
               YamlValue.Sequence [| YamlValue.Number 4m |] |]
    Assert.Equal(expected, YamlValue.Parse doc)

// ---------------------------------------------------------------------
// Non-string keys
// ---------------------------------------------------------------------

[<Fact>]
let ``integer key in block mapping`` () =
    let doc = "42: the answer\n"
    let expected = YamlValue.Mapping [| (YamlValue.Number 42m, YamlValue.String "the answer") |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``boolean key in block mapping`` () =
    let doc = "true: yes value\nfalse: no value\n"
    let expected =
        YamlValue.Mapping
            [| (YamlValue.Boolean true, YamlValue.String "yes value")
               (YamlValue.Boolean false, YamlValue.String "no value") |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``float key in block mapping`` () =
    let doc = "3.14: pi\n"
    let expected = YamlValue.Mapping [| (YamlValue.Number 3.14m, YamlValue.String "pi") |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``non-string key via explicit form`` () =
    let doc = "? 7\n: seven\n"
    let expected = YamlValue.Mapping [| (YamlValue.Number 7m, YamlValue.String "seven") |]
    Assert.Equal(expected, YamlValue.Parse doc)

// ---------------------------------------------------------------------
// Comments and blank lines interspersed
// ---------------------------------------------------------------------

[<Fact>]
let ``comments and blank lines between mapping entries`` () =
    let doc = "a: 1\n\n# a comment\n\nb: 2  # trailing comment\nc: 3\n"
    let expected =
        YamlValue.Mapping
            [| (YamlValue.String "a", YamlValue.Number 1m)
               (YamlValue.String "b", YamlValue.Number 2m)
               (YamlValue.String "c", YamlValue.Number 3m) |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``comments and blank lines between sequence entries`` () =
    let doc = "- a\n# comment\n\n- b\n- c  # trailing\n"
    let expected = YamlValue.Sequence [| YamlValue.String "a"; YamlValue.String "b"; YamlValue.String "c" |]
    Assert.Equal(expected, YamlValue.Parse doc)

// ---------------------------------------------------------------------
// Bare top-level plain scalar
// ---------------------------------------------------------------------

[<Fact>]
let ``bare top-level plain scalar document with no block structure`` () =
    Assert.Equal(YamlValue.String "hello world", YamlValue.Parse "hello world")

[<Fact>]
let ``bare top-level plain scalar resolves per core schema`` () =
    Assert.Equal(YamlValue.Number 42m, YamlValue.Parse "42")
    Assert.Equal(YamlValue.Boolean true, YamlValue.Parse "true")

// ---------------------------------------------------------------------
// Error cases: inconsistent / impossible indentation
// ---------------------------------------------------------------------

[<Fact>]
let ``misaligned sibling indentation raises`` () =
    let doc = "a:\n  b: 1\n b: 2\n"
    Assert.Throws<YamlParseException>(fun () -> YamlValue.Parse doc |> ignore) |> ignore

[<Fact>]
let ``deeper indentation than any enclosing level raises`` () =
    let doc = "a: 1\n    b: 2\n"
    Assert.Throws<YamlParseException>(fun () -> YamlValue.Parse doc |> ignore) |> ignore

[<Fact>]
let ``mixing sequence dash with mapping key at same indent raises`` () =
    let doc = "- a\nb: 1\n"
    Assert.Throws<YamlParseException>(fun () -> YamlValue.Parse doc |> ignore) |> ignore

[<Fact>]
let ``sequence entry indented less than parent raises`` () =
    let doc = "a:\n  - 1\n - 2\n"
    Assert.Throws<YamlParseException>(fun () -> YamlValue.Parse doc |> ignore) |> ignore

[<Fact>]
let ``tab in indentation raises`` () =
    let doc = "a:\n\tb: 1\n"
    Assert.Throws<YamlParseException>(fun () -> YamlValue.Parse doc |> ignore) |> ignore
