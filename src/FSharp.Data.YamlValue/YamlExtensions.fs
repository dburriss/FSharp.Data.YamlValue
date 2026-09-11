/// Extension methods for working with `YamlValue` in a less safe, but more convenient way,
/// mirroring `FSharp.Data.JsonExtensions` for YAML. Each accessor raises a clear exception when
/// the value is not of the expected shape (or cannot be coerced to it) — this is the "I know
/// what this document looks like" half of the API, as opposed to pattern matching on the DU.
///
/// These members only become visible on `YamlValue` once this module is opened:
/// `open FSharp.Data.YamlExtensions`.
module FSharp.Data.YamlExtensions

open System
open System.Collections.Generic
open System.Globalization

type YamlValue with

    // -----------------------------------------------------------------
    // Scalar accessors
    // -----------------------------------------------------------------

    /// Gets the boolean value of an element, assuming it is a boolean (or a string that resolves
    /// to one under the core schema, e.g. `"true"`/`"false"`/`Boolean.TryParse`-able text).
    member this.AsBoolean() : bool =
        match this with
        | YamlValue.Boolean b -> b
        | YamlValue.String s ->
            match s with
            | "true" | "True" | "TRUE" -> true
            | "false" | "False" | "FALSE" -> false
            | _ ->
                match Boolean.TryParse s with
                | true, b -> b
                | false, _ -> failwithf "YamlValue.AsBoolean: not a boolean: %O" this
        | _ -> failwithf "YamlValue.AsBoolean: not a boolean: %O" this

    /// Gets a number as an integer, assuming the value fits in an `int` — a `Number`, a `Float`
    /// (truncated), or a `String` containing an integer.
    member this.AsInteger(?cultureInfo: CultureInfo) : int =
        let culture = defaultArg cultureInfo CultureInfo.InvariantCulture
        match this with
        | YamlValue.Number n -> int n
        | YamlValue.Float f -> int f
        | YamlValue.String s ->
            match Int32.TryParse(s, NumberStyles.Integer, culture) with
            | true, v -> v
            | false, _ -> failwithf "YamlValue.AsInteger: not an integer: %O" this
        | _ -> failwithf "YamlValue.AsInteger: not an integer: %O" this

    /// Gets a number as a 64-bit integer, assuming the value fits.
    member this.AsInteger64(?cultureInfo: CultureInfo) : int64 =
        let culture = defaultArg cultureInfo CultureInfo.InvariantCulture
        match this with
        | YamlValue.Number n -> int64 n
        | YamlValue.Float f -> int64 f
        | YamlValue.String s ->
            match Int64.TryParse(s, NumberStyles.Integer, culture) with
            | true, v -> v
            | false, _ -> failwithf "YamlValue.AsInteger64: not an integer: %O" this
        | _ -> failwithf "YamlValue.AsInteger64: not an integer: %O" this

    /// Gets a number as a decimal, assuming the value fits in a `decimal`.
    member this.AsDecimal(?cultureInfo: CultureInfo) : decimal =
        let culture = defaultArg cultureInfo CultureInfo.InvariantCulture
        match this with
        | YamlValue.Number n -> n
        | YamlValue.Float f -> decimal f
        | YamlValue.String s ->
            match Decimal.TryParse(s, NumberStyles.Float ||| NumberStyles.AllowLeadingSign, culture) with
            | true, v -> v
            | false, _ -> failwithf "YamlValue.AsDecimal: not a decimal: %O" this
        | _ -> failwithf "YamlValue.AsDecimal: not a decimal: %O" this

    /// Gets a number as a float, assuming the value is convertible. Values listed in
    /// `missingValues` (default: `""`, `"#N/A"`) resolve to `nan` instead of failing.
    member this.AsFloat(?cultureInfo: CultureInfo, ?missingValues: string[]) : float =
        let culture = defaultArg cultureInfo CultureInfo.InvariantCulture
        let missingValues = defaultArg missingValues [| ""; "#N/A" |]
        match this with
        | YamlValue.Float f -> f
        | YamlValue.Number n -> float n
        | YamlValue.String s when missingValues |> Array.exists ((=) (s.Trim())) -> Double.NaN
        | YamlValue.String s ->
            match Double.TryParse(s, NumberStyles.Float, culture) with
            | true, v -> v
            | false, _ -> failwithf "YamlValue.AsFloat: not a float: %O" this
        | _ -> failwithf "YamlValue.AsFloat: not a float: %O" this

    /// Gets the string value of an element, assuming the value is a scalar. Returns the empty
    /// string for `YamlValue.Null`.
    member this.AsString(?cultureInfo: CultureInfo) : string =
        let culture = defaultArg cultureInfo CultureInfo.InvariantCulture
        match this with
        | YamlValue.String s -> s
        | YamlValue.Null -> ""
        | YamlValue.Boolean b -> if b then "true" else "false"
        | YamlValue.Number n -> n.ToString(culture)
        | YamlValue.Float f -> f.ToString(culture)
        | YamlValue.Timestamp t -> t.ToString("o", culture)
        | _ -> failwithf "YamlValue.AsString: not a scalar: %O" this

    /// Gets the datetime value of an element — either a `Timestamp`, or a string containing a
    /// well-formed ISO date/date-time.
    member this.AsDateTime(?cultureInfo: CultureInfo) : DateTime =
        let culture = defaultArg cultureInfo CultureInfo.InvariantCulture :> IFormatProvider
        match this with
        | YamlValue.Timestamp t -> t.DateTime
        | YamlValue.String s ->
            match DateTime.TryParse(s, culture, DateTimeStyles.RoundtripKind) with
            | true, v -> v
            | false, _ -> failwithf "YamlValue.AsDateTime: not a datetime: %O" this
        | _ -> failwithf "YamlValue.AsDateTime: not a datetime: %O" this

    /// Gets the datetime offset of an element — either a `Timestamp`, or a string containing an
    /// ISO date-time with offset.
    member this.AsDateTimeOffset(?cultureInfo: CultureInfo) : DateTimeOffset =
        let culture = defaultArg cultureInfo CultureInfo.InvariantCulture :> IFormatProvider
        match this with
        | YamlValue.Timestamp t -> t
        | YamlValue.String s ->
            match DateTimeOffset.TryParse(s, culture, DateTimeStyles.AssumeUniversal) with
            | true, v -> v
            | false, _ -> failwithf "YamlValue.AsDateTimeOffset: not a datetime offset: %O" this
        | _ -> failwithf "YamlValue.AsDateTimeOffset: not a datetime offset: %O" this

    /// Gets the timespan value of an element, assuming it is a string containing a well-formed
    /// time span.
    member this.AsTimeSpan(?cultureInfo: CultureInfo) : TimeSpan =
        let culture = defaultArg cultureInfo CultureInfo.InvariantCulture
        match this with
        | YamlValue.String s ->
            match TimeSpan.TryParse(s, culture) with
            | true, v -> v
            | false, _ -> failwithf "YamlValue.AsTimeSpan: not a timespan: %O" this
        | _ -> failwithf "YamlValue.AsTimeSpan: not a timespan: %O" this

    /// Gets the guid value of an element, assuming it is a string containing a well-formed guid.
    member this.AsGuid() : Guid =
        match this with
        | YamlValue.String s ->
            match Guid.TryParse s with
            | true, g -> g
            | false, _ -> failwithf "YamlValue.AsGuid: not a guid: %O" this
        | _ -> failwithf "YamlValue.AsGuid: not a guid: %O" this

    // -----------------------------------------------------------------
    // Collection accessors
    // -----------------------------------------------------------------

    /// Gets all the elements of a value. Returns an empty array if the value is not a sequence.
    member this.AsArray() : YamlValue[] =
        match this with
        | YamlValue.Sequence elements -> elements
        | _ -> [||]

    /// Alias for `AsArray`, using YAML's terminology.
    member this.AsSequence() : YamlValue[] = this.AsArray()

    /// Gets all key-value pairs. Returns an empty array if the value is not a mapping.
    member this.AsMapping() : (YamlValue * YamlValue)[] =
        match this with
        | YamlValue.Mapping properties -> properties
        | _ -> [||]

    // -----------------------------------------------------------------
    // Property / entry access
    // -----------------------------------------------------------------

    /// Tries to get a property of a mapping by name. Returns `None` if the value is not a
    /// mapping, or the property is not present. Only string-keyed entries (matching
    /// `YamlValue.String name`) are considered — use `Entries` to see non-string keys.
    member this.TryGetProperty(propertyName: string) : YamlValue option =
        match this with
        | YamlValue.Mapping properties ->
            properties
            |> Array.tryPick (fun (k, v) ->
                match k with
                | YamlValue.String s when s = propertyName -> Some v
                | _ -> None)
        | _ -> None

    /// Gets a property of a mapping. Fails if the value is not a mapping, or if the property is
    /// not present.
    member this.GetProperty(propertyName: string) : YamlValue =
        match this.TryGetProperty propertyName with
        | Some v -> v
        | None -> failwithf "YamlValue.GetProperty: property '%s' not found" propertyName

    /// Gets the string-keyed properties of a mapping, as name-value pairs. Non-string keys are
    /// skipped — use `Entries` for all of them.
    member this.Properties() : (string * YamlValue)[] =
        match this with
        | YamlValue.Mapping properties ->
            properties
            |> Array.choose (fun (k, v) ->
                match k with
                | YamlValue.String s -> Some(s, v)
                | _ -> None)
        | _ -> [||]

    /// Gets every key-value pair of a mapping, including non-string keys. Returns an empty array
    /// if the value is not a mapping.
    member this.Entries() : (YamlValue * YamlValue)[] =
        match this with
        | YamlValue.Mapping properties -> properties
        | _ -> [||]

    /// Gets the inner text of an element — scalars as strings, mappings and sequences as their
    /// concatenated contents (no separator, mirroring `JsonValue.InnerText`).
    member this.InnerText() : string =
        match this with
        | YamlValue.String s -> s
        | YamlValue.Null -> ""
        | YamlValue.Sequence elements ->
            elements |> Array.map (fun v -> v.InnerText()) |> String.concat ""
        | YamlValue.Mapping properties ->
            properties |> Array.map (fun (_, v) -> v.InnerText()) |> String.concat ""
        | YamlValue.Boolean _
        | YamlValue.Number _
        | YamlValue.Float _
        | YamlValue.Timestamp _ -> this.AsString()

    // -----------------------------------------------------------------
    // Indexers
    // -----------------------------------------------------------------

    /// Assuming the value is a mapping, gets the value with the given name.
    member this.Item
        with get (propertyName: string) : YamlValue = this.GetProperty propertyName

    /// Assuming the value is a sequence, gets the value at the given index.
    member this.Item
        with get (index: int) : YamlValue =
            match this with
            | YamlValue.Sequence elements ->
                if index >= 0 && index < elements.Length then
                    elements.[index]
                else
                    failwithf "YamlValue.Item: index %d out of bounds (length %d)" index elements.Length
            | _ -> failwithf "YamlValue.Item: not a sequence: %O" this

    // -----------------------------------------------------------------
    // Enumeration
    // -----------------------------------------------------------------

    /// Gets all the elements of a value, assuming it is a sequence. Enables `for x in value do`.
    member this.GetEnumerator() : IEnumerator<YamlValue> =
        match this with
        | YamlValue.Sequence elements -> (elements :> seq<YamlValue>).GetEnumerator()
        | _ -> failwithf "YamlValue.GetEnumerator: not a sequence: %O" this

/// Gets a property of a YAML mapping. Equivalent to `YamlValue.GetProperty`, enabling
/// `info?name?first`-style dotted property access.
let (?) (yamlObject: YamlValue) (propertyName: string) : YamlValue =
    yamlObject.GetProperty propertyName
