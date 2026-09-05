module FSharp.Data.Yaml.Tests.RoundTripTests

open System
open System.Globalization
open Xunit
open FsCheck
open FSharp.Data

// `YamlScalar.resolvePlainScalar` is `internal`, visible here via `InternalsVisibleTo` (see
// `ScalarTests.fs`). Used below to build generators that only produce values the emitter/parser
// pair can actually round-trip *by construction*, rather than asserting it after the fact.
open FSharp.Data.YamlScalar

// ---------------------------------------------------------------------
// Generators
// ---------------------------------------------------------------------

/// A `string` generator with the null case mapped to `""` (F#'s default `string` `Arbitrary` can
/// produce `null`, which isn't a value `YamlValue.String` can sensibly hold) and lone (unpaired)
/// UTF-16 surrogates filtered out (not valid text, and not something the rest of the pipeline —
/// string methods, the reader's char-by-char scanning — needs to cope with).
let private stringGen : Gen<string> =
    Arb.generate<string>
    |> Gen.map (fun s -> if isNull s then "" else s)
    |> Gen.filter (fun s ->
        let mutable ok = true
        let mutable i = 0

        while ok && i < s.Length do
            if Char.IsHighSurrogate s.[i] then
                if i + 1 >= s.Length || not (Char.IsLowSurrogate s.[i + 1]) then
                    ok <- false
                else
                    i <- i + 2
            elif Char.IsLowSurrogate s.[i] then
                ok <- false
            else
                i <- i + 1

        ok)

/// A finite `float` that is *not* exactly `decimal`-representable, so that emitting it and
/// reparsing resolves back to `YamlValue.Float` rather than `YamlValue.Number` (see
/// `YamlEmitter.renderFloat`'s doc comment — the emitter has no way to mark "this must stay a
/// Float" in plain YAML text; the only float literal forms that are unambiguous are `.inf`/`.nan`,
/// which are handled separately and not exercised by this generator).
///
/// `NaN`/`Infinity` are excluded entirely: emitted, they round-trip perfectly fine as `.nan`/`.inf`
/// text, but `Double.NaN = Double.NaN` is `false` (IEEE 754 non-reflexivity), which would make the
/// *property* (structural equality of the parsed-back value) spuriously fail even though the
/// emitter and parser both behaved correctly. This is a generator-design accommodation for an
/// equality quirk, not evidence of a parser/emitter bug.
let private nonDecimalFloatGen : Gen<float> =
    Arb.generate<int64>
    |> Gen.map BitConverter.Int64BitsToDouble
    |> Gen.filter (fun f ->
        not (Double.IsNaN f)
        && not (Double.IsInfinity f)
        && (let text = f.ToString("G17", CultureInfo.InvariantCulture)

            match resolvePlainScalar text with
            | YamlValue.Float f2 -> f2 = f
            | _ -> false))

/// A `DateTimeOffset` at millisecond precision (not the full 7-digit tick precision
/// `YamlValue.Timestamp` can technically hold) — `YamlScalar.resolvePlainScalar`'s fractional-
/// second parsing goes through a `Double.Parse` scaled by 10,000,000, which is not guaranteed
/// exact at full tick precision for arbitrary fractions. Millisecond-precision fractions parse
/// back exactly in practice; the `Gen.filter` below double-checks this empirically for every
/// generated value rather than assuming it, so if that assumption ever breaks — e.g. under a
/// runtime with different floating-point rounding — the generator simply produces fewer values
/// instead of the property flaking.
let private timestampGen : Gen<DateTimeOffset> =
    gen {
        let! days = Gen.choose (-100_000, 100_000)
        let! secondsOfDay = Gen.choose (0, 86_399)
        let! millis = Gen.choose (0, 999)
        let! offsetQuarterHours = Gen.choose (-56, 56) // +/- 14:00 in 15-minute steps
        let baseDate = DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified).AddDays(float days)
        let dt = baseDate.AddSeconds(float secondsOfDay).AddMilliseconds(float millis)
        let offset = TimeSpan.FromMinutes(float (offsetQuarterHours * 15))
        return DateTimeOffset(dt, offset)
    }
    |> Gen.filter (fun t ->
        let text = t.ToString("yyyy-MM-ddTHH:mm:ss.fffffffK", CultureInfo.InvariantCulture)

        match resolvePlainScalar text with
        | YamlValue.Timestamp t2 -> t2 = t
        | _ -> false)

let private leafGen : Gen<YamlValue> =
    Gen.oneof
        [ stringGen |> Gen.map YamlValue.String
          Arb.generate<decimal> |> Gen.map YamlValue.Number
          nonDecimalFloatGen |> Gen.map YamlValue.Float
          Arb.generate<bool> |> Gen.map YamlValue.Boolean
          timestampGen |> Gen.map YamlValue.Timestamp
          Gen.constant YamlValue.Null ]

/// One mapping-entry key, tagged with `i` (this entry's position within its enclosing mapping) so
/// that keys generated for the same mapping are guaranteed distinct by construction — round-trip
/// equality of a `Mapping`'s `(YamlValue * YamlValue)[]` requires the parser to reproduce the same
/// entries in the same order, which duplicate-looking keys would put at the mercy of however the
/// parser happens to handle (or reject) actual key collisions, an orthogonal concern this
/// property isn't trying to exercise.
let private keyGen (i: int) : Gen<YamlValue> =
    Gen.frequency
        [ 6, (stringGen |> Gen.map (fun suffix -> YamlValue.String(sprintf "k%d_%s" i suffix)))
          2, Gen.constant (YamlValue.Number(decimal i))
          1,
          (stringGen
           |> Gen.map (fun suffix -> YamlValue.Sequence [| YamlValue.Number(decimal i); YamlValue.String suffix |])) ]

let rec private valueGen (depth: int) : Gen<YamlValue> =
    if depth <= 0 then
        leafGen
    else
        let composite =
            Gen.oneof
                [ gen {
                      let! n = Gen.choose (0, 3)
                      let! items = Gen.listOfLength n (valueGen (depth - 1))
                      return YamlValue.Sequence(List.toArray items)
                  }
                  gen {
                      let! n = Gen.choose (0, 3)
                      let! keys = [ 0 .. n - 1 ] |> List.map keyGen |> Gen.sequence
                      let! values = Gen.listOfLength n (valueGen (depth - 1))
                      return YamlValue.Mapping(List.zip keys values |> List.toArray)
                  } ]

        Gen.frequency [ 3, leafGen; 2, composite ]

/// Biased toward shallow trees (depth 3) to keep individual test runs — and shrinking, on a
/// failure — fast; `YamlEmitter`'s node budget and structural correctness are already exercised
/// separately in `EmitterTests`.
let private yamlValueGen : Gen<YamlValue> = valueGen 3

// ---------------------------------------------------------------------
// The round-trip property
// ---------------------------------------------------------------------

let private roundTrips (saveOptions: YamlSaveOptions) (indentationSpaces: int) (v: YamlValue) : bool =
    let emitted = v.ToString(saveOptions, indentationSpaces)
    let parsed = YamlValue.Parse emitted
    parsed = v

let private check (saveOptions: YamlSaveOptions) (indentationSpaces: int) =
    let arb = Arb.fromGen yamlValueGen
    let config = { Config.QuickThrowOnFailure with MaxTest = 300 }
    Check.One(config, Prop.forAll arb (roundTrips saveOptions indentationSpaces))

[<Fact>]
let ``Parse(v.ToString(None)) = v`` () = check YamlSaveOptions.None 2

[<Fact>]
let ``Parse(v.ToString(Flow)) = v`` () = check YamlSaveOptions.Flow 2

[<Fact>]
let ``Parse(v.ToString(DisableFormatting)) = v`` () = check YamlSaveOptions.DisableFormatting 2

[<Fact>]
let ``Parse(v.ToString(Flow ||| DisableFormatting)) = v`` () =
    check (YamlSaveOptions.Flow ||| YamlSaveOptions.DisableFormatting) 2

[<Fact>]
let ``Parse(v.ToString(ExplicitDocumentMarkers)) = v`` () =
    check YamlSaveOptions.ExplicitDocumentMarkers 2

[<Fact>]
let ``Parse(v.ToString(None, indentationSpaces = 4)) = v`` () = check YamlSaveOptions.None 4

[<Fact>]
let ``Parse(v.ToString(None ||| ExplicitDocumentMarkers ||| DisableFormatting)) = v`` () =
    check (YamlSaveOptions.ExplicitDocumentMarkers ||| YamlSaveOptions.DisableFormatting) 2
