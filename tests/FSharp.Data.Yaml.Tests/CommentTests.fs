module FSharp.Data.Yaml.Tests.CommentTests

open Xunit
open FSharp.Data

// `YamlExtensions` (the `?` operator, `GetProperty`, etc.) is Phase 10 — not implemented yet, so
// tests here look up mapping properties directly against the `(YamlValue * YamlValue)[]` shape,
// matching the convention already used by `DocumentTests.fs`.

/// Looks up a string-keyed property of a `YamlValue.Mapping`, failing the test if the value isn't
/// a mapping or doesn't contain the key.
let private prop (name: string) (value: YamlValue) : YamlValue =
    match value with
    | YamlValue.Mapping pairs ->
        match pairs |> Array.tryFind (fun (k, _) -> k = YamlValue.String name) with
        | Some(_, v) -> v
        | None -> failwithf "Mapping has no property '%s': %A" name value
    | other -> failwithf "Expected a Mapping to look up '%s', got %A" name other

/// The `YamlNodeComments` attached to `path`, failing the test if none was recorded.
let private commentsAt (path: YamlPath) (doc: YamlDocument) : YamlNodeComments =
    match Map.tryFind path doc.Comments with
    | Some c -> c
    | None -> failwithf "No comments recorded at path %A. Comments = %A" path doc.Comments

// ---------------------------------------------------------------------
// Root-level leading comments
// ---------------------------------------------------------------------

[<Fact>]
let ``a leading comment before the document's top node is attached to the root path`` () =
    let doc = YamlDocument.Parse "# a root comment\nname: Tomas\n"
    let comments = commentsAt [] doc
    Assert.Equal<string list>([ "a root comment" ], comments.Leading)
    Assert.Equal(None, comments.Trailing)

[<Fact>]
let ``multiple consecutive leading comments before the root node are all captured in order`` () =
    let doc = YamlDocument.Parse "# first\n# second\nname: Tomas\n"
    let comments = commentsAt [] doc
    Assert.Equal<string list>([ "first"; "second" ], comments.Leading)

// ---------------------------------------------------------------------
// Leading comments before a block mapping entry
// ---------------------------------------------------------------------

[<Fact>]
let ``a leading comment before a (non-first) block mapping entry is keyed to that entry's path`` () =
    let doc = YamlDocument.Parse "name: Tomas\n# given name is important\nage: 30\n"
    let comments = commentsAt [ Key(YamlValue.String "age") ] doc
    Assert.Equal<string list>([ "given name is important" ], comments.Leading)
    // And it must NOT leak onto the sibling entry that already existed.
    Assert.False(doc.Comments.ContainsKey [ Key(YamlValue.String "name") ])

[<Fact>]
let ``multiple consecutive leading comments before a block mapping entry are all captured in order`` () =
    let doc =
        YamlDocument.Parse "name: Tomas\n# note one\n# note two\nage: 30\n"

    let comments = commentsAt [ Key(YamlValue.String "age") ] doc
    Assert.Equal<string list>([ "note one"; "note two" ], comments.Leading)

// ---------------------------------------------------------------------
// Leading comments before a block sequence entry
// ---------------------------------------------------------------------

[<Fact>]
let ``a leading comment before a block sequence entry is keyed to that entry's Index path`` () =
    let doc = YamlDocument.Parse "items:\n  - one\n  # a note about two\n  - two\n"
    let comments = commentsAt [ Key(YamlValue.String "items"); Index 1 ] doc
    Assert.Equal<string list>([ "a note about two" ], comments.Leading)

[<Fact>]
let ``multiple consecutive leading comments before a block sequence entry are all captured`` () =
    let doc = YamlDocument.Parse "items:\n  - one\n  # note a\n  # note b\n  - two\n"
    let comments = commentsAt [ Key(YamlValue.String "items"); Index 1 ] doc
    Assert.Equal<string list>([ "note a"; "note b" ], comments.Leading)

// ---------------------------------------------------------------------
// Trailing same-line comments
// ---------------------------------------------------------------------

[<Fact>]
let ``a trailing same-line comment after a mapping entry's scalar value is attached to that entry's path`` () =
    let doc = YamlDocument.Parse "name: Tomas   # given name\nborn: 1985\n"
    let comments = commentsAt [ Key(YamlValue.String "name") ] doc
    Assert.Equal(Some "given name", comments.Trailing)
    // The next entry must not have inherited it.
    Assert.False(doc.Comments.ContainsKey [ Key(YamlValue.String "born") ])

[<Fact>]
let ``a trailing same-line comment after a sequence entry is attached to that entry's Index path`` () =
    let doc = YamlDocument.Parse "items:\n  - one   # first item\n  - two\n"
    let comments = commentsAt [ Key(YamlValue.String "items"); Index 0 ] doc
    Assert.Equal(Some "first item", comments.Trailing)

// ---------------------------------------------------------------------
// Nested structure — verify full path correctness
// ---------------------------------------------------------------------

[<Fact>]
let ``a comment on a deeply-nested entry carries the full path from the root`` () =
    let yaml =
        "services:\n  web:\n    ports:\n      - 80\n      # the admin port\n      - 8080\n"

    let doc = YamlDocument.Parse yaml
    let path =
        [ Key(YamlValue.String "services")
          Key(YamlValue.String "web")
          Key(YamlValue.String "ports")
          Index 1 ]

    let comments = commentsAt path doc
    Assert.Equal<string list>([ "the admin port" ], comments.Leading)

    // Sanity: the value tree itself is unaffected by comment capture.
    let webPorts = doc.Value |> prop "services" |> prop "web" |> prop "ports"
    Assert.Equal(YamlValue.Sequence [| YamlValue.Number 80m; YamlValue.Number 8080m |], webPorts)

[<Fact>]
let ``a trailing comment 2-3 levels deep is keyed to its own full path, not a shallower one`` () =
    let yaml = "a:\n  b:\n    c: 1   # deep note\n    d: 2\n"
    let doc = YamlDocument.Parse yaml
    let path =
        [ Key(YamlValue.String "a"); Key(YamlValue.String "b"); Key(YamlValue.String "c") ]

    let comments = commentsAt path doc
    Assert.Equal(Some "deep note", comments.Trailing)
    Assert.False(
        doc.Comments.ContainsKey
            [ Key(YamlValue.String "a"); Key(YamlValue.String "b"); Key(YamlValue.String "d") ]
    )

// ---------------------------------------------------------------------
// Trailing comments after the document's last node
// ---------------------------------------------------------------------

[<Fact>]
let ``comments after the last node in the document are captured in YamlDocument.Trailing`` () =
    let doc = YamlDocument.Parse "name: Tomas\n# closing remark\n"
    Assert.Equal<string list>([ "closing remark" ], doc.Trailing)

[<Fact>]
let ``multiple trailing comments after the last node are all captured in order`` () =
    let doc = YamlDocument.Parse "name: Tomas\n# first\n# second\n"
    Assert.Equal<string list>([ "first"; "second" ], doc.Trailing)

[<Fact>]
let ``trailing comments after a --- ... end-marked document are still captured`` () =
    let doc = YamlDocument.Parse "name: Tomas\n...\n# after the end marker\n"
    Assert.Equal<string list>([ "after the end marker" ], doc.Trailing)

// ---------------------------------------------------------------------
// YamlValue.Parse stays comment-free
// ---------------------------------------------------------------------

[<Fact>]
let ``YamlValue.Parse on commented input still parses to the plain value, with no comment awareness`` () =
    let yaml = "# leading\nname: Tomas   # trailing\nage: 30\n# closing\n"
    let value = YamlValue.Parse yaml
    Assert.Equal(YamlValue.String "Tomas", prop "name" value)
    Assert.Equal(YamlValue.Number 30m, prop "age" value)

    // The same input via YamlDocument.Parse must produce an equal Value plus comments.
    let doc = YamlDocument.Parse yaml
    Assert.Equal(value, doc.Value)
    Assert.False(doc.Comments.IsEmpty)
    Assert.False(doc.Trailing.IsEmpty)

// ---------------------------------------------------------------------
// No comments at all
// ---------------------------------------------------------------------

[<Fact>]
let ``a document with no comments at all yields an empty Comments map and empty Trailing`` () =
    let doc = YamlDocument.Parse "name: Tomas\nage: 30\nitems:\n  - one\n  - two\n"
    Assert.Empty(doc.Comments)
    Assert.Empty(doc.Trailing)

[<Fact>]
let ``TryGetComments returns None for a path with no recorded comments`` () =
    let doc = YamlDocument.Parse "name: Tomas\nage: 30\n"
    Assert.Equal(None, doc.TryGetComments [ Key(YamlValue.String "age") ])

[<Fact>]
let ``TryGetComments returns Some for a path with recorded comments`` () =
    let doc = YamlDocument.Parse "name: Tomas   # given name\n"
    match doc.TryGetComments [ Key(YamlValue.String "name") ] with
    | Some c -> Assert.Equal(Some "given name", c.Trailing)
    | None -> failwith "Expected Some"
