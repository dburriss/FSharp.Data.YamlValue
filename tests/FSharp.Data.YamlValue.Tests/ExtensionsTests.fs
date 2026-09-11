module FSharp.Data.YamlValue.Tests.ExtensionsTests

open System
open Xunit
open FSharp.Data
open FSharp.Data.YamlExtensions

// ---------------------------------------------------------------------
// AsBoolean
// ---------------------------------------------------------------------

[<Fact>]
let ``AsBoolean unwraps a Boolean directly`` () =
    Assert.True(YamlValue.Boolean(true).AsBoolean())

[<Fact>]
let ``AsBoolean coerces a String`` () =
    Assert.True(YamlValue.String("true").AsBoolean())
    Assert.False(YamlValue.String("false").AsBoolean())

[<Fact>]
let ``AsBoolean fails on a Mapping`` () =
    Assert.Throws<Exception>(fun () -> YamlValue.Mapping [||] |> fun v -> v.AsBoolean() |> ignore)
    |> ignore

// ---------------------------------------------------------------------
// AsInteger / AsInteger64
// ---------------------------------------------------------------------

[<Fact>]
let ``AsInteger unwraps a Number directly`` () =
    Assert.Equal(42, YamlValue.Number(42M).AsInteger())

[<Fact>]
let ``AsInteger coerces a String`` () =
    Assert.Equal(42, YamlValue.String("42").AsInteger())

[<Fact>]
let ``AsInteger truncates a Float`` () =
    Assert.Equal(3, YamlValue.Float(3.7).AsInteger())

[<Fact>]
let ``AsInteger fails on a Mapping`` () =
    Assert.Throws<Exception>(fun () -> YamlValue.Mapping [||] |> fun v -> v.AsInteger() |> ignore)
    |> ignore

[<Fact>]
let ``AsInteger64 unwraps a Number directly`` () =
    Assert.Equal(9000000000L, YamlValue.Number(9000000000M).AsInteger64())

[<Fact>]
let ``AsInteger64 coerces a String`` () =
    Assert.Equal(9000000000L, YamlValue.String("9000000000").AsInteger64())

// ---------------------------------------------------------------------
// AsDecimal / AsFloat
// ---------------------------------------------------------------------

[<Fact>]
let ``AsDecimal unwraps a Number directly`` () =
    Assert.Equal(3.14M, YamlValue.Number(3.14M).AsDecimal())

[<Fact>]
let ``AsDecimal converts a Float`` () =
    Assert.Equal(3.5M, YamlValue.Float(3.5).AsDecimal())

[<Fact>]
let ``AsDecimal coerces a String`` () =
    Assert.Equal(3.14M, YamlValue.String("3.14").AsDecimal())

[<Fact>]
let ``AsFloat unwraps a Float directly`` () =
    Assert.Equal(3.5, YamlValue.Float(3.5).AsFloat())

[<Fact>]
let ``AsFloat converts a Number`` () =
    Assert.Equal(3.14, YamlValue.Number(3.14M).AsFloat())

[<Fact>]
let ``AsFloat coerces a String`` () =
    Assert.Equal(3.14, YamlValue.String("3.14").AsFloat())

[<Fact>]
let ``AsFloat treats missing-value strings as nan`` () =
    Assert.True(Double.IsNaN(YamlValue.String("").AsFloat()))
    Assert.True(Double.IsNaN(YamlValue.String("#N/A").AsFloat()))

[<Fact>]
let ``AsFloat fails on a Mapping`` () =
    Assert.Throws<Exception>(fun () -> YamlValue.Mapping [||] |> fun v -> v.AsFloat() |> ignore)
    |> ignore

// ---------------------------------------------------------------------
// AsString
// ---------------------------------------------------------------------

[<Fact>]
let ``AsString unwraps a String directly`` () =
    Assert.Equal("hello", YamlValue.String("hello").AsString())

[<Fact>]
let ``AsString returns empty string for Null`` () =
    Assert.Equal("", YamlValue.Null.AsString())

[<Fact>]
let ``AsString renders a Boolean`` () =
    Assert.Equal("true", YamlValue.Boolean(true).AsString())

[<Fact>]
let ``AsString fails on a Mapping`` () =
    Assert.Throws<Exception>(fun () -> YamlValue.Mapping [||] |> fun v -> v.AsString() |> ignore)
    |> ignore

// ---------------------------------------------------------------------
// AsDateTime / AsDateTimeOffset / AsTimeSpan / AsGuid
// ---------------------------------------------------------------------

[<Fact>]
let ``AsDateTime unwraps a Timestamp directly`` () =
    let dto = DateTimeOffset(2024, 1, 30, 0, 0, 0, TimeSpan.Zero)
    Assert.Equal(dto.DateTime, YamlValue.Timestamp(dto).AsDateTime())

[<Fact>]
let ``AsDateTime coerces a String`` () =
    let dt = YamlValue.String("2024-01-30").AsDateTime()
    Assert.Equal(2024, dt.Year)
    Assert.Equal(1, dt.Month)
    Assert.Equal(30, dt.Day)

[<Fact>]
let ``AsDateTimeOffset unwraps a Timestamp directly`` () =
    let dto = DateTimeOffset(2024, 1, 30, 0, 0, 0, TimeSpan.Zero)
    Assert.Equal(dto, YamlValue.Timestamp(dto).AsDateTimeOffset())

[<Fact>]
let ``AsTimeSpan coerces a String`` () =
    Assert.Equal(TimeSpan.FromHours(1.5), YamlValue.String("01:30:00").AsTimeSpan())

[<Fact>]
let ``AsGuid coerces a String`` () =
    let g = Guid.NewGuid()
    Assert.Equal(g, YamlValue.String(g.ToString()).AsGuid())

// ---------------------------------------------------------------------
// AsArray / AsSequence / AsMapping
// ---------------------------------------------------------------------

[<Fact>]
let ``AsArray unwraps a Sequence`` () =
    let elements = [| YamlValue.Number 1M; YamlValue.Number 2M |]
    Assert.Equal<YamlValue[]>(elements, YamlValue.Sequence(elements).AsArray())

[<Fact>]
let ``AsArray returns empty array for non-sequence`` () =
    Assert.Empty(YamlValue.String("x").AsArray())

[<Fact>]
let ``AsSequence is an alias for AsArray`` () =
    let elements = [| YamlValue.Number 1M |]
    Assert.Equal<YamlValue[]>(elements, YamlValue.Sequence(elements).AsSequence())

[<Fact>]
let ``AsMapping unwraps a Mapping`` () =
    let props = [| (YamlValue.String "a", YamlValue.Number 1M) |]
    Assert.Equal<(YamlValue * YamlValue)[]>(props, YamlValue.Mapping(props).AsMapping())

[<Fact>]
let ``AsMapping returns empty array for non-mapping`` () =
    Assert.Empty(YamlValue.String("x").AsMapping())

// ---------------------------------------------------------------------
// TryGetProperty / GetProperty / Properties / Entries
// ---------------------------------------------------------------------

let private mixedKeyMapping =
    YamlValue.Mapping
        [| (YamlValue.String "name", YamlValue.String "Tomas")
           (YamlValue.String "born", YamlValue.Number 1985M)
           (YamlValue.Number 1M, YamlValue.String "one") |]

[<Fact>]
let ``TryGetProperty finds a present string-keyed property`` () =
    match mixedKeyMapping.TryGetProperty "name" with
    | Some(YamlValue.String s) -> Assert.Equal("Tomas", s)
    | _ -> Assert.True(false, "expected Some (String \"Tomas\")")

[<Fact>]
let ``TryGetProperty returns None for a missing property`` () =
    Assert.Equal(None, mixedKeyMapping.TryGetProperty "missing")

[<Fact>]
let ``TryGetProperty returns None for a non-mapping`` () =
    Assert.Equal(None, YamlValue.String("x").TryGetProperty "name")

[<Fact>]
let ``GetProperty returns the value for a present property`` () =
    Assert.Equal(YamlValue.Number 1985M, mixedKeyMapping.GetProperty "born")

[<Fact>]
let ``GetProperty throws for a missing property`` () =
    Assert.Throws<Exception>(fun () -> mixedKeyMapping.GetProperty "missing" |> ignore)
    |> ignore

[<Fact>]
let ``Properties excludes non-string keys`` () =
    let props = mixedKeyMapping.Properties()
    Assert.Equal(2, props.Length)
    Assert.Contains(("name", YamlValue.String "Tomas"), props)
    Assert.Contains(("born", YamlValue.Number 1985M), props)

[<Fact>]
let ``Entries includes every pair including non-string keys`` () =
    let entries = mixedKeyMapping.Entries()
    Assert.Equal(3, entries.Length)
    Assert.Contains((YamlValue.Number 1M, YamlValue.String "one"), entries)

[<Fact>]
let ``InnerText concatenates mapping and sequence contents`` () =
    let seq' = YamlValue.Sequence [| YamlValue.String "a"; YamlValue.String "b" |]
    Assert.Equal("ab", seq'.InnerText())
    Assert.Equal("Tomas", YamlValue.String("Tomas").InnerText())
    Assert.Equal("", YamlValue.Null.InnerText())

// ---------------------------------------------------------------------
// The `?` operator
// ---------------------------------------------------------------------

[<Fact>]
let ``the question-mark operator gets a property`` () =
    let v = mixedKeyMapping?name
    Assert.Equal(YamlValue.String "Tomas", v)

// ---------------------------------------------------------------------
// Indexers
// ---------------------------------------------------------------------

[<Fact>]
let ``string indexer gets a property`` () =
    Assert.Equal(YamlValue.String "Tomas", mixedKeyMapping.["name"])

[<Fact>]
let ``int indexer gets a sequence element`` () =
    let s = YamlValue.Sequence [| YamlValue.String "Anna"; YamlValue.String "Petr" |]
    Assert.Equal(YamlValue.String "Petr", s.[1])

[<Fact>]
let ``int indexer throws when out of bounds`` () =
    let s = YamlValue.Sequence [| YamlValue.String "Anna" |]
    Assert.Throws<Exception>(fun () -> s.[5] |> ignore) |> ignore

[<Fact>]
let ``int indexer throws for a non-sequence`` () =
    Assert.Throws<Exception>(fun () -> mixedKeyMapping.[0] |> ignore) |> ignore

// ---------------------------------------------------------------------
// GetEnumerator
// ---------------------------------------------------------------------

[<Fact>]
let ``GetEnumerator enables for-in over a Sequence`` () =
    let s = YamlValue.Sequence [| YamlValue.String "Anna"; YamlValue.String "Petr" |]
    let names = ResizeArray()
    for x in s do
        names.Add(x.AsString())
    Assert.Equal<string list>([ "Anna"; "Petr" ], List.ofSeq names)

// ---------------------------------------------------------------------
// Integration: the plan's exact usage snippet
// ---------------------------------------------------------------------

[<Fact>]
let ``plan usage snippet works end to end`` () =
    let info =
        YamlValue.Parse
            """
name: Tomas          # given name
born: 1985
siblings: [Anna, Petr]
"""

    Assert.Equal("Tomas", info?name.AsString())
    Assert.Equal(1985, info?born.AsInteger())

    let names = ResizeArray()
    for s in info?siblings do
        names.Add(s.AsString())

    Assert.Equal<string list>([ "Anna"; "Petr" ], List.ofSeq names)
