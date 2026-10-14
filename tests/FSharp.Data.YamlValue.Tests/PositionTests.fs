module FSharp.Data.YamlValue.Tests.PositionTests

open System
open System.IO
open Xunit
open FSharp.Data

let private slice (text: string) (s: Span) = text.Substring(s.Start.Offset, s.End.Offset - s.Start.Offset)

let private pos line column offset : Pos = { Line = line; Column = column; Offset = offset }

// ---------------------------------------------------------------------
// Conventions shared with the sibling JSON library
// ---------------------------------------------------------------------

[<Fact>]
let ``flow document spans match the JSON library's conventions`` () =
    let text = "{\n  \"a\": [10, {\"b\": \"x\"}],\n  \"c\": true\n}"
    let v, spans = YamlValue.ParseWithPositions text
    Assert.Equal(YamlValue.Parse text, v)
    let at (p: string) = spans.[p]
    Assert.Equal(pos 1 1 0, (at "").Start)
    Assert.Equal(pos 4 2 text.Length, (at "").End)
    Assert.Equal(pos 2 8 9, (at "/a").Start)
    Assert.Equal(2, (at "/a/0").Start.Line)
    Assert.Equal(9, (at "/a/0").Start.Column)
    Assert.Equal(11, (at "/a/0").End.Column)
    Assert.Equal("{\"b\": \"x\"}", slice text (at "/a/1"))
    Assert.Equal("\"x\"", slice text (at "/a/1/b"))
    Assert.Equal(3, (at "/c").Start.Line)
    Assert.Equal(8, (at "/c").Start.Column)
    Assert.Equal(12, (at "/c").End.Column)
    Assert.Equal(6, spans.Count)

[<Fact>]
let ``pointer keys escape tilde and slash`` () =
    let _, spans = YamlValue.ParseWithPositions "{\"a/b\": 1, \"m~n\": 2, \"\": 3}"
    Assert.True(spans.ContainsKey "/a~1b")
    Assert.True(spans.ContainsKey "/m~0n")
    Assert.True(spans.ContainsKey "/")

[<Fact>]
let ``duplicate keys first occurrence wins`` () =
    let text = "a: 1\na: 22\n"
    let _, spans = YamlValue.ParseWithPositions text
    Assert.Equal(3, spans.["/a"].Start.Offset)
    let _, spans2 = YamlValue.ParseWithPositions "a:\n  x: 1\na:\n  y: 2\n"
    Assert.True(spans2.ContainsKey "/a/x")
    Assert.False(spans2.ContainsKey "/a/y")

[<Fact>]
let ``crlf counts as one line break and bom is not a column`` () =
    let _, spans = YamlValue.ParseWithPositions "\uFEFF[1,\r\n 2]"
    Assert.Equal(1, spans.["/0"].Start.Line)
    Assert.Equal(2, spans.["/0"].Start.Column)
    Assert.Equal(2, spans.["/1"].Start.Line)
    Assert.Equal(2, spans.["/1"].Start.Column)
    Assert.Equal(2, spans.["/0"].Start.Offset) // offset is a plain string index, BOM included
    let _, block = YamlValue.ParseWithPositions "a: 1\r\nb: 2\r\n"
    Assert.Equal(pos 2 4 9, block.["/b"].Start)

[<Fact>]
let ``parse errors surface through the result api`` () =
    match YamlValue.TryParseWithPositions "[1,\n  oops: [" with
    | Error e ->
        Assert.Equal(2, e.Pos.Line)
        Assert.True(e.Pos.Column >= 1)
        Assert.False(e.Message.Contains " at line ")
        Assert.Equal(e.Pos.Offset, "[1,\n".Length + e.Pos.Column - 1)
    | Ok _ -> failwith "expected error"
    match YamlValue.TryParseWithPositions "a: 1\nb: 2\n" with
    | Ok(v, _) -> Assert.Equal(YamlValue.Parse "a: 1\nb: 2\n", v)
    | Error e -> failwith e.Message

// ---------------------------------------------------------------------
// Block style
// ---------------------------------------------------------------------

[<Fact>]
let ``block document spans`` () =
    let text = "a: 1\nb:\n  - x\n  - [y, z]\nc: |\n  lit\n  eral\nd: \"q\"\n"
    let v, spans = YamlValue.ParseWithPositions text
    Assert.Equal(YamlValue.Parse text, v)
    Assert.Equal(pos 1 1 0, spans.[""].Start)
    Assert.Equal(pos 8 7 49, spans.[""].End)
    Assert.Equal("1", slice text spans.["/a"])
    Assert.Equal(pos 1 4 3, spans.["/a"].Start)
    Assert.Equal("- x\n  - [y, z]", slice text spans.["/b"])
    Assert.Equal("x", slice text spans.["/b/0"])
    Assert.Equal("[y, z]", slice text spans.["/b/1"])
    Assert.Equal("y", slice text spans.["/b/1/0"])
    Assert.Equal("z", slice text spans.["/b/1/1"])
    Assert.Equal("|\n  lit\n  eral", slice text spans.["/c"])
    Assert.Equal("\"q\"", slice text spans.["/d"])
    Assert.Equal(9, spans.Count)

[<Fact>]
let ``scalar kinds`` () =
    let text = "p: plain text # comment\nm: multi\n  line\ns: 'it''s'\nn:\ne: ~\nt: !!str 12\nan: &x 5\nal: *x\n"
    let _, spans = YamlValue.ParseWithPositions text
    Assert.Equal("plain text", slice text spans.["/p"])
    Assert.Equal("multi\n  line", slice text spans.["/m"])
    Assert.Equal("'it''s'", slice text spans.["/s"])
    Assert.Equal("~", slice text spans.["/e"])
    Assert.Equal("!!str 12", slice text spans.["/t"]) // a node's span includes its tag
    Assert.Equal("&x 5", slice text spans.["/an"]) // ... and its anchor
    Assert.Equal("*x", slice text spans.["/al"])

[<Fact>]
let ``an empty value is a zero-width span just after its marker`` () =
    let text = "n:\nx: 1\nl:\n  -\n  - 2\n"
    let _, spans = YamlValue.ParseWithPositions text
    Assert.Equal(pos 1 3 2, spans.["/n"].Start)
    Assert.Equal(spans.["/n"].Start, spans.["/n"].End)
    Assert.Equal(spans.["/l/0"].Start, spans.["/l/0"].End)
    Assert.Equal(pos 4 4 14, spans.["/l/0"].Start)

[<Fact>]
let ``explicit keys and flow pairs`` () =
    let text = "? a\n: b\nf: [k: v, w]\n"
    let _, spans = YamlValue.ParseWithPositions text
    Assert.Equal("b", slice text spans.["/a"])
    Assert.Equal("k: v", slice text spans.["/f/0"])
    Assert.Equal("v", slice text spans.["/f/0/k"])
    Assert.Equal("w", slice text spans.["/f/1"])

[<Fact>]
let ``non-string keys get no pointer entries`` () =
    let text = "1: a\n? [x]\n: b\ns: c\n"
    let _, spans = YamlValue.ParseWithPositions text
    Assert.Equal<string list>([ ""; "/s" ], spans |> Map.toList |> List.map fst)

// ---------------------------------------------------------------------
// Aliases and merge keys
// ---------------------------------------------------------------------

[<Fact>]
let ``alias sites own the span of the whole expanded subtree`` () =
    let text = "base: &b\n  x: 1\n  y: [2]\nd: *b\n"
    let _, spans = YamlValue.ParseWithPositions text
    Assert.Equal("1", slice text spans.["/base/x"])
    let alias = spans.["/d"]
    Assert.Equal("*b", slice text alias)
    for p in [ "/d/x"; "/d/y"; "/d/y/0" ] do
        Assert.Equal(alias, spans.[p])

[<Fact>]
let ``merged keys are positioned at the merge entry`` () =
    let text = "base: &b {x: 1, y: {z: 2}}\nd:\n  <<: *b\n  w: 3\n"
    let _, spans = YamlValue.ParseWithPositions text
    let merge = spans.["/d/x"]
    Assert.Equal("*b", slice text merge)
    Assert.Equal(merge, spans.["/d/y"])
    Assert.Equal(merge, spans.["/d/y/z"])
    Assert.Equal("3", slice text spans.["/d/w"])
    Assert.False(spans.ContainsKey "/d/<<")
    Assert.Equal(pos 3 7 36, merge.Start)

[<Fact>]
let ``merge sequences and flow merges`` () =
    let text = "a: &a {p: 1}\nb: &b {q: 2}\nc: {<<: [*a, *b], r: 3}\n"
    let _, spans = YamlValue.ParseWithPositions text
    Assert.Equal("[*a, *b]", slice text spans.["/c/p"])
    Assert.Equal("[*a, *b]", slice text spans.["/c/q"])
    Assert.Equal("3", slice text spans.["/c/r"])

[<Fact>]
let ``with merge keys disabled the << key is an ordinary entry`` () =
    let text = "base: &b {x: 1}\nd:\n  <<: *b\n"
    let v, spans = YamlValue.ParseWithPositions(text, disableMergeKeys = true)
    Assert.Equal(YamlValue.Parse(text, disableMergeKeys = true), v)
    Assert.Equal("*b", slice text spans.["/d/<<"])
    Assert.False(spans.ContainsKey "/d/x")

// ---------------------------------------------------------------------
// Multi-document streams
// ---------------------------------------------------------------------

[<Fact>]
let ``multi-document spans are relative to the whole stream`` () =
    let text = "a: 1\n---\nb: 2\n...\n---\n[3]\n"
    let docs = YamlValue.ParseMultipleWithPositions text |> List.ofSeq
    Assert.Equal(3, docs.Length)
    Assert.Equal<YamlValue list>(YamlValue.ParseMultiple text |> List.ofSeq, docs |> List.map fst)
    Assert.Equal("1", slice text (snd docs.[0]).["/a"])
    Assert.Equal(pos 3 4 12, (snd docs.[1]).["/b"].Start)
    Assert.Equal("3", slice text (snd docs.[2]).["/0"])

// ---------------------------------------------------------------------
// Parse is untouched
// ---------------------------------------------------------------------

[<Fact>]
let ``ParseWithPositions agrees with Parse on every conformance input`` () =
    let dir = Path.Combine(AppContext.BaseDirectory, "data")
    let mutable checkedCount = 0
    for file in Directory.GetFiles(dir, "*.yaml") do
        let text = File.ReadAllText file
        let plain = try Ok(YamlValue.Parse text) with :? YamlParseException as e -> Error(e.Line, e.Column)
        let withPos =
            try Ok(fst (YamlValue.ParseWithPositions text)) with :? YamlParseException as e -> Error(e.Line, e.Column)
        Assert.True((plain = withPos), Path.GetFileName file)
        match withPos with
        | Ok v ->
            // every pointer in the expanded tree has a span
            let _, spans = YamlValue.ParseWithPositions text
            let rec pointers (p: string) (v: YamlValue) : string list =
                match v with
                | YamlValue.Mapping pairs ->
                    p :: [ for (k, x) in pairs do
                               match k with
                               | YamlValue.String s when not (s.Contains "/" || s.Contains "~") -> yield! pointers (p + "/" + s) x
                               | _ -> () ]
                | YamlValue.Sequence xs -> p :: [ for i in 0 .. xs.Length - 1 do yield! pointers (p + "/" + string i) xs.[i] ]
                | _ -> [ p ]
            for p in pointers "" v do
                Assert.True(spans.ContainsKey p, Path.GetFileName file + ": missing " + p)
        | Error _ -> ()
        checkedCount <- checkedCount + 1
    Assert.True(checkedCount > 20)

// ---------------------------------------------------------------------
// Timestamp original text (ParseWithMetadata)
// ---------------------------------------------------------------------

[<Theory>]
[<InlineData("2026-10-14")>]
[<InlineData("2026-10-14T01:02:03Z")>]
[<InlineData("2026-10-14T01:02:03+02:00")>]
[<InlineData("2026-10-14T01:02:03")>]
[<InlineData("2026-10-14 01:02:03")>]
[<InlineData("2026-10-14t01:02:03.123456z")>]
[<InlineData("2026-10-14T01:02:03.5-05:30")>]
let ``timestamp source text is kept`` (ts: string) =
    let p = YamlValue.ParseWithMetadata("when: " + ts + "\n")
    Assert.True(
        (match p.Root with
         | YamlValue.Mapping [| _, YamlValue.Timestamp _ |] -> true
         | _ -> false)
    )
    Assert.Equal<(string * string) list>([ "/when", ts ], p.TimestampText |> Map.toList)

[<Fact>]
let ``timestamp text covers flow, sequences, aliases, merges and tags`` () =
    let text =
        "a: &d 2026-01-02\nb: *d\nc: [2026-03-04, \"2026-05-06\", 7]\nm: &m {t: 2026-07-08 09:10:11}\nd:\n  <<: *m\ne: !!timestamp 2026-09-10\n"
    let p = YamlValue.ParseWithMetadata text
    let t = p.TimestampText
    Assert.Equal("2026-01-02", t.["/a"])
    Assert.Equal("2026-01-02", t.["/b"])
    Assert.Equal("2026-03-04", t.["/c/0"])
    Assert.False(t.ContainsKey "/c/1") // quoted: a String, not a Timestamp
    Assert.False(t.ContainsKey "/c/2")
    Assert.Equal("2026-07-08 09:10:11", t.["/m/t"])
    Assert.Equal("2026-07-08 09:10:11", t.["/d/t"])
    Assert.Equal("2026-09-10", t.["/e"])

[<Fact>]
let ``metadata agrees with ParseWithPositions and Parse`` () =
    let text = "a: 2026-10-14\nb: [1, {c: x}]\n"
    let p = YamlValue.ParseWithMetadata text
    let v, spans = YamlValue.ParseWithPositions text
    Assert.Equal(YamlValue.Parse text, p.Root)
    Assert.Equal(v, p.Root)
    Assert.Equal<Map<string, Span>>(spans, p.Spans)

[<Fact>]
let ``multi-document metadata and the non-throwing form`` () =
    let docs = YamlValue.ParseMultipleWithMetadata "a: 2026-01-01\n---\nb: 2026-02-02 03:04:05\n" |> List.ofSeq
    Assert.Equal("2026-01-01", docs.[0].TimestampText.["/a"])
    Assert.Equal("2026-02-02 03:04:05", docs.[1].TimestampText.["/b"])
    match YamlValue.TryParseWithMetadata "a: [" with
    | Error e -> Assert.Equal(1, e.Pos.Line)
    | Ok _ -> failwith "expected error"

[<Fact>]
let ``plain Parse still holds only the DateTimeOffset`` () =
    // Documented limitation: Parse alone cannot tell a date from midnight UTC, or say a zone was omitted.
    let date = YamlValue.Parse "2026-10-14"
    let midnight = YamlValue.Parse "2026-10-14T00:00:00Z"
    let zoneless = YamlValue.Parse "2026-10-14T01:02:03"
    Assert.Equal(date, midnight)
    Assert.Equal(YamlValue.Timestamp(DateTimeOffset(2026, 10, 14, 1, 2, 3, TimeSpan.Zero)), zoneless)
