namespace FSharp.Data

open System
open System.Globalization
open System.Numerics
open System.Text
open System.Text.RegularExpressions

/// Core-schema plain-scalar resolution, plus quoted-scalar unescaping. Both are pure
/// string-in-value/string-out functions with no dependency on `YamlReader` or a parser — the
/// (later) parser calls into these once it has isolated a scalar's raw text.
module internal YamlScalar =

    // ---------------------------------------------------------------------
    // Plain-scalar resolution (YAML core schema + timestamp)
    // ---------------------------------------------------------------------

    let private intPattern = Regex(@"^[-+]?[0-9]+$", RegexOptions.Compiled)
    let private octPattern = Regex(@"^0o[0-7]+$", RegexOptions.Compiled)
    let private hexPattern = Regex(@"^0x[0-9a-fA-F]+$", RegexOptions.Compiled)

    // Accepted case variants for the special (non-finite) floats, per common core-schema
    // convention (and yaml-test-suite usage): the all-lowercase, Capitalized, and all-uppercase
    // forms — never arbitrary mixed case.
    let private negInfForms = set [ "-.inf"; "-.Inf"; "-.INF" ]
    let private posInfForms = set [ ".inf"; ".Inf"; ".INF" ]
    let private nanForms = set [ ".nan"; ".NaN"; ".NAN" ]

    // General float shape: optional sign, then `digits[.digits]` or `.digits`, optional
    // exponent. This intentionally overlaps with `intPattern` (e.g. "123" matches both) —
    // callers must try `intPattern` first. Named groups let the decimal-exactness check
    // reconstruct the value without re-parsing string formatting.
    let private floatPattern =
        Regex(
            @"^(?<sign>[-+])?(?:(?<int>[0-9]+)(?:\.(?<frac>[0-9]*))?|\.(?<frac2>[0-9]+))(?:[eE](?<exp>[-+]?[0-9]+))?$",
            RegexOptions.Compiled
        )

    let private datePattern =
        Regex(@"^([0-9]{4})-([0-9]{2})-([0-9]{2})$", RegexOptions.Compiled)

    let private dateTimePattern =
        Regex(
            @"^([0-9]{4})-([0-9]{2})-([0-9]{2})[Tt ]([0-9]{2}):([0-9]{2}):([0-9]{2})(\.[0-9]+)?[ \t]*(Z|z|[-+][0-9]{2}:?[0-9]{2}?)?$",
            RegexOptions.Compiled
        )

    /// The largest magnitude `System.Decimal` can hold at scale 0 — `Decimal.MaxValue`'s
    /// unscaled integer, as a `BigInteger` for exact comparison.
    let private maxDecimalUnscaled =
        BigInteger.Parse("79228162514264337593543950335")

    /// Decides, via exact rational arithmetic (no float/decimal rounding involved in the
    /// decision itself), whether the numeric literal captured by `floatPattern` is exactly
    /// representable as a `System.Decimal` — i.e. there exists an integer `m` and a scale `s`
    /// in `[0, 28]` such that the value equals `m * 10^-s` exactly. If so, returns
    /// `Some (Decimal.Parse the-original-string)`; otherwise `None` (caller falls back to
    /// `Float`).
    let private tryExactDecimal (m: Match) (original: string) : decimal option =
        let intPart = m.Groups.["int"].Value
        let fracPart =
            if m.Groups.["frac"].Success then m.Groups.["frac"].Value
            else m.Groups.["frac2"].Value
        let digits = intPart + fracPart
        let digits = if digits = "" then "0" else digits
        let expPart = m.Groups.["exp"]
        let exp = if expPart.Success then int expPart.Value else 0

        let unscaled0 = BigInteger.Parse(digits)
        // Effective power of ten: value = unscaled0 * 10^(exp - fracLen)
        let mutable e = exp - fracPart.Length
        let mutable unscaled = unscaled0

        if unscaled <> BigInteger.Zero then
            let ten = BigInteger(10)
            while unscaled % ten = BigInteger.Zero do
                unscaled <- unscaled / ten
                e <- e + 1

        let m', scale =
            if e >= 0 then
                (unscaled * BigInteger.Pow(BigInteger(10), e), 0)
            else
                (unscaled, -e)

        if scale <= 28 && BigInteger.Abs(m') <= maxDecimalUnscaled then
            Some(Decimal.Parse(original, NumberStyles.Float ||| NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture))
        else
            None

    /// Parses an offset group from `dateTimePattern` ("Z"/"z", "+HH:MM", "+HHMM", "+HH") into a
    /// `TimeSpan`. Absent (empty capture) means "no zone" — callers treat that as UTC per
    /// `docs/reference.md` ("dates without a zone are read as UTC").
    let private parseOffset (offsetGroup: string) : TimeSpan =
        if String.IsNullOrEmpty offsetGroup || offsetGroup = "Z" || offsetGroup = "z" then
            TimeSpan.Zero
        else
            let sign = if offsetGroup.[0] = '-' then -1 else 1
            let rest = offsetGroup.Substring(1).Replace(":", "")
            let hours = int (rest.Substring(0, 2))
            let minutes = if rest.Length >= 4 then int (rest.Substring(2, 2)) else 0
            let magnitude = TimeSpan(hours, minutes, 0)
            if sign < 0 then magnitude.Negate() else magnitude

    /// Resolves a **plain** (unquoted) scalar's raw text to a `YamlValue`, per the core schema
    /// plus `Timestamp`. Assumes `text` has already had any surrounding whitespace stripped by
    /// the caller (the parser trims plain scalars as part of scanning them).
    ///
    /// Never call this for quoted or block scalars — those are always `YamlValue.String`
    /// regardless of content, and an explicit tag always overrides resolution; both of those
    /// rules are the parser's responsibility to enforce, not this function's.
    let resolvePlainScalar (text: string) : YamlValue =
        if text = "" || text = "~" || text = "null" || text = "Null" || text = "NULL" then
            YamlValue.Null
        elif text = "true" || text = "True" || text = "TRUE" then
            YamlValue.Boolean true
        elif text = "false" || text = "False" || text = "FALSE" then
            YamlValue.Boolean false
        elif intPattern.IsMatch text then
            YamlValue.Number(Decimal.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture))
        elif octPattern.IsMatch text then
            let digits = text.Substring(2)
            let value = digits |> Seq.fold (fun acc c -> acc * 8L + int64 (int c - int '0')) 0L
            YamlValue.Number(decimal value)
        elif hexPattern.IsMatch text then
            let digits = text.Substring(2)
            let value = Convert.ToInt64(digits, 16)
            YamlValue.Number(decimal value)
        elif negInfForms.Contains text then
            YamlValue.Float Double.NegativeInfinity
        elif posInfForms.Contains text then
            YamlValue.Float Double.PositiveInfinity
        elif nanForms.Contains text then
            YamlValue.Float Double.NaN
        else
            let floatMatch = floatPattern.Match text
            if floatMatch.Success then
                match tryExactDecimal floatMatch text with
                | Some d -> YamlValue.Number d
                | None -> YamlValue.Float(Double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture))
            else
                let dateMatch = datePattern.Match text
                let dateTimeMatch = dateTimePattern.Match text
                // Both regexes only constrain digit *shape*, not calendar validity (e.g. month
                // 13 still matches). An out-of-range component is therefore not a malformed
                // document — it just isn't a timestamp after all, so fall back to String rather
                // than letting the BCL date constructor throw.
                try
                    if dateTimeMatch.Success then
                        let g (i: int) = dateTimeMatch.Groups.[i].Value
                        let year, month, day = int (g 1), int (g 2), int (g 3)
                        let hour, minute, second = int (g 4), int (g 5), int (g 6)
                        let fraction = dateTimeMatch.Groups.[7]
                        let ticks =
                            if fraction.Success then
                                // fraction.Value includes the leading '.'
                                let frac = "0" + fraction.Value
                                int64 (Double.Parse(frac, CultureInfo.InvariantCulture) * 10_000_000.0)
                            else
                                0L
                        let offset = parseOffset (g 8)
                        let dt = DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified).AddTicks(ticks)
                        YamlValue.Timestamp(DateTimeOffset(dt, offset))
                    elif dateMatch.Success then
                        let g (i: int) = int dateMatch.Groups.[i].Value
                        YamlValue.Timestamp(DateTimeOffset(g 1, g 2, g 3, 0, 0, 0, TimeSpan.Zero))
                    else
                        YamlValue.String text
                with :? ArgumentOutOfRangeException ->
                    YamlValue.String text

    // ---------------------------------------------------------------------
    // Quoted-scalar unescaping
    // ---------------------------------------------------------------------

    /// Unescapes the content of a single-quoted scalar (the text *between* the quotes). The
    /// only escape in single-quoted scalars is `''` for a literal `'`. Always yields
    /// `YamlValue.String` material — callers wrap the result themselves, no resolution applies.
    let unescapeSingleQuoted (content: string) : string =
        let sb = StringBuilder(content.Length)
        let len = content.Length
        let mutable i = 0
        while i < len do
            if content.[i] = '\'' && i + 1 < len && content.[i + 1] = '\'' then
                sb.Append('\'') |> ignore
                i <- i + 2
            else
                sb.Append(content.[i]) |> ignore
                i <- i + 1
        sb.ToString()

    /// Unescapes the content of a double-quoted scalar (the text *between* the quotes).
    /// Supports `\x.. \u.... \U........ \0 \a \b \t \n \v \f \r \e \" \\ \N \_ \L \P` and
    /// escaped-newline folding: a `\` immediately before a line break consumes the break and
    /// any leading whitespace on the continuation line, contributing no character to the
    /// result. Raises `YamlParseException` on a malformed escape.
    ///
    /// Line/column in the raised exception are computed relative to `content` itself (this
    /// function has no notion of where `content` sits in the wider source; a parser translating
    /// this into a document-relative position is a later-phase concern).
    let unescapeDoubleQuoted (content: string) : string =
        let len = content.Length

        let positionOf (i: int) : int * int =
            let mutable ln = 1
            let mutable col = 1
            for j in 0 .. i - 1 do
                if content.[j] = '\n' then
                    ln <- ln + 1
                    col <- 1
                else
                    col <- col + 1
            (ln, col)

        let fail (i: int) (message: string) : 'a =
            let ln, col = positionOf i
            raise (YamlParseException(message, content, ln, col))

        let sb = StringBuilder(len)
        let mutable i = 0

        let parseHexEscape (escapeStart: int) (digitCount: int) : unit =
            // escapeStart points at the char following the introducer letter (x/u/U), i.e.
            // the first hex digit.
            if escapeStart + digitCount > len then
                fail escapeStart "Truncated hex escape sequence"
            let hex = content.Substring(escapeStart, digitCount)
            if not (hex |> Seq.forall Uri.IsHexDigit) then
                fail escapeStart ("Invalid hex escape sequence '" + hex + "'")
            let codepoint = Convert.ToInt32(hex, 16)
            try
                sb.Append(Char.ConvertFromUtf32 codepoint) |> ignore
            with :? ArgumentOutOfRangeException ->
                fail escapeStart ("Invalid Unicode code point U+" + codepoint.ToString("X"))
            i <- escapeStart + digitCount

        while i < len do
            let c = content.[i]
            if c <> '\\' then
                sb.Append(c) |> ignore
                i <- i + 1
            else
                if i + 1 >= len then
                    fail i "Unterminated escape sequence at end of scalar"
                let n = content.[i + 1]
                match n with
                | '0' -> sb.Append('\000') |> ignore; i <- i + 2
                | 'a' -> sb.Append('\a') |> ignore; i <- i + 2
                | 'b' -> sb.Append('\b') |> ignore; i <- i + 2
                | 't' -> sb.Append('\t') |> ignore; i <- i + 2
                | 'n' -> sb.Append('\n') |> ignore; i <- i + 2
                | 'v' -> sb.Append('\v') |> ignore; i <- i + 2
                | 'f' -> sb.Append('\f') |> ignore; i <- i + 2
                | 'r' -> sb.Append('\r') |> ignore; i <- i + 2
                | 'e' -> sb.Append('\u001B') |> ignore; i <- i + 2
                | '"' -> sb.Append('"') |> ignore; i <- i + 2
                | '\\' -> sb.Append('\\') |> ignore; i <- i + 2
                | 'N' -> sb.Append('\u0085') |> ignore; i <- i + 2
                | '_' -> sb.Append('\u00A0') |> ignore; i <- i + 2
                | 'L' -> sb.Append('\u2028') |> ignore; i <- i + 2
                | 'P' -> sb.Append('\u2029') |> ignore; i <- i + 2
                | 'x' -> parseHexEscape (i + 2) 2
                | 'u' -> parseHexEscape (i + 2) 4
                | 'U' -> parseHexEscape (i + 2) 8
                | '\n' ->
                    i <- i + 2
                    while i < len && (content.[i] = ' ' || content.[i] = '\t') do
                        i <- i + 1
                | '\r' ->
                    i <- i + 2
                    if i < len && content.[i] = '\n' then i <- i + 1
                    while i < len && (content.[i] = ' ' || content.[i] = '\t') do
                        i <- i + 1
                | other -> fail i ("Invalid escape sequence '\\" + string other + "'")

        sb.ToString()
