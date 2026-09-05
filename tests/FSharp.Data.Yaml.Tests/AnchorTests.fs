module FSharp.Data.Yaml.Tests.AnchorTests

open System
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

/// Like `prop`, but `None` when the mapping doesn't contain the key (still fails if `value` isn't
/// a mapping at all).
let private tryProp (name: string) (value: YamlValue) : YamlValue option =
    match value with
    | YamlValue.Mapping pairs -> pairs |> Array.tryFind (fun (k, _) -> k = YamlValue.String name) |> Option.map snd
    | other -> failwithf "Expected a Mapping to look up '%s', got %A" name other

// ---------------------------------------------------------------------
// Basic anchor + alias, on every node shape
// ---------------------------------------------------------------------

[<Fact>]
let ``anchor and alias on a scalar`` () =
    let doc = "a: &x hello\nb: *x\n"
    let result = YamlValue.Parse doc
    Assert.Equal(YamlValue.String "hello", prop "a" result)
    Assert.Equal(YamlValue.String "hello", prop "b" result)

[<Fact>]
let ``anchor and alias on a flow mapping`` () =
    let doc = "a: &x {p: 1, q: 2}\nb: *x\n"
    let result = YamlValue.Parse doc
    let expected =
        YamlValue.Mapping [| (YamlValue.String "p", YamlValue.Number 1m); (YamlValue.String "q", YamlValue.Number 2m) |]
    Assert.Equal(expected, prop "a" result)
    Assert.Equal(expected, prop "b" result)

[<Fact>]
let ``anchor and alias on a flow sequence`` () =
    let doc = "a: &x [1, 2, 3]\nb: *x\n"
    let result = YamlValue.Parse doc
    let expected = YamlValue.Sequence [| YamlValue.Number 1m; YamlValue.Number 2m; YamlValue.Number 3m |]
    Assert.Equal(expected, prop "a" result)
    Assert.Equal(expected, prop "b" result)

[<Fact>]
let ``anchor and alias on a block mapping`` () =
    let doc = "a: &x\n  p: 1\n  q: 2\nb: *x\n"
    let result = YamlValue.Parse doc
    let expected =
        YamlValue.Mapping [| (YamlValue.String "p", YamlValue.Number 1m); (YamlValue.String "q", YamlValue.Number 2m) |]
    Assert.Equal(expected, prop "a" result)
    Assert.Equal(expected, prop "b" result)

[<Fact>]
let ``anchor and alias on a block sequence`` () =
    let doc = "a: &x\n  - 1\n  - 2\nb: *x\n"
    let result = YamlValue.Parse doc
    let expected = YamlValue.Sequence [| YamlValue.Number 1m; YamlValue.Number 2m |]
    Assert.Equal(expected, prop "a" result)
    Assert.Equal(expected, prop "b" result)

[<Fact>]
let ``anchor and alias directly on a block sequence entry`` () =
    let doc = "- &x foo\n- *x\n"
    let result = YamlValue.Parse doc
    let expected = YamlValue.Sequence [| YamlValue.String "foo"; YamlValue.String "foo" |]
    Assert.Equal(expected, result)

// ---------------------------------------------------------------------
// Sharing, not copying
// ---------------------------------------------------------------------

[<Fact>]
let ``multiple aliases to the same scalar anchor both resolve`` () =
    let doc = "src: &a hello\nlist: [*a, *a]\n"
    let result = YamlValue.Parse doc
    match prop "list" result with
    | YamlValue.Sequence [| x; y |] ->
        Assert.Equal(YamlValue.String "hello", x)
        Assert.Equal(YamlValue.String "hello", y)
    | other -> failwithf "Expected a two-element sequence, got %A" other

[<Fact>]
let ``aliases to a collection anchor share the same underlying array, not a copy`` () =
    // &a's Sequence value is parsed exactly once; every alias site must reuse the very same
    // YamlValue.Sequence (and thus the same backing array instance), not a structurally-equal
    // deep copy — this is what makes `&a [*a, *a]`-style sharing free at parse time.
    let doc = "src: &a [1, 2, 3]\nlist: [*a, *a]\n"
    let result = YamlValue.Parse doc
    let srcValue = prop "src" result
    match prop "list" result with
    | YamlValue.Sequence [| x; y |] ->
        let arrOf v =
            match v with
            | YamlValue.Sequence arr -> arr
            | _ -> failwith "expected a sequence"
        Assert.True(obj.ReferenceEquals(arrOf srcValue, arrOf x))
        Assert.True(obj.ReferenceEquals(arrOf srcValue, arrOf y))
    | other -> failwithf "Expected a two-element sequence, got %A" other

// ---------------------------------------------------------------------
// Recursive anchor guard
// ---------------------------------------------------------------------

[<Fact>]
let ``direct self-referencing alias raises a parse error`` () =
    Assert.Throws<YamlParseException>(fun () -> YamlValue.Parse "&a [*a]" |> ignore)
    |> ignore

[<Fact>]
let ``indirect two-hop cycle raises a parse error`` () =
    // *y can't be defined yet while &x's node is still being parsed (aliases only ever refer
    // backwards to an already-completed anchor in a single top-to-bottom pass), so this raises
    // either as an unknown-anchor error or an in-flight recursive one — either way it's a
    // YamlParseException, which is all that's guaranteed to be representable here.
    let doc = "a: &x\n  b: *y\nc: &y\n  d: *x\n"
    Assert.Throws<YamlParseException>(fun () -> YamlValue.Parse doc |> ignore) |> ignore

[<Fact>]
let ``unknown alias raises a parse error`` () =
    Assert.Throws<YamlParseException>(fun () -> YamlValue.Parse "a: *nope" |> ignore)
    |> ignore

// ---------------------------------------------------------------------
// Merge keys
// ---------------------------------------------------------------------

[<Fact>]
let ``merge key with a single source merges missing keys and keeps explicit overrides`` () =
    let doc = "base: &base\n  a: 1\n  b: 2\nderived:\n  <<: *base\n  b: 3\n"
    let result = YamlValue.Parse doc
    let derived = prop "derived" result
    Assert.Equal(YamlValue.Number 1m, prop "a" derived)
    Assert.Equal(YamlValue.Number 3m, prop "b" derived)

[<Fact>]
let ``merge key with multiple sources via a sequence merges all of them`` () =
    let doc =
        "base1: &b1\n  x: 1\n  y: 1\nbase2: &b2\n  y: 2\n  z: 2\nderived:\n  <<: [*b1, *b2]\n  x: 99\n"
    let result = YamlValue.Parse doc
    let derived = prop "derived" result
    // Explicit key wins outright.
    Assert.Equal(YamlValue.Number 99m, prop "x" derived)
    // y is defined by both merge sources — the earlier source (*b1) wins.
    Assert.Equal(YamlValue.Number 1m, prop "y" derived)
    // z only appears in the later source.
    Assert.Equal(YamlValue.Number 2m, prop "z" derived)

[<Fact>]
let ``merge key in flow mapping style also expands`` () =
    let doc = "src: &b {a: 1, b: 2}\nderived: {<<: *b, b: 9}\n"
    let result = YamlValue.Parse doc
    let derived = prop "derived" result
    Assert.Equal(YamlValue.Number 1m, prop "a" derived)
    Assert.Equal(YamlValue.Number 9m, prop "b" derived)

[<Fact>]
let ``merge keys can be disabled via the parse option`` () =
    let doc = "base: &base\n  a: 1\nderived:\n  <<: *base\n  own: 2\n"
    let result = YamlValue.Parse(doc, disableMergeKeys = true)
    let derived = prop "derived" result
    Assert.Equal(None, tryProp "a" derived)
    Assert.Equal(Some(YamlValue.Number 2m), tryProp "own" derived)
    Assert.True((tryProp "<<" derived).IsSome)

// ---------------------------------------------------------------------
// Core tag overrides
// ---------------------------------------------------------------------

[<Fact>]
let ``!!str forces a numeric-looking scalar to remain a string`` () =
    Assert.Equal(YamlValue.String "123", YamlValue.Parse "!!str 123")

[<Fact>]
let ``!!str forces a bool-looking scalar to remain a string`` () =
    Assert.Equal(YamlValue.String "true", YamlValue.Parse "!!str true")

[<Fact>]
let ``!!int forces resolution to a number`` () =
    Assert.Equal(YamlValue.Number 42m, YamlValue.Parse "!!int 42")

[<Fact>]
let ``!!float forces resolution to a float`` () =
    match YamlValue.Parse "!!float 42" with
    | YamlValue.Float f -> Assert.Equal(42.0, f)
    | other -> failwithf "Expected a Float, got %A" other

[<Fact>]
let ``!!bool forces resolution to a boolean`` () =
    Assert.Equal(YamlValue.Boolean true, YamlValue.Parse "!!bool true")
    Assert.Equal(YamlValue.Boolean false, YamlValue.Parse "!!bool false")

[<Fact>]
let ``!!null forces resolution to null`` () =
    Assert.Equal(YamlValue.Null, YamlValue.Parse "!!null whatever")

[<Fact>]
let ``!!timestamp forces resolution to a timestamp`` () =
    match YamlValue.Parse "!!timestamp 2024-01-30" with
    | YamlValue.Timestamp dto -> Assert.Equal(DateTimeOffset(2024, 1, 30, 0, 0, 0, TimeSpan.Zero), dto)
    | other -> failwithf "Expected a Timestamp, got %A" other

[<Fact>]
let ``!!map on an already-mapping node is a no-op`` () =
    let expected = YamlValue.Mapping [| (YamlValue.String "x", YamlValue.Number 1m) |]
    Assert.Equal(expected, YamlValue.Parse "!!map {x: 1}")

[<Fact>]
let ``!!seq on an already-sequence node is a no-op`` () =
    let expected = YamlValue.Sequence [| YamlValue.Number 1m; YamlValue.Number 2m |]
    Assert.Equal(expected, YamlValue.Parse "!!seq [1, 2]")

[<Fact>]
let ``unknown custom tag falls back to normal resolution without erroring`` () =
    Assert.Equal(YamlValue.String "bar", YamlValue.Parse "!foo bar")

[<Fact>]
let ``verbatim tag falls back to normal resolution without erroring`` () =
    Assert.Equal(YamlValue.String "bar", YamlValue.Parse "!<tag:example.com,2000:foo> bar")

[<Fact>]
let ``core tag inside a block mapping value`` () =
    let doc = "a: !!int \"42\"\n"
    Assert.Equal(YamlValue.Number 42m, prop "a" (YamlValue.Parse doc))
