namespace FSharp.Data

open System
open System.Globalization
open System.Text

/// The handful of operations whose .NET implementation is not portable to Fable (JS / Python),
/// each written once here with a `FABLE_COMPILER` branch so the rest of the library is a single
/// source. On .NET every function is exactly the BCL call the library always used (invariant
/// culture), so behaviour there is unchanged. Under Fable the BCL overloads that take a
/// `NumberStyles`/`IFormatProvider` are compiled with the argument ignored (a Fable warning,
/// fatal under `TreatWarningsAsErrors`), so those branches call the plain overloads — culture is
/// always invariant under Fable — and re-check the number's shape where the style used to.
module internal YamlPortable =

#if FABLE_COMPILER
    /// Fable's number parsers reject an explicit leading `+` (`+5`), which .NET accepts.
    let private dropPlus (s: string) : string =
        if s.Length > 0 && s.[0] = '+' then s.Substring 1 else s
#endif

    /// True for `0-9a-fA-F` (what `System.Uri.IsHexDigit` accepted; that member is not in Fable).
    let isHexDigit (c: char) : bool =
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')

    /// Appends the UTF-16 encoding of `codepoint` to `sb`, returning `false` (and appending
    /// nothing) for a value `Char.ConvertFromUtf32` would reject: negative, above U+10FFFF, or a
    /// surrogate code point. Replaces `Char.ConvertFromUtf32`, which Fable lacks.
    let tryAppendCodePoint (sb: StringBuilder) (codepoint: int) : bool =
        if codepoint < 0 || codepoint > 0x10FFFF || (codepoint >= 0xD800 && codepoint <= 0xDFFF) then
            false
        elif codepoint < 0x10000 then
            sb.Append(char codepoint) |> ignore
            true
#if FABLE_COMPILER_PYTHON
        else
            // Python strings hold whole code points, not UTF-16 units: one `chr` for an
            // astral character (so under Fable Python `Length`/columns count it as 1, not 2).
            sb.Append(char codepoint) |> ignore
            true
#else
        else
            let c = codepoint - 0x10000
            sb.Append(char (0xD800 + (c >>> 10))).Append(char (0xDC00 + (c &&& 0x3FF))) |> ignore
            true
#endif

    /// Parses text already known (by regex) to be a decimal literal — `[-+]?digits[.digits][e±n]`
    /// — invariantly.
    let parseDecimal (s: string) : decimal =
#if FABLE_COMPILER
        Decimal.Parse(dropPlus s)
#else
        Decimal.Parse(s, NumberStyles.Float ||| NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
#endif

    /// Parses text already known (by regex) to be a floating-point literal, invariantly.
    let parseDouble (s: string) : float =
#if FABLE_COMPILER
        Double.Parse(dropPlus s)
#else
        Double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture)
#endif

    /// `[-+]?[0-9]+` (the `NumberStyles.AllowLeadingSign` / `NumberStyles.Integer` shape, minus
    /// surrounding whitespace under `Integer`).
    let private isSignedDigits (s: string) : bool =
        let start = if s.Length > 0 && (s.[0] = '-' || s.[0] = '+') then 1 else 0
        s.Length > start && Seq.forall (fun c -> c >= '0' && c <= '9') (s.Substring start)

    /// `Decimal.TryParse(s, NumberStyles.AllowLeadingSign, invariant)`: an optionally signed run
    /// of digits (no decimal point, exponent or whitespace).
    let tryParseIntegerDecimal (s: string) : decimal option =
#if FABLE_COMPILER
        if isSignedDigits s then Some(Decimal.Parse(dropPlus s)) else None
#else
        match Decimal.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
        | true, d -> Some d
        | false, _ -> None
#endif

    /// `Double.TryParse(s, NumberStyles.Float, culture)`.
    let tryParseFloat (s: string) (culture: CultureInfo) : float option =
#if FABLE_COMPILER
        match Double.TryParse(dropPlus s) with
        | true, d -> Some d
        | false, _ -> None
#else
        match Double.TryParse(s, NumberStyles.Float, culture) with
        | true, d -> Some d
        | false, _ -> None
#endif

    /// `Decimal.TryParse(s, NumberStyles.Float ||| NumberStyles.AllowLeadingSign, culture)`.
    let tryParseDecimal (s: string) (culture: CultureInfo) : decimal option =
#if FABLE_COMPILER
        match Decimal.TryParse(dropPlus s) with
        | true, d -> Some d
        | false, _ -> None
#else
        match Decimal.TryParse(s, NumberStyles.Float ||| NumberStyles.AllowLeadingSign, culture) with
        | true, d -> Some d
        | false, _ -> None
#endif

    /// `Int32.TryParse(s, NumberStyles.Integer, culture)`.
    let tryParseInt32 (s: string) (culture: CultureInfo) : int option =
#if FABLE_COMPILER
        match Int32.TryParse(dropPlus (s.Trim())) with
        | true, d -> Some d
        | false, _ -> None
#else
        match Int32.TryParse(s, NumberStyles.Integer, culture) with
        | true, d -> Some d
        | false, _ -> None
#endif

    /// `Int64.TryParse(s, NumberStyles.Integer, culture)`.
    let tryParseInt64 (s: string) (culture: CultureInfo) : int64 option =
#if FABLE_COMPILER
        match Int64.TryParse(dropPlus (s.Trim())) with
        | true, d -> Some d
        | false, _ -> None
#else
        match Int64.TryParse(s, NumberStyles.Integer, culture) with
        | true, d -> Some d
        | false, _ -> None
#endif

    /// Ordinal comparison of `length` chars of `source` starting at `offset` with the start of
    /// `text` (`String.CompareOrdinal(source, offset, text, 0, length) = 0`, absent in Fable
    /// Python's library).
    let regionEquals (source: string) (offset: int) (text: string) (length: int) : bool =
#if FABLE_COMPILER
        let mutable i = 0
        while i < length && source.[offset + i] = text.[i] do
            i <- i + 1
        i = length
#else
        String.CompareOrdinal(source, offset, text, 0, length) = 0
#endif
