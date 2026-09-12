namespace FSharp.Data

open System
open System.Globalization
open System.IO
open System.Text

/// Raised by the emitter when writing a `YamlValue` would exceed the emitter's node-visit
/// budget (see `YamlEmitter.NodeBudget`). Aliases/anchors are resolved into shared, immutable
/// subtree references at parse time (Phase 6) with no marker distinguishing "this came from an
/// alias" from "this just happens to be structurally equal" — so the emitter has no way to
/// recognise a shared reference it has already fully walked and skip re-walking it. A
/// pathological shared-reference graph (`&a [*a, *a]` nested many levels deep) can therefore make
/// the *tree* the emitter walks exponentially large even though the underlying object graph is
/// tiny. Rather than hang or exhaust memory walking such a tree, the emitter counts every node it
/// visits and raises this exception once the count exceeds the budget.
type YamlEmitBudgetExceededException(message: string) =
    inherit Exception(message)

/// `WriteTo`/`ToString` for `YamlValue` and `YamlDocument`. Block style by default (2-space
/// indent), `YamlSaveOptions.Flow` for compact JSON-like output. The single quoting predicate
/// this all rests on is `YamlScalar.resolvePlainScalar s <> YamlValue.String s` — see
/// `needsQuotingBlock` below — plus a handful of structural checks (leading/trailing whitespace,
/// leading indicator chars, `: `/` #`, control chars) that `resolvePlainScalar` alone can't catch
/// since they're about *parsing*, not *resolution*.
module internal YamlEmitter =

    /// Maximum number of `YamlValue` nodes the emitter will visit while rendering a single
    /// document. Generous — far beyond any hand-written or realistically-generated document —
    /// purely as a guard against the pathological shared-reference graphs described on
    /// `YamlEmitBudgetExceededException`.
    [<Literal>]
    let NodeBudget = 2_000_000

    /// Chomping mode for a literal block scalar's trailing-newline count, mirroring
    /// `YamlParser`'s private `Chomp` type (duplicated here rather than shared since the parser's
    /// is private to that module).
    type private Chomp =
        | Strip
        | Clip
        | Keep

    // ---------------------------------------------------------------------
    // Quoting predicates
    // ---------------------------------------------------------------------

    /// Indicator characters that are only safe as the FIRST character of a plain scalar when
    /// quoted, per the plan: `- ? : # & * ! | > ' " % @ \` [ ] { } ,`.
    let private isLeadingIndicatorChar (c: char) =
        match c with
        | '-'
        | '?'
        | ':'
        | '#'
        | '&'
        | '*'
        | '!'
        | '|'
        | '>'
        | '\''
        | '"'
        | '%'
        | '@'
        | '`'
        | '['
        | ']'
        | '{'
        | '}'
        | ',' -> true
        | _ -> false

    let private hasControlChars (s: string) = s |> Seq.exists Char.IsControl

    /// The core quoting predicate for a single-line string in BLOCK context. The first check —
    /// `resolvePlainScalar s <> String s` — subsumes `"true"`, `"123"`, `"null"`, `"~"`, `""`,
    /// case variants, `.inf`/`.nan` shapes, timestamp-shaped text, and so on: anything that would
    /// resolve to a different `YamlValue` case (or a `String` with different content, though that
    /// can't actually happen for `resolvePlainScalar`) if left bare. The remaining checks catch
    /// things `resolvePlainScalar` has no opinion on because they're about how the *parser*
    /// delimits a plain scalar, not how it resolves one once delimited.
    let private needsQuotingBlock (s: string) : bool =
        YamlScalar.resolvePlainScalar s <> YamlValue.String s
        || s <> s.Trim()
        || (s.Length > 0 && isLeadingIndicatorChar s.[0])
        || s.Contains(": ")
        || s.EndsWith(":")
        || s.Contains(" #")
        || hasControlChars s

    /// Block-context quoting plus the flow-specific structural indicators (`,[]{}`) that are only
    /// significant inside `{...}`/`[...]`.
    let private needsQuotingFlow (s: string) : bool =
        needsQuotingBlock s
        || s |> Seq.exists (fun c -> c = ',' || c = '[' || c = ']' || c = '{' || c = '}')

    // ---------------------------------------------------------------------
    // Quoted-scalar rendering
    // ---------------------------------------------------------------------

    /// Escapes `s` for a double-quoted scalar: backslash/doublequote, the named single-char
    /// escapes `unescapeDoubleQuoted` understands (`\0 \a \b \t \n \v \f \r \e`), and any other
    /// control character as `\xHH`. Everything else (including non-ASCII printable text) is left
    /// as literal UTF-16 text — double-quoted scalars permit raw Unicode content.
    let private escapeDoubleQuoted (s: string) : string =
        let sb = StringBuilder(s.Length + 8)
        for c in s do
            match c with
            | '\\' -> sb.Append("\\\\") |> ignore
            | '"' -> sb.Append("\\\"") |> ignore
            | '\000' -> sb.Append("\\0") |> ignore
            | '\a' -> sb.Append("\\a") |> ignore
            | '\b' -> sb.Append("\\b") |> ignore
            | '\t' -> sb.Append("\\t") |> ignore
            | '\n' -> sb.Append("\\n") |> ignore
            | '\v' -> sb.Append("\\v") |> ignore
            | '\f' -> sb.Append("\\f") |> ignore
            | '\r' -> sb.Append("\\r") |> ignore
            | '\u001B' -> sb.Append("\\e") |> ignore
            | c when Char.IsControl c -> sb.Append("\\x").Append((int c).ToString("X2")) |> ignore
            | c -> sb.Append(c) |> ignore
        sb.ToString()

    let private doubleQuote (s: string) : string = "\"" + escapeDoubleQuoted s + "\""

    /// Single-quoted scalar: the only escape is `''` for a literal `'`. Never used for a string
    /// containing control characters (those force double-quoted — see `renderQuotedScalar`).
    let private singleQuote (s: string) : string = "'" + s.Replace("'", "''") + "'"

    /// Chooses between single- and double-quoted style for a string that needs quoting: double
    /// when it contains control characters (single-quoted has no escape mechanism at all), single
    /// otherwise (more readable, and the only escaping needed is doubling `'`).
    let private renderQuotedScalar (s: string) : string =
        if hasControlChars s then doubleQuote s else singleQuote s

    // ---------------------------------------------------------------------
    // Non-string scalar rendering
    // ---------------------------------------------------------------------

    let private renderNumber (n: decimal) : string = n.ToString(CultureInfo.InvariantCulture)

    /// Renders a float using round-trip-safe `"G17"` formatting (sufficient on every target
    /// framework, unlike the shortest-round-trip default `ToString()` which only round-trips
    /// reliably from .NET Core 3.0 onward). `RoundTripTests`' generator only ever produces finite
    /// floats that are *not* exactly `decimal`-representable (see that file for why), so the
    /// textual shape produced here is never at risk of being re-resolved as `Number` instead of
    /// `Float` on reparse.
    let private renderFloat (f: float) : string =
        if Double.IsNaN f then ".nan"
        elif Double.IsPositiveInfinity f then ".inf"
        elif Double.IsNegativeInfinity f then "-.inf"
        else f.ToString("G17", CultureInfo.InvariantCulture)

    /// Renders a timestamp with full 7-digit tick precision and an explicit `Z`/offset — `"K"`
    /// yields `Z` for `TimeSpan.Zero` and `+HH:mm`/`-HH:mm` otherwise, matching
    /// `YamlScalar.resolvePlainScalar`'s `dateTimePattern`.
    let private renderTimestamp (t: DateTimeOffset) : string =
        t.ToString("yyyy-MM-ddTHH:mm:ss.fffffffK", CultureInfo.InvariantCulture)

    let private isLeafScalar (v: YamlValue) =
        match v with
        | YamlValue.Mapping _
        | YamlValue.Sequence _ -> false
        | _ -> true

    // ---------------------------------------------------------------------
    // Multi-line string → literal ("|") block scalar
    // ---------------------------------------------------------------------

    /// True for a non-empty line composed entirely of the space character. Such a line, if it
    /// appeared verbatim as literal-block content, would be indistinguishable from a truly empty
    /// line to `YamlParser.parseBlockScalar` (it counts the *entire* leading run of spaces on a
    /// line before checking whether anything follows; a tab anywhere would break that run and
    /// keep the line safe, but an all-space line collapses to nothing) — so a string containing
    /// one disqualifies it from literal-block rendering entirely.
    let private isAllSpacesNonEmpty (line: string) = line.Length > 0 && line |> Seq.forall ((=) ' ')

    /// Decides whether `s` (known to be multi-line) can be rendered as a `|` literal block
    /// scalar, and if so, the physical lines to write (everything up to, but not including, the
    /// phantom empty segment that `String.Split` produces after a final `\n`) and which chomping
    /// indicator reproduces `s`'s exact trailing-newline count on reparse.
    ///
    /// Ineligible when `s` contains `\r` or any control character other than `\n`/`\t` (both
    /// excluded from `resolvePlainScalar`'s block-scalar territory entirely — those always fall
    /// back to a double-quoted single line with escapes), or when any physical line is
    /// non-empty-but-all-spaces (see `isAllSpacesNonEmpty`).
    let private tryLiteralBlockLines (s: string) : (string[] * Chomp) option =
        if not (s.Contains "\n") then
            None
        elif s |> Seq.exists (fun c -> Char.IsControl c && c <> '\n' && c <> '\t') then
            None
        else
            let endsWithNewline = s.EndsWith("\n")
            let parts = s.Split('\n')
            let physicalLines = if endsWithNewline then parts.[0 .. parts.Length - 2] else parts

            if physicalLines |> Array.exists isAllSpacesNonEmpty then
                None
            else
                let trailingNewlines =
                    let mutable n = 0
                    let mutable i = s.Length - 1
                    while i >= 0 && s.[i] = '\n' do
                        n <- n + 1
                        i <- i - 1
                    n

                let chomp =
                    if trailingNewlines = 0 then Strip
                    elif trailingNewlines = 1 then Clip
                    else Keep

                Some(physicalLines, chomp)

    /// Writes a literal block scalar's header (`|<digit><chomp>`, optionally followed by a
    /// trailing comment) and content lines, each indented by `contentIndent` spaces (blank lines
    /// written bare, matching `YamlParser.parseBlockScalar`'s own literal-join semantics).
    let private writeLiteralBlock
        (sb: StringBuilder)
        (contentIndent: int)
        (digit: int)
        (lines: string[])
        (chomp: Chomp)
        (trailingComment: string option)
        : unit =
        let chompChar =
            match chomp with
            | Strip -> "-"
            | Clip -> ""
            | Keep -> "+"

        sb.Append('|').Append(digit).Append(chompChar) |> ignore

        match trailingComment with
        | Some c -> sb.Append(" # ").Append(c) |> ignore
        | None -> ()

        sb.Append('\n') |> ignore
        let pad = String(' ', contentIndent)

        for line in lines do
            if line = "" then
                sb.Append('\n') |> ignore
            else
                sb.Append(pad).Append(line).Append('\n') |> ignore

    // ---------------------------------------------------------------------
    // Emission context (indent width, formatting flags, budget)
    // ---------------------------------------------------------------------

    type private EmitCtx =
        { IndentWidth: int
          DisableFormatting: bool
          Budget: int ref }

    let private bump (ctx: EmitCtx) =
        ctx.Budget.Value <- ctx.Budget.Value + 1

        if ctx.Budget.Value > NodeBudget then
            raise (
                YamlEmitBudgetExceededException(
                    "Emitting this value exceeded the node budget of "
                    + string NodeBudget
                    + " nodes. This usually \
                       means the value contains a pathological shared-reference structure (an \
                       anchor/alias graph resolved at parse time into shared subtrees, walked \
                       here as if each occurrence were separate content) rather than being a \
                       legitimately huge document."
                )
            )

    // ---------------------------------------------------------------------
    // Block-style emission
    // ---------------------------------------------------------------------

    /// How a mapping key should be rendered: a simple scalar key stays inline (`key: value`); a
    /// composite (`Mapping`/`Sequence`) key, or a multi-line `String` key (which can't appear on
    /// a single "simple key" line), uses the explicit `? key` / `: value` form.
    type private KeyMode =
        | SimpleKey of string
        | ExplicitKey

    let private renderScalarBlock (v: YamlValue) : string =
        match v with
        | YamlValue.String s -> if needsQuotingBlock s then renderQuotedScalar s else s
        | YamlValue.Number n -> renderNumber n
        | YamlValue.Float f -> renderFloat f
        | YamlValue.Boolean b -> if b then "true" else "false"
        | YamlValue.Timestamp t -> renderTimestamp t
        | YamlValue.Null -> "null"
        | YamlValue.Mapping _
        | YamlValue.Sequence _ -> invalidOp "renderScalarBlock: not a leaf scalar"

    let private keyRenderMode (k: YamlValue) : KeyMode =
        match k with
        | YamlValue.Mapping _
        | YamlValue.Sequence _ -> ExplicitKey
        | YamlValue.String s when s.Contains("\n") -> ExplicitKey
        | scalar -> SimpleKey(renderScalarBlock scalar)

    let private writeLeading (sb: StringBuilder) (pad: string) (comments: YamlNodeComments option) =
        match comments with
        | Some c ->
            for line in c.Leading do
                sb.Append(pad).Append("# ").Append(line).Append('\n') |> ignore
        | None -> ()

    let private trailingOf (comments: YamlNodeComments option) =
        match comments with
        | Some c -> c.Trailing
        | None -> None

    let private appendTrailingText (sb: StringBuilder) (trailing: string option) =
        match trailing with
        | Some c -> sb.Append(" # ").Append(c) |> ignore
        | None -> ()

    /// Emits a multi-line `String` at the current cursor position (already preceded, on the same
    /// line, by whatever prefix — `key: `, `- `, or nothing at the document root — the caller has
    /// written). Prefers a `|` literal block scalar (see `tryLiteralBlockLines`); falls back to a
    /// double-quoted single line with `\n` escaped when that isn't legal (control chars other
    /// than `\n`/`\t`, an all-spaces-but-nonempty line) or when `indentationSpaces` can't be
    /// expressed as the single-digit explicit indentation indicator literal block scalars need
    /// here (see the doc comment on `isDocumentRoot` below).
    ///
    /// `isDocumentRoot` is true only when this string *is* the entire document (no enclosing
    /// mapping/sequence at all) — `YamlParser.parseBlockScalar` is called with `parentIndent = -1`
    /// in that one case (every other call site passes the enclosing entry's own indent), so the
    /// explicit indentation-indicator digit needed to land content at `indent + IndentWidth` is
    /// one larger there than everywhere else.
    let rec private emitMultilineScalar
        (sb: StringBuilder)
        (ctx: EmitCtx)
        (indent: int)
        (isDocumentRoot: bool)
        (trailingComment: string option)
        (s: string)
        : unit =
        match tryLiteralBlockLines s with
        | Some(lines, chomp) ->
            let digit = if isDocumentRoot then ctx.IndentWidth + 1 else ctx.IndentWidth

            if digit >= 1 && digit <= 9 then
                writeLiteralBlock sb (indent + ctx.IndentWidth) digit lines chomp trailingComment
            else
                sb.Append(doubleQuote s) |> ignore
                appendTrailingText sb trailingComment
                sb.Append('\n') |> ignore
        | None ->
            sb.Append(doubleQuote s) |> ignore
            appendTrailingText sb trailingComment
            sb.Append('\n') |> ignore

    /// Emits one node's value, assuming the caller has already written whatever precedes it on
    /// its line (a mapping entry's `key:`, a sequence entry's `-`, or nothing at the document
    /// root) and passing `needsLeadingSpace` accordingly (false only at the document root, where
    /// there's no preceding token to separate from). `trailingComment`, if any, is emitted on
    /// whichever physical line "belongs" to this node — right after `key:`/`-` when the value
    /// continues on a new, more-indented line (a non-empty `Mapping`/`Sequence`, or a literal
    /// block scalar's header line), otherwise after the inline value itself.
    and private emitBlockNode
        (sb: StringBuilder)
        (ctx: EmitCtx)
        (indent: int)
        (needsLeadingSpace: bool)
        (isDocumentRoot: bool)
        (trailingComment: string option)
        (commentsOpt: Map<YamlPath, YamlNodeComments> option)
        (path: YamlPath)
        (v: YamlValue)
        : unit =
        bump ctx

        match v with
        | YamlValue.Mapping pairs when pairs.Length > 0 ->
            appendTrailingText sb trailingComment
            sb.Append('\n') |> ignore
            emitBlockMappingEntries sb ctx (indent + ctx.IndentWidth) commentsOpt path pairs
        | YamlValue.Mapping _ ->
            if needsLeadingSpace then
                sb.Append(' ') |> ignore

            sb.Append("{}") |> ignore
            appendTrailingText sb trailingComment
            sb.Append('\n') |> ignore
        | YamlValue.Sequence elems when elems.Length > 0 ->
            appendTrailingText sb trailingComment
            sb.Append('\n') |> ignore
            emitBlockSequenceEntries sb ctx (indent + ctx.IndentWidth) commentsOpt path elems
        | YamlValue.Sequence _ ->
            if needsLeadingSpace then
                sb.Append(' ') |> ignore

            sb.Append("[]") |> ignore
            appendTrailingText sb trailingComment
            sb.Append('\n') |> ignore
        | YamlValue.String s when s.Contains("\n") ->
            if needsLeadingSpace then
                sb.Append(' ') |> ignore

            emitMultilineScalar sb ctx indent isDocumentRoot trailingComment s
        | scalar ->
            if needsLeadingSpace then
                sb.Append(' ') |> ignore

            sb.Append(renderScalarBlock scalar) |> ignore
            appendTrailingText sb trailingComment
            sb.Append('\n') |> ignore

    and private emitBlockMappingEntries
        (sb: StringBuilder)
        (ctx: EmitCtx)
        (indent: int)
        (commentsOpt: Map<YamlPath, YamlNodeComments> option)
        (basePath: YamlPath)
        (pairs: (YamlValue * YamlValue)[])
        : unit =
        let pad = String(' ', indent)

        for k, v in pairs do
            bump ctx
            let childPath = basePath @ [ YamlPathStep.Key k ]
            let nodeComments = commentsOpt |> Option.bind (Map.tryFind childPath)
            writeLeading sb pad nodeComments
            let trailing = trailingOf nodeComments

            match keyRenderMode k with
            | SimpleKey keyText ->
                sb.Append(pad).Append(keyText).Append(':') |> ignore
                emitBlockNode sb ctx indent true false trailing commentsOpt childPath v
            | ExplicitKey ->
                sb.Append(pad).Append("? ") |> ignore

                match k with
                | YamlValue.String s ->
                    // A multi-line `String` key (the only `String` case that reaches
                    // `ExplicitKey`) is always rendered double-quoted, never as a `|`/`>` block
                    // scalar: `YamlParser`'s explicit-mapping-key parsing recognises a quoted
                    // scalar right after `? ` but has no support for a block-scalar header there
                    // at all — it would otherwise be scanned as if `|`/`>` were ordinary plain-
                    // scalar text.
                    sb.Append(doubleQuote s).Append('\n') |> ignore
                | _ -> emitBlockNode sb ctx indent false false None commentsOpt childPath k

                sb.Append(pad).Append(':') |> ignore
                emitBlockNode sb ctx indent true false trailing commentsOpt childPath v

    and private emitBlockSequenceEntries
        (sb: StringBuilder)
        (ctx: EmitCtx)
        (indent: int)
        (commentsOpt: Map<YamlPath, YamlNodeComments> option)
        (basePath: YamlPath)
        (elems: YamlValue[])
        : unit =
        let pad = String(' ', indent)
        let mutable idx = 0

        for v in elems do
            bump ctx
            let childPath = basePath @ [ YamlPathStep.Index idx ]
            let nodeComments = commentsOpt |> Option.bind (Map.tryFind childPath)
            writeLeading sb pad nodeComments
            let trailing = trailingOf nodeComments
            sb.Append(pad).Append('-') |> ignore
            emitBlockNode sb ctx indent true false trailing commentsOpt childPath v
            idx <- idx + 1

    /// Renders the whole document in block style, including the root node's own leading/trailing
    /// comments (path `[]`) when `commentsOpt` is `Some`.
    let private renderBlockRoot
        (ctx: EmitCtx)
        (commentsOpt: Map<YamlPath, YamlNodeComments> option)
        (v: YamlValue)
        : string =
        let sb = StringBuilder()
        bump ctx
        let rootComments = commentsOpt |> Option.bind (Map.tryFind [])
        writeLeading sb "" rootComments
        let trailing = trailingOf rootComments

        match v with
        | YamlValue.Mapping pairs when pairs.Length > 0 -> emitBlockMappingEntries sb ctx 0 commentsOpt [] pairs
        | YamlValue.Mapping _ ->
            sb.Append("{}") |> ignore
            appendTrailingText sb trailing
            sb.Append('\n') |> ignore
        | YamlValue.Sequence elems when elems.Length > 0 -> emitBlockSequenceEntries sb ctx 0 commentsOpt [] elems
        | YamlValue.Sequence _ ->
            sb.Append("[]") |> ignore
            appendTrailingText sb trailing
            sb.Append('\n') |> ignore
        | YamlValue.String s when s.Contains("\n") -> emitMultilineScalar sb ctx 0 true trailing s
        | scalar ->
            sb.Append(renderScalarBlock scalar) |> ignore
            appendTrailingText sb trailing
            sb.Append('\n') |> ignore

        sb.ToString()

    // ---------------------------------------------------------------------
    // Flow-style emission
    // ---------------------------------------------------------------------

    let rec private renderFlowValue (ctx: EmitCtx) (v: YamlValue) : string =
        bump ctx

        match v with
        | YamlValue.Mapping pairs ->
            if pairs.Length = 0 then
                "{}"
            else
                let sep = if ctx.DisableFormatting then "," else ", "
                // The space after ':' is NOT purely cosmetic in flow style: the parser only
                // recognises ':' as a mapping separator when followed by whitespace, EOF, or a
                // flow indicator (`colonTerminates` in YamlParser) — dropping it would make
                // "key:value" scan as one plain scalar whenever `value`'s first character is
                // ordinary text, silently corrupting the mapping. So this space stays even under
                // DisableFormatting.
                let colon = ": "

                let items =
                    pairs
                    |> Array.map (fun (k, v) -> renderFlowValue ctx k + colon + renderFlowValue ctx v)

                "{" + String.Join(sep, items) + "}"
        | YamlValue.Sequence elems ->
            if elems.Length = 0 then
                "[]"
            else
                let sep = if ctx.DisableFormatting then "," else ", "
                let items = elems |> Array.map (renderFlowValue ctx)
                "[" + String.Join(sep, items) + "]"
        | YamlValue.String s ->
            if s.Contains("\n") then doubleQuote s
            elif needsQuotingFlow s then renderQuotedScalar s
            else s
        | scalar -> renderScalarBlock scalar

    // ---------------------------------------------------------------------
    // Top-level entry point shared by YamlValue and YamlDocument
    // ---------------------------------------------------------------------

    /// Renders `v` per `saveOptions`/`indentationSpaces`. `commentsOpt`/`trailingComments` are
    /// `None`/`[]` for a plain `YamlValue` (or a `YamlDocument` with `SuppressComments` set); for
    /// an unsuppressed `YamlDocument` they're `Some doc.Comments`/`doc.Trailing`. Comment
    /// re-emission only applies to block-style output — a `YamlDocument` rendered with `Flow` set
    /// still emits its root-level `Trailing` comments (appended after the flow content) but not
    /// per-node leading/trailing comments, since there is no single line a node "belongs to" once
    /// everything is flattened onto one.
    let render
        (v: YamlValue)
        (commentsOpt: Map<YamlPath, YamlNodeComments> option)
        (trailingComments: string list)
        (saveOptions: YamlSaveOptions)
        (indentationSpaces: int)
        : string =
        let flow = saveOptions.HasFlag YamlSaveOptions.Flow
        let disableFormatting = saveOptions.HasFlag YamlSaveOptions.DisableFormatting
        let explicitMarkers = saveOptions.HasFlag YamlSaveOptions.ExplicitDocumentMarkers

        let ctx =
            { IndentWidth = indentationSpaces
              DisableFormatting = disableFormatting
              Budget = ref 0 }

        let bodyCore =
            if flow then
                renderFlowValue ctx v
            else
                renderBlockRoot ctx commentsOpt v

        let bodyWithTrailingComments =
            if trailingComments.IsEmpty then
                bodyCore
            else
                let ensureNL =
                    if bodyCore = "" || bodyCore.EndsWith("\n") then bodyCore else bodyCore + "\n"

                let commentLines = trailingComments |> List.map (fun c -> "# " + c) |> String.concat "\n"

                ensureNL + commentLines + "\n"

        // The trailing newline is only ever "purely cosmetic" — safe for `DisableFormatting` to
        // omit — when *we* are the one about to add it because `bodyWithTrailingComments` didn't
        // already end with one (which only happens in `Flow` style; block style always ends with
        // exactly one newline by construction, as does any trailing-comments suffix). A newline
        // the content already produced on its own must never be stripped here: it can be chomp-
        // significant (the very last thing in the document might be a `|`/`>` literal block
        // scalar, whose trailing-newline *count* is part of its value, per its chomping
        // indicator) — blindly removing "the" trailing newline would silently change that value
        // on reparse. So `DisableFormatting` only ever controls whether this function adds a
        // newline that wouldn't otherwise be there; it never removes one.
        let core =
            if bodyWithTrailingComments.EndsWith("\n") then bodyWithTrailingComments
            elif disableFormatting then bodyWithTrailingComments
            else bodyWithTrailingComments + "\n"

        if explicitMarkers then
            let sep = if core = "" || core.EndsWith("\n") then "" else "\n"
            let withMarkers = "---\n" + core + sep + "..."
            if disableFormatting then withMarkers else withMarkers + "\n"
        else
            core

[<AutoOpen>]
module YamlEmitterExtensions =

    /// Adds `WriteTo`/`ToString` to `YamlValue`.
    type YamlValue with

        /// Serializes to a `TextWriter` per `saveOptions`. `indentationSpaces` (default 2) is the
        /// number of spaces added per nesting level in block style; ignored in `Flow` style.
        member this.WriteTo(writer: TextWriter, saveOptions: YamlSaveOptions, ?indentationSpaces: int) : unit =
            let indent = defaultArg indentationSpaces 2
            writer.Write(YamlEmitter.render this None [] saveOptions indent)

        /// Serializes with the given formatting options. `indentationSpaces` defaults to 2.
        member this.ToString(saveOptions: YamlSaveOptions, ?indentationSpaces: int) : string =
            let indent = defaultArg indentationSpaces 2
            YamlEmitter.render this None [] saveOptions indent

        /// Serializes to block-style YAML with `YamlSaveOptions.None` and the given indentation
        /// (default 2). Note this only binds at a call site that supplies the argument (e.g.
        /// `x.ToString(4)` or `x.ToString()` — the latter via the optional parameter): F#/.NET
        /// intrinsic type extensions cannot `override` a virtual method declared in the type's
        /// original file, so a genuinely bare `x.ToString()` call still resolves to the
        /// compiler-generated structural `ToString()` from `[<StructuredFormatDisplay>]` in
        /// `YamlValue.fs` (unchanged Phase-1 behaviour) rather than this member. Use
        /// `ToString(YamlSaveOptions.None)` or `ToString(indentationSpaces = 2)` for YAML text
        /// from code that can't rely on overload resolution picking this member.
        member this.ToString(?indentationSpaces: int) : string =
            let indent = defaultArg indentationSpaces 2
            YamlEmitter.render this None [] YamlSaveOptions.None indent

    /// Adds `WriteTo`/`ToString`/comment re-emission to `YamlDocument`.
    type YamlDocument with

        /// Serializes to a `TextWriter`, re-emitting comments and directives unless
        /// `YamlSaveOptions.SuppressComments` is set.
        member this.WriteTo(writer: TextWriter, saveOptions: YamlSaveOptions, ?indentationSpaces: int) : unit =
            let indent = defaultArg indentationSpaces 2
            let suppress = saveOptions.HasFlag YamlSaveOptions.SuppressComments
            let comments = if suppress then None else Some this.Comments
            let trailing = if suppress then [] else this.Trailing
            writer.Write(YamlEmitter.render this.Value comments trailing saveOptions indent)

        /// Serializes with the given options, re-emitting comments unless
        /// `YamlSaveOptions.SuppressComments` is set. `indentationSpaces` defaults to 2.
        member this.ToString(saveOptions: YamlSaveOptions, ?indentationSpaces: int) : string =
            let indent = defaultArg indentationSpaces 2
            let suppress = saveOptions.HasFlag YamlSaveOptions.SuppressComments
            let comments = if suppress then None else Some this.Comments
            let trailing = if suppress then [] else this.Trailing
            YamlEmitter.render this.Value comments trailing saveOptions indent

        /// Serializes the document, re-emitting comments and directives, with `YamlSaveOptions.None`
        /// and the given indentation (default 2). See the analogous
        /// `YamlValue.ToString(?indentationSpaces)` for why a genuinely bare `x.ToString()` call
        /// still resolves to `YamlDocument`'s compiler-generated structural `ToString()` rather
        /// than this member.
        member this.ToString(?indentationSpaces: int) : string =
            let indent = defaultArg indentationSpaces 2
            YamlEmitter.render this.Value (Some this.Comments) this.Trailing YamlSaveOptions.None indent
