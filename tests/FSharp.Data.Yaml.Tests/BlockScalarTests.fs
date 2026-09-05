module FSharp.Data.Yaml.Tests.BlockScalarTests

open Xunit
open FSharp.Data

// ---------------------------------------------------------------------
// `|` literal block scalars — chomping
// ---------------------------------------------------------------------

[<Fact>]
let ``literal block scalar with clip chomping keeps exactly one trailing newline`` () =
    let doc = "text: |\n  line one\n  line two\n"
    let expected = YamlValue.Mapping [| (YamlValue.String "text", YamlValue.String "line one\nline two\n") |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``literal block scalar with strip chomping has no trailing newline`` () =
    let doc = "text: |-\n  line one\n  line two\n"
    let expected = YamlValue.Mapping [| (YamlValue.String "text", YamlValue.String "line one\nline two") |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``literal block scalar with keep chomping preserves trailing blank lines`` () =
    let doc = "text: |+\n  line one\n  line two\n\n\n"
    let expected =
        YamlValue.Mapping [| (YamlValue.String "text", YamlValue.String "line one\nline two\n\n\n") |]
    Assert.Equal(expected, YamlValue.Parse doc)

// ---------------------------------------------------------------------
// `>` folded block scalars — chomping
// ---------------------------------------------------------------------

[<Fact>]
let ``folded block scalar with clip chomping folds single breaks to spaces`` () =
    let doc = "text: >\n  line one\n  line two\n"
    let expected = YamlValue.Mapping [| (YamlValue.String "text", YamlValue.String "line one line two\n") |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``folded block scalar with strip chomping has no trailing newline`` () =
    let doc = "text: >-\n  line one\n  line two\n"
    let expected = YamlValue.Mapping [| (YamlValue.String "text", YamlValue.String "line one line two") |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``folded block scalar with keep chomping preserves trailing blank lines`` () =
    let doc = "text: >+\n  line one\n  line two\n\n\n"
    let expected =
        YamlValue.Mapping [| (YamlValue.String "text", YamlValue.String "line one line two\n\n\n") |]
    Assert.Equal(expected, YamlValue.Parse doc)

// ---------------------------------------------------------------------
// Explicit indentation indicators, both orders, both styles
// ---------------------------------------------------------------------

[<Fact>]
let ``literal block scalar with explicit indent indicator after chomp`` () =
    // Content is genuinely indented 4 spaces; the indicator fixes the strip width at 2 (relative
    // to the parent's indent of 0), so 2 spaces of "extra" indentation are retained per line.
    let doc = "text: |-2\n    a\n    b\n"
    let expected = YamlValue.Mapping [| (YamlValue.String "text", YamlValue.String "  a\n  b") |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``literal block scalar with explicit indent indicator before chomp`` () =
    let doc = "text: |2-\n    a\n    b\n"
    let expected = YamlValue.Mapping [| (YamlValue.String "text", YamlValue.String "  a\n  b") |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``folded block scalar with explicit indent indicator handles leading blank line`` () =
    // Without an indicator, auto-detection would use the first non-blank line; here we force the
    // indent explicitly so a leading blank line poses no ambiguity.
    let doc = "text: >2\n\n  a\n  b\n"
    let expected = YamlValue.Mapping [| (YamlValue.String "text", YamlValue.String "\na b\n") |]
    Assert.Equal(expected, YamlValue.Parse doc)

// ---------------------------------------------------------------------
// Blank lines: leading, trailing, interior
// ---------------------------------------------------------------------

[<Fact>]
let ``literal block scalar with leading blank line auto-detects indent from first content line`` () =
    let doc = "text: |\n\n  a\n  b\n"
    let expected = YamlValue.Mapping [| (YamlValue.String "text", YamlValue.String "\na\nb\n") |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``literal block scalar with interior blank line keeps it literally`` () =
    let doc = "text: |\n  a\n\n  b\n"
    let expected = YamlValue.Mapping [| (YamlValue.String "text", YamlValue.String "a\n\nb\n") |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``folded block scalar with one interior blank line yields one newline`` () =
    let doc = "text: >\n  a\n\n  b\n"
    let expected = YamlValue.Mapping [| (YamlValue.String "text", YamlValue.String "a\nb\n") |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``folded block scalar with two interior blank lines yields two newlines`` () =
    let doc = "text: >\n  a\n\n\n  b\n"
    let expected = YamlValue.Mapping [| (YamlValue.String "text", YamlValue.String "a\n\nb\n") |]
    Assert.Equal(expected, YamlValue.Parse doc)

// ---------------------------------------------------------------------
// More-indented lines are not folded (`>` only)
// ---------------------------------------------------------------------

[<Fact>]
let ``folded block scalar does not fold more-indented lines`` () =
    let doc = "text: >\n  normal\n    indented one\n    indented two\n  normal again\n"
    let expected =
        YamlValue.Mapping
            [| (YamlValue.String "text",
                YamlValue.String "normal\n  indented one\n  indented two\nnormal again\n") |]
    Assert.Equal(expected, YamlValue.Parse doc)

// ---------------------------------------------------------------------
// Multi-line plain scalars in block context
// ---------------------------------------------------------------------

[<Fact>]
let ``multi-line plain scalar folds continuation lines to spaces`` () =
    let doc = "text: this is\n  a folded\n  scalar\n"
    let expected = YamlValue.Mapping [| (YamlValue.String "text", YamlValue.String "this is a folded scalar") |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``multi-line plain scalar continuation may be indented less than the scalar's own start`` () =
    let doc = "key: first\n  second\n"
    let expected = YamlValue.Mapping [| (YamlValue.String "key", YamlValue.String "first second") |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``multi-line plain scalar with a blank line folds to a single newline`` () =
    let doc = "text: first\n\n  second\n"
    let expected = YamlValue.Mapping [| (YamlValue.String "text", YamlValue.String "first\nsecond") |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``multi-line plain scalar terminates on dedent to sibling key`` () =
    let doc = "a: first\n  second\nb: 2\n"
    let expected =
        YamlValue.Mapping
            [| (YamlValue.String "a", YamlValue.String "first second")
               (YamlValue.String "b", YamlValue.Number 2m) |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``multi-line plain scalar in a sequence entry`` () =
    let doc = "- first\n  second\n- third\n"
    let expected = YamlValue.Sequence [| YamlValue.String "first second"; YamlValue.String "third" |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``multi-line top-level plain scalar with no block structure`` () =
    Assert.Equal(YamlValue.String "hello there world", YamlValue.Parse "hello there\nworld")

// ---------------------------------------------------------------------
// Multi-line quoted scalars in block context
// ---------------------------------------------------------------------

[<Fact>]
let ``multi-line single-quoted scalar folds continuation lines`` () =
    let doc = "text: 'first\n  second'\n"
    let expected = YamlValue.Mapping [| (YamlValue.String "text", YamlValue.String "first second") |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``multi-line double-quoted scalar folds continuation lines`` () =
    let doc = "text: \"first\n  second\"\n"
    let expected = YamlValue.Mapping [| (YamlValue.String "text", YamlValue.String "first second") |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``multi-line double-quoted scalar still processes escapes across folded lines`` () =
    let doc = "text: \"a\\tb\n  c\\nd\"\n"
    let expected = YamlValue.Mapping [| (YamlValue.String "text", YamlValue.String "a\tb c\nd") |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``multi-line double-quoted scalar honors backslash line continuation without folding`` () =
    // A `\` immediately before the line break is a genuine escaped continuation: it and the
    // following line's leading whitespace contribute nothing, unlike ordinary folding which would
    // insert a space.
    let doc = "text: \"a\\\n  b\"\n"
    let expected = YamlValue.Mapping [| (YamlValue.String "text", YamlValue.String "ab") |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``multi-line double-quoted scalar with blank line folds to a newline`` () =
    let doc = "text: \"first\n\n  second\"\n"
    let expected = YamlValue.Mapping [| (YamlValue.String "text", YamlValue.String "first\nsecond") |]
    Assert.Equal(expected, YamlValue.Parse doc)

// ---------------------------------------------------------------------
// Nested inside block mappings/sequences
// ---------------------------------------------------------------------

[<Fact>]
let ``block scalar as a mapping value nested under another mapping`` () =
    let doc = "outer:\n  text: |\n    a\n    b\n"
    let expected =
        YamlValue.Mapping
            [| (YamlValue.String "outer", YamlValue.Mapping [| (YamlValue.String "text", YamlValue.String "a\nb\n") |]) |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``block scalar as a sequence entry`` () =
    let doc = "- |\n  a\n  b\n- |\n  c\n"
    let expected = YamlValue.Sequence [| YamlValue.String "a\nb\n"; YamlValue.String "c\n" |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``folded block scalar as a sequence entry followed by a sibling entry`` () =
    let doc = "- >\n  a\n  b\n- c\n"
    let expected = YamlValue.Sequence [| YamlValue.String "a b\n"; YamlValue.String "c" |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``block scalar mapping value followed by a sibling mapping key`` () =
    let doc = "text: |\n  a\n  b\nnext: 2\n"
    let expected =
        YamlValue.Mapping
            [| (YamlValue.String "text", YamlValue.String "a\nb\n")
               (YamlValue.String "next", YamlValue.Number 2m) |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``block scalar as a compact sequence-of-mappings value`` () =
    let doc = "- name: a\n  desc: |\n    line one\n    line two\n- name: b\n  desc: |\n    single\n"
    let expected =
        YamlValue.Sequence
            [| YamlValue.Mapping
                   [| (YamlValue.String "name", YamlValue.String "a")
                      (YamlValue.String "desc", YamlValue.String "line one\nline two\n") |]
               YamlValue.Mapping
                   [| (YamlValue.String "name", YamlValue.String "b")
                      (YamlValue.String "desc", YamlValue.String "single\n") |] |]
    Assert.Equal(expected, YamlValue.Parse doc)

// ---------------------------------------------------------------------
// Empty / degenerate block scalars
// ---------------------------------------------------------------------

[<Fact>]
let ``empty literal block scalar with clip chomping`` () =
    let doc = "text: |\nnext: 2\n"
    let expected =
        YamlValue.Mapping
            [| (YamlValue.String "text", YamlValue.String "")
               (YamlValue.String "next", YamlValue.Number 2m) |]
    Assert.Equal(expected, YamlValue.Parse doc)

[<Fact>]
let ``top-level literal block scalar document`` () =
    let doc = "|\n  a\n  b\n"
    Assert.Equal(YamlValue.String "a\nb\n", YamlValue.Parse doc)
