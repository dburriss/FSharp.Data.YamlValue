namespace FSharp.Data

open System.Text
open System.Collections.Generic

/// Recursive-descent parser. Phase 3 implemented **flow-style** grammar — `{...}` mappings,
/// `[...]` sequences, quoted and plain flow scalars, arbitrarily nested. Phase 4 adds
/// **block-style** (indentation-based) mappings and sequences, compact notation, and explicit
/// `? key` / `: value` pairs, calling into the flow parser wherever a flow node may appear (a
/// block mapping value or sequence entry can always be a flow collection). `YamlValue.Parse` now
/// tries block-style parsing first — real YAML is overwhelmingly block-style — falling back
/// naturally to flow parsing wherever the grammar allows a flow node. Phase 6 adds anchors
/// (`&name`), aliases (`*name`), merge keys (`<<: *base`), and core-tag (`!!str` etc.) overrides,
/// threaded through both the flow and block parsers via `ParseState`.
module internal YamlParser =

    open YamlReader

    /// Per-document parsing state threaded through both the flow and block parsers: the anchor
    /// table (name -> the already-fully-parsed `YamlValue` it names — a shared reference to the
    /// same immutable array-backed value, not a copy, since every alias site just looks the value
    /// up and reuses it), the set of anchor names whose node is still being constructed (used to
    /// guard against a recursive alias — see `resolveAlias`), and whether merge-key expansion is
    /// enabled for this parse.
    type internal ParseState(mergeKeysEnabled: bool) =
        let anchors = Dictionary<string, YamlValue>()
        let inFlight = HashSet<string>()

        /// Records that `name`'s node has started parsing — guards a direct or indirect
        /// self-reference for as long as this anchor's node is still under construction.
        member _.BeginAnchor(name: string) = inFlight.Add name |> ignore

        /// Records that `name`'s node finished parsing, storing its value (shared, not copied) in
        /// the anchor table for later aliases to resolve.
        member _.EndAnchor(name: string, value: YamlValue) =
            inFlight.Remove name |> ignore
            anchors.[name] <- value

        /// Whether `name` refers to an anchor whose node is still under construction.
        member _.IsInFlight(name: string) = inFlight.Contains name

        /// Looks up a previously completed anchor's value.
        member _.TryGetAnchor(name: string) =
            match anchors.TryGetValue name with
            | true, v -> Some v
            | false, _ -> None

        /// Whether merge-key (`<<`) expansion should be applied to mappings parsed under this
        /// state. See `applyMergeKeys`.
        member _.MergeKeysEnabled = mergeKeysEnabled

    /// Characters that delimit a flow scalar/collection: comma and the four bracket/brace
    /// chars. `:` is handled separately since whether it terminates a plain scalar depends on
    /// what follows it (see `scanFlowPlainScalar`).
    let private isFlowIndicator (c: char) =
        c = ',' || c = '[' || c = ']' || c = '{' || c = '}'

    // -----------------------------------------------------------------------
    // Anchors, aliases, and tags (Phase 6) — shared between flow and block parsing
    // -----------------------------------------------------------------------

    /// Scans an anchor or alias name (the text right after `&`/`*`, cursor already past the
    /// sigil). Per the YAML spec, anchor/alias names exclude whitespace, flow indicators, and
    /// `:`/`#`; `YamlValue` never surfaces anchor names itself, so only well-formedness of the
    /// *document* matters here — anything else (letters, digits, punctuation) is accepted.
    let private scanAnchorName (cur: Cursor) : string =
        let start = cur.Offset
        let isStop (c: char) =
            System.Char.IsWhiteSpace c || isFlowIndicator c || c = ':' || c = '#'
        while (match cur.Peek() with
               | Some c when not (isStop c) -> true
               | _ -> false) do
            cur.Advance()
        let name = cur.Source.Substring(start, cur.Offset - start)
        if name = "" then
            raise (cur.Error "Expected an anchor/alias name after '&'/'*'")
        name

    /// Scans a tag, cursor positioned at the leading `!`. Supports shorthand tags (`!foo`),
    /// core/secondary tags (`!!str`), and verbatim tags (`!<tag:example.com,2000:foo>`). Returns
    /// the tag exactly as written, `!`/`!!`/`!<...>` prefix included — `applyCoreTag` pattern-
    /// matches the core-schema forms (`!!str` etc.) and treats everything else (`!foo`, a
    /// verbatim tag) as unrecognised, leaving resolution untouched — `YamlValue` has no field to
    /// carry an arbitrary tag name, so that's the full extent of tag support in scope here.
    let private scanTag (cur: Cursor) : string =
        let start = cur.Offset
        cur.Advance() // leading '!'
        if cur.Peek() = Some '<' then
            cur.Advance()
            while cur.Peek() <> Some '>' && not cur.IsEof do
                cur.Advance()
            if cur.Peek() = Some '>' then cur.Advance()
        else
            if cur.Peek() = Some '!' then cur.Advance()
            let isStop (c: char) =
                System.Char.IsWhiteSpace c || isFlowIndicator c || c = '#'
            while (match cur.Peek() with
                   | Some c when not (isStop c) -> true
                   | _ -> false) do
                cur.Advance()
        cur.Source.Substring(start, cur.Offset - start)

    /// Resolves `*name` to the value stored under a previously completed anchor. Raises a parse
    /// error for both an unknown anchor and a recursive one (an alias to an anchor whose node is
    /// still under construction — see `ParseState.IsInFlight`). A single top-to-bottom
    /// recursive-descent pass can never encounter an alias to an anchor that hasn't appeared *at
    /// all* yet without also hitting one of those two cases first, so no third "known but not
    /// ready" state is needed.
    let private resolveAlias (cur: Cursor) (state: ParseState) (name: string) : YamlValue =
        if state.IsInFlight name then
            raise (cur.Error(sprintf "Recursive alias '*%s' refers to an anchor still being constructed" name))
        match state.TryGetAnchor name with
        | Some v -> v
        | None -> raise (cur.Error(sprintf "Unknown anchor '*%s'" name))

    /// Applies a core-schema tag's forced resolution to a scalar's *raw* (unresolved) text —
    /// called instead of `resolvePlainScalar` so that e.g. `!!str 123` yields `String "123"`
    /// rather than resolving to `Number` first and losing the original text. Unknown/custom tags
    /// fall back to normal `resolvePlainScalar` behaviour.
    let private applyCoreTag (cur: Cursor) (tag: string) (rawText: string) : YamlValue =
        match tag with
        | "!!str" -> YamlValue.String rawText
        | "!!null" -> YamlValue.Null
        | "!!bool" ->
            match rawText with
            | "true" | "True" | "TRUE" -> YamlValue.Boolean true
            | "false" | "False" | "FALSE" -> YamlValue.Boolean false
            | _ -> raise (cur.Error(sprintf "Invalid value '%s' for tag !!bool" rawText))
        | "!!int" ->
            match System.Decimal.TryParse(
                      rawText,
                      System.Globalization.NumberStyles.AllowLeadingSign,
                      System.Globalization.CultureInfo.InvariantCulture) with
            | true, d -> YamlValue.Number d
            | false, _ -> raise (cur.Error(sprintf "Invalid value '%s' for tag !!int" rawText))
        | "!!float" ->
            match System.Double.TryParse(
                      rawText,
                      System.Globalization.NumberStyles.Float,
                      System.Globalization.CultureInfo.InvariantCulture) with
            | true, f -> YamlValue.Float f
            | false, _ -> raise (cur.Error(sprintf "Invalid value '%s' for tag !!float" rawText))
        | "!!timestamp" ->
            match YamlScalar.resolvePlainScalar rawText with
            | YamlValue.Timestamp _ as t -> t
            | _ ->
                match System.DateTimeOffset.TryParse(
                          rawText,
                          System.Globalization.CultureInfo.InvariantCulture,
                          System.Globalization.DateTimeStyles.AssumeUniversal) with
                | true, dto -> YamlValue.Timestamp dto
                | false, _ -> raise (cur.Error(sprintf "Invalid value '%s' for tag !!timestamp" rawText))
        | "!!map" | "!!seq" ->
            // A shape mismatch (the tagged node turned out to be a plain scalar, not a mapping/
            // sequence) — fall back to normal resolution rather than erroring.
            YamlScalar.resolvePlainScalar rawText
        | _ -> YamlScalar.resolvePlainScalar rawText

    /// Applies a core tag to an already-parsed quoted or block scalar (always `YamlValue.String`
    /// beforehand). `!!str`, no tag, or an unrecognised tag leaves it untouched; the other core
    /// tags reinterpret its text the same way `applyCoreTag` would for a plain scalar. A
    /// collection value (mapping/sequence) is passed through unchanged regardless of tag.
    let private applyTagToScalar (cur: Cursor) (tagOpt: string option) (value: YamlValue) : YamlValue =
        match tagOpt, value with
        | Some tag, YamlValue.String s when tag <> "!!str" -> applyCoreTag cur tag s
        | _ -> value

    /// Merge-key (`<<: *base`) expansion — on by default per the plan, near-universal in
    /// docker-compose/GitLab CI. `items` is a mapping's entries in source order, `<<` entries
    /// included (there may be more than one, and a `<<` value may itself be a mapping or a
    /// sequence of mappings, e.g. `<<: [*a, *b]`). Precedence: an explicit key always wins over a
    /// merged-in one; among merge sources, an earlier one wins over a later one.
    let private applyMergeKeys (items: ResizeArray<YamlValue * YamlValue>) : (YamlValue * YamlValue)[] =
        let mergeKey = YamlValue.String "<<"
        let explicitPairs = ResizeArray<YamlValue * YamlValue>()
        let mergeSources = ResizeArray<YamlValue>()
        for (k, v) in items do
            if k = mergeKey then mergeSources.Add v
            else explicitPairs.Add((k, v))
        if mergeSources.Count = 0 then
            explicitPairs.ToArray()
        else
            let seenKeys = HashSet<YamlValue>(explicitPairs |> Seq.map fst)
            let mergedPairs = ResizeArray<YamlValue * YamlValue>()
            let addSource (source: YamlValue) =
                match source with
                | YamlValue.Mapping pairs ->
                    for (k, v) in pairs do
                        if seenKeys.Add k then mergedPairs.Add((k, v))
                | _ ->
                    // A non-mapping merge source is silently ignored — lenient, matching common
                    // real-world merge-key implementations rather than erroring.
                    ()
            for source in mergeSources do
                match source with
                | YamlValue.Sequence elems -> for e in elems do addSource e
                | other -> addSource other
            Array.append (explicitPairs.ToArray()) (mergedPairs.ToArray())

    /// Skips everything between flow tokens that carries no meaning: blanks, `#` comments, and
    /// line breaks. YAML flow collections may freely span multiple lines, so — unlike a
    /// block-context scan — line breaks are just more whitespace here.
    let rec skipFlowWhitespace (cur: Cursor) : unit =
        let mutable moved = true
        while moved do
            moved <- false
            if cur.SkipBlanks() > 0 then
                moved <- true
            match cur.Peek() with
            | Some '#' ->
                cur.SkipComment() |> ignore
                moved <- true
            | _ -> ()
            if cur.SkipLineBreak() then
                moved <- true

    /// Whether the char at the cursor's current position, followed by whatever comes next, would
    /// terminate a flow plain scalar if it were a `:` — i.e. the colon is a mapping-entry
    /// separator (followed by whitespace, EOF, or a flow indicator) rather than plain content
    /// (e.g. the `:` in a timestamp-shaped token like `12:30:00`).
    let private colonTerminates (cur: Cursor) : bool =
        match cur.PeekAt(1) with
        | None -> true
        | Some (' ' | '\t' | '\n' | '\r') -> true
        | Some c when isFlowIndicator c -> true
        | _ -> false

    /// Scans a plain (unquoted) scalar in flow context, starting at the cursor's current
    /// position, and returns its raw (unresolved) text with the cursor left positioned just
    /// after it (before any trailing blanks/terminator).
    ///
    /// Flow-context plain scalars are more restricted than block-context ones: they may not
    /// contain an unescaped `,` `[` `]` `{` `}` (those delimit flow structure) and a `: ` (colon
    /// followed by whitespace, EOF, or another flow indicator) also terminates the scalar — both
    /// because those characters would otherwise be ambiguous with flow/mapping syntax. A `#`
    /// only starts a comment when preceded by whitespace, so `a#b` is one scalar but `a #b` is
    /// the scalar `a` followed by a comment. Internal single blanks are preserved as part of the
    /// scalar's text (e.g. `a b` inside `[a b, c]` is the one scalar `"a b"`); only a run of
    /// blanks that leads into a terminator/comment/EOF/newline is excluded.
    let private scanFlowPlainScalar (cur: Cursor) : string =
        let start = cur.Position
        let mutable contentEnd = cur.Offset
        let mutable finished = false
        while not finished do
            match cur.Peek() with
            | None -> finished <- true
            | Some ('\n' | '\r') -> finished <- true
            | Some c when isFlowIndicator c -> finished <- true
            | Some ':' ->
                if colonTerminates cur then
                    finished <- true
                else
                    cur.Advance()
                    contentEnd <- cur.Offset
            | Some (' ' | '\t') ->
                let savedPos = cur.Position
                while (match cur.Peek() with
                       | Some (' ' | '\t') -> true
                       | _ -> false) do
                    cur.Advance()
                let stopsHere =
                    match cur.Peek() with
                    | None -> true
                    | Some ('\n' | '\r') -> true
                    | Some '#' -> true
                    | Some c when isFlowIndicator c -> true
                    | Some ':' -> colonTerminates cur
                    | _ -> false
                if stopsHere then
                    cur.Seek(savedPos)
                    finished <- true
            | Some _ ->
                cur.Advance()
                contentEnd <- cur.Offset
        cur.Source.Substring(start.Offset, contentEnd - start.Offset)

    /// Folds one maximal run of segments (raw text between the unescaped breaks that bound the
    /// run) per the quoted-scalar line-folding rule: leading whitespace is stripped from every
    /// segment except the first, trailing whitespace from every segment except the last (the
    /// first/last segments sit at a run boundary — either the scalar's own edge or an escaped
    /// break — and must be left untouched there), then a run of `k` blank (now-empty) segments
    /// folds to a single space when `k = 0` or `k` line breaks when `k >= 1`.
    let private foldRun (segs: ResizeArray<string>) : string =
        let n = segs.Count
        let sb = StringBuilder()
        let mutable pendingBlanks = 0
        let mutable started = false
        for idx in 0 .. n - 1 do
            let s0 = segs.[idx]
            let s1 = if idx > 0 then s0.TrimStart(' ', '\t') else s0
            let s = if idx < n - 1 then s1.TrimEnd(' ', '\t') else s1
            if s = "" then
                pendingBlanks <- pendingBlanks + 1
            else
                if pendingBlanks > 0 then
                    sb.Append(String.replicate pendingBlanks "\n") |> ignore
                elif started then
                    sb.Append(' ') |> ignore
                sb.Append(s) |> ignore
                started <- true
                pendingBlanks <- 0
        if pendingBlanks > 0 then
            sb.Append(String.replicate pendingBlanks "\n") |> ignore
        sb.ToString()

    /// Line-folds the raw (pre-unescape) content of a multi-line quoted scalar per YAML's quoted-
    /// scalar folding rule (see `foldRun`). For double-quoted scalars, a line break immediately
    /// preceded by an odd number of backslashes is a `\`-escaped line continuation — handled
    /// entirely by `YamlScalar.unescapeDoubleQuoted` — and is passed through here completely
    /// untouched (including the following line's leading whitespace) so that handling is not
    /// double-processed; folding resumes as a fresh run on both sides of it.
    let private foldQuotedRaw (raw: string) (isDouble: bool) : string =
        let len = raw.Length
        let segments = ResizeArray<string>()
        let breakTexts = ResizeArray<string>()
        let escaped = ResizeArray<bool>()
        let mutable segStart = 0
        let mutable i = 0
        let addBreak (breakEnd: int) (brk: string) =
            let seg = raw.Substring(segStart, i - segStart)
            segments.Add(seg)
            breakTexts.Add(brk)
            let mutable bs = 0
            let mutable k = seg.Length - 1
            while k >= 0 && seg.[k] = '\\' do
                bs <- bs + 1
                k <- k - 1
            escaped.Add(isDouble && bs % 2 = 1)
            i <- breakEnd
            segStart <- i
        while i < len do
            match raw.[i] with
            | '\r' ->
                let brk = if i + 1 < len && raw.[i + 1] = '\n' then "\r\n" else "\r"
                addBreak (i + brk.Length) brk
            | '\n' -> addBreak (i + 1) "\n"
            | _ -> i <- i + 1
        segments.Add(raw.Substring(segStart, len - segStart))
        if breakTexts.Count = 0 then
            raw
        else
            let sb = StringBuilder()
            let mutable runSegments = ResizeArray<string>()
            runSegments.Add(segments.[0])
            for bIdx in 0 .. breakTexts.Count - 1 do
                if escaped.[bIdx] then
                    sb.Append(foldRun runSegments) |> ignore
                    sb.Append(breakTexts.[bIdx]) |> ignore
                    runSegments <- ResizeArray<string>()
                runSegments.Add(segments.[bIdx + 1])
            sb.Append(foldRun runSegments) |> ignore
            sb.ToString()

    /// Parses a single-quoted flow scalar, cursor positioned at the opening `'`. Always yields
    /// `YamlValue.String` — no scalar resolution applies to quoted scalars. Multi-line content is
    /// line-folded (see `foldQuotedRaw`) before the single-quote unescape (`''` → `'`) is applied.
    let private parseSingleQuoted (cur: Cursor) : YamlValue =
        let startPos = cur.Position
        cur.Advance() // opening '
        let contentStart = cur.Offset
        let mutable endOffset = contentStart
        let mutable closed = false
        while not closed do
            match cur.Peek() with
            | None ->
                raise (cur.ErrorAt("Unclosed single-quoted scalar", startPos.Line, startPos.Column))
            | Some '\'' ->
                if cur.PeekAt(1) = Some '\'' then
                    cur.Advance(2)
                else
                    endOffset <- cur.Offset
                    cur.Advance()
                    closed <- true
            | Some _ -> cur.Advance()
        let raw = cur.Source.Substring(contentStart, endOffset - contentStart)
        let folded = if raw.IndexOfAny([| '\n'; '\r' |]) >= 0 then foldQuotedRaw raw false else raw
        YamlValue.String(YamlScalar.unescapeSingleQuoted folded)

    /// Strips the ` at line L, column C\n<snippet>` suffix that `YamlParseException`'s
    /// constructor appends, recovering the original message passed to `YamlReader.Cursor.Error`
    /// / `YamlScalar.unescapeDoubleQuoted`'s internal `fail`.
    let private originalMessage (ex: YamlParseException) : string =
        let marker = " at line "
        let idx = ex.Message.IndexOf(marker)
        if idx >= 0 then ex.Message.Substring(0, idx) else ex.Message

    /// Parses a double-quoted flow scalar, cursor positioned at the opening `"`. Always yields
    /// `YamlValue.String`. Escape decoding is delegated to `YamlScalar.unescapeDoubleQuoted`,
    /// which reports positions relative to the quoted content alone — a caught failure is
    /// re-raised here translated to the document's real line/column.
    let private parseDoubleQuoted (cur: Cursor) : YamlValue =
        let startPos = cur.Position
        cur.Advance() // opening "
        let contentPos = cur.Position
        let contentStart = cur.Offset
        let mutable endOffset = contentStart
        let mutable closed = false
        while not closed do
            match cur.Peek() with
            | None ->
                raise (cur.ErrorAt("Unclosed double-quoted scalar", startPos.Line, startPos.Column))
            | Some '\\' ->
                cur.Advance()
                if not cur.IsEof then cur.Advance()
            | Some '"' ->
                endOffset <- cur.Offset
                cur.Advance()
                closed <- true
            | Some _ -> cur.Advance()
        let raw = cur.Source.Substring(contentStart, endOffset - contentStart)
        let folded = if raw.IndexOfAny([| '\n'; '\r' |]) >= 0 then foldQuotedRaw raw true else raw
        try
            YamlValue.String(YamlScalar.unescapeDoubleQuoted folded)
        with :? YamlParseException as ex ->
            // ex.Line/ex.Column are 1-based, relative to `raw` alone. Translate onto the
            // document: line 1 of `raw` continues on `contentPos`'s line; later lines of `raw`
            // start at column 1 in the document too, offset only by how many lines in.
            let actualLine, actualColumn =
                if ex.Line = 1 then
                    contentPos.Line, contentPos.Column + ex.Column - 1
                else
                    contentPos.Line + ex.Line - 1, ex.Column
            raise (cur.ErrorAt(originalMessage ex, actualLine, actualColumn))

    /// Parses one flow node — anchor, alias, tag, mapping, sequence, quoted scalar, or plain
    /// scalar — starting at the cursor's current position (surrounding whitespace/comments must
    /// already be skipped by the caller, matching the convention used throughout this module:
    /// every `parse*` function leaves the cursor immediately after what it consumed, and every
    /// caller calls `skipFlowWhitespace` before looking at the next token).
    let rec parseFlowNode (cur: Cursor) (state: ParseState) : YamlValue =
        parseFlowNodeWithTag cur state None

    /// `parseFlowNode`, but honouring a tag consumed by an enclosing `!tag` prefix (`None` when
    /// there wasn't one) — kept separate so that a tagged *scalar* can be resolved straight from
    /// its raw text via `applyCoreTag`, rather than resolving it first with `resolvePlainScalar`
    /// and losing the original text (which matters for e.g. `!!str 123`).
    and private parseFlowNodeWithTag (cur: Cursor) (state: ParseState) (tagOpt: string option) : YamlValue =
        match cur.Peek() with
        | Some '&' ->
            cur.Advance()
            let name = scanAnchorName cur
            state.BeginAnchor name
            skipFlowWhitespace cur
            let value = parseFlowNodeWithTag cur state tagOpt
            state.EndAnchor(name, value)
            value
        | Some '*' ->
            cur.Advance()
            let name = scanAnchorName cur
            resolveAlias cur state name
        | Some '!' ->
            let tag = scanTag cur
            skipFlowWhitespace cur
            parseFlowNodeWithTag cur state (Some tag)
        | Some '{' -> parseFlowMapping cur state
        | Some '[' -> parseFlowSequence cur state
        | Some '\'' -> applyTagToScalar cur tagOpt (parseSingleQuoted cur)
        | Some '"' -> applyTagToScalar cur tagOpt (parseDoubleQuoted cur)
        | Some ':' -> raise (cur.Error "Expected a value, found ':'")
        | Some c when isFlowIndicator c ->
            raise (cur.Error(sprintf "Expected a value, found '%c'" c))
        | Some _ ->
            let text = scanFlowPlainScalar cur
            match tagOpt with
            | Some tag -> applyCoreTag cur tag text
            | None -> YamlScalar.resolvePlainScalar text
        | None -> raise (cur.Error "Unexpected end of input, expected a value")

    /// Parses one flow-sequence entry. Supports YAML's "compact" single-pair flow mapping
    /// shorthand inside a sequence (`[a: b, c]`, equivalent to `[{a: b}, c]`) — after parsing the
    /// first node, a following `:` (not already consumed, since `scanFlowPlainScalar` stops
    /// before a terminating colon) turns the entry into a one-pair mapping.
    and private parseFlowSequenceEntry (cur: Cursor) (state: ParseState) : YamlValue =
        let node = parseFlowNode cur state
        skipFlowWhitespace cur
        match cur.Peek() with
        | Some ':' ->
            cur.Advance()
            skipFlowWhitespace cur
            let value =
                match cur.Peek() with
                | Some (',' | ']') -> YamlValue.Null
                | _ -> parseFlowNode cur state
            YamlValue.Mapping [| (node, value) |]
        | _ -> node

    /// Parses a flow mapping, cursor positioned at the opening `{`. Supports trailing commas
    /// (`{a: 1,}`) and key-only entries with an implicit `Null` value (`{a}` / `{a,}`). Merge
    /// keys (`<<: *base`) are expanded per `state.MergeKeysEnabled` — see `applyMergeKeys`.
    and private parseFlowMapping (cur: Cursor) (state: ParseState) : YamlValue =
        let openPos = cur.Position
        cur.Advance() // opening {
        let items = ResizeArray<YamlValue * YamlValue>()
        skipFlowWhitespace cur
        if cur.Peek() = Some '}' then
            cur.Advance()
        else
            let mutable more = true
            while more do
                if cur.IsEof then
                    raise (cur.ErrorAt("Unclosed flow mapping — missing '}'", openPos.Line, openPos.Column))
                let key = parseFlowNode cur state
                skipFlowWhitespace cur
                let value =
                    match cur.Peek() with
                    | Some ':' ->
                        cur.Advance()
                        skipFlowWhitespace cur
                        match cur.Peek() with
                        | Some (',' | '}') -> YamlValue.Null
                        | _ -> parseFlowNode cur state
                    | _ -> YamlValue.Null
                items.Add((key, value))
                skipFlowWhitespace cur
                match cur.Peek() with
                | Some ',' ->
                    cur.Advance()
                    skipFlowWhitespace cur
                    if cur.Peek() = Some '}' then
                        cur.Advance()
                        more <- false
                | Some '}' ->
                    cur.Advance()
                    more <- false
                | _ -> raise (cur.Error "Expected ',' or '}' in flow mapping")
        let finalItems = if state.MergeKeysEnabled then applyMergeKeys items else items.ToArray()
        YamlValue.Mapping finalItems

    /// Parses a flow sequence, cursor positioned at the opening `[`. Supports trailing commas
    /// (`[1, 2,]`) and the compact flow-pair shorthand via `parseFlowSequenceEntry`.
    and private parseFlowSequence (cur: Cursor) (state: ParseState) : YamlValue =
        let openPos = cur.Position
        cur.Advance() // opening [
        let items = ResizeArray<YamlValue>()
        skipFlowWhitespace cur
        if cur.Peek() = Some ']' then
            cur.Advance()
        else
            let mutable more = true
            while more do
                if cur.IsEof then
                    raise (cur.ErrorAt("Unclosed flow sequence — missing ']'", openPos.Line, openPos.Column))
                let elem = parseFlowSequenceEntry cur state
                items.Add(elem)
                skipFlowWhitespace cur
                match cur.Peek() with
                | Some ',' ->
                    cur.Advance()
                    skipFlowWhitespace cur
                    if cur.Peek() = Some ']' then
                        cur.Advance()
                        more <- false
                | Some ']' ->
                    cur.Advance()
                    more <- false
                | _ -> raise (cur.Error "Expected ',' or ']' in flow sequence")
        YamlValue.Sequence(items.ToArray())

    // -----------------------------------------------------------------------
    // Block parser (Phase 4)
    // -----------------------------------------------------------------------
    //
    // Indentation bookkeeping is the crux of block parsing. The convention used throughout this
    // section: every `parse*` function that parses "the node at this position" is called with
    // the cursor already positioned exactly at the first content column of that node (i.e. any
    // leading indentation has already been consumed) and an `indent` (0-based column number, ==
    // number of leading spaces) that means one of two things depending on the function:
    //   - for `parseBlockMapping`/`parseBlockSequence`, `indent` is the column at which *every*
    //     sibling entry of this collection must start;
    //   - for `parseIndentedValue`, `parentIndent` is the enclosing entry's own indent, and a
    //     nested value is only recognised if the next content line is indented *more* than it.
    //
    // The `-`-contributes-to-indent rule (and compact notation `- key: value` / `- - a`) falls
    // out of one piece of plumbing: after consuming a sequence's `-` (or a mapping's `:`), the
    // column immediately following it becomes the anchor indent handed to `parseNodeAt` for
    // whatever comes next *on the same line*. If nothing follows on the same line, the anchor
    // indent handed to `parseIndentedValue` instead is the indent of the `-`/key itself (so a
    // nested block on a following line must be indented past the `-`/key, not just past its
    // value's would-be column).
    //
    // Phase 6 note: `&anchor`/`!tag` prefixes on a block-context node are treated exactly like
    // another marker (`-`/`:`/`?`) — `parseNodeAtWithTag` consumes the sigil then recurses into
    // `parseValueAfterMarkerWithTag` using the *same* `parentIndent` it was itself given, so a
    // node following `&anchor`/`!tag` (inline, or indented on a following line) is bound by
    // exactly the same indentation rule as the marker it decorates.

    /// Skips a run of blank lines and full-line (or trailing) comments, leaving the cursor
    /// positioned at the start of a line with real content, or at EOF. Used between block
    /// entries and to find the start/end of a document. Does not skip past an illegal
    /// indentation tab — it leaves that line unconsumed so the caller's own `CountIndent` call
    /// discovers (and reports) the same tab error.
    let rec private skipBlankLines (cur: Cursor) : unit =
        let mutable moved = true
        while moved do
            moved <- false
            let lineStart = cur.Position
            let spaces, tabError = cur.CountIndent()
            match tabError with
            | Some _ -> ()
            | None ->
                cur.Advance(spaces)
                match cur.Peek() with
                | None -> ()
                | Some '#' ->
                    cur.SkipComment() |> ignore
                    cur.SkipLineBreak() |> ignore
                    moved <- true
                | Some ('\n' | '\r') ->
                    cur.SkipLineBreak() |> ignore
                    moved <- true
                | Some _ -> cur.Seek(lineStart)

    /// Builds the "tab in indentation" error at the position `CountIndent` reported (`off` is
    /// relative to the cursor's position when `CountIndent` was called; the tab is always on the
    /// same line since `CountIndent` stops scanning at the first line break).
    let private tabIndentError (cur: Cursor) (off: int) : YamlParseException =
        cur.ErrorAt("Tab characters are not allowed in indentation", cur.Line, cur.Column + off)

    /// True when the char at the cursor is `#` and is a genuine comment start (preceded by
    /// whitespace, or at column 1) — as opposed to a `#` embedded directly in a plain scalar.
    let private isCommentStart (cur: Cursor) : bool =
        match cur.Peek() with
        | Some '#' ->
            cur.Column = 1
            || (match cur.PeekAt(-1) with
                | Some (' ' | '\t') -> true
                | _ -> false)
        | _ -> false

    /// Like `skipBlankLines`, but counts the blank/comment-only lines skipped — used by
    /// multi-line plain-scalar folding to know how many line breaks a gap between two content
    /// lines represents. Leaves the cursor positioned at the start of a line with real content, at
    /// EOF, or (like `skipBlankLines`) at an illegal indentation tab for the caller to discover.
    let private countAndSkipBlankLines (cur: Cursor) : int =
        let mutable count = 0
        let mutable moved = true
        while moved do
            moved <- false
            let lineStart = cur.Position
            let spaces, tabError = cur.CountIndent()
            match tabError with
            | Some _ -> ()
            | None ->
                cur.Advance(spaces)
                match cur.Peek() with
                | None -> ()
                | Some '#' when isCommentStart cur ->
                    cur.SkipComment() |> ignore
                    cur.SkipLineBreak() |> ignore
                    count <- count + 1
                    moved <- true
                | Some ('\n' | '\r') ->
                    cur.SkipLineBreak() |> ignore
                    count <- count + 1
                    moved <- true
                | Some _ -> cur.Seek(lineStart)
        count

    /// True when the cursor is at a `-` that acts as a block-sequence entry indicator, i.e. it is
    /// followed by whitespace, a line break, or EOF — not e.g. the `-` of a negative number like
    /// `-1`.
    let private isDashIndicator (cur: Cursor) : bool =
        cur.Peek() = Some '-'
        && (match cur.PeekAt(1) with
            | None
            | Some (' ' | '\t' | '\n' | '\r') -> true
            | _ -> false)

    /// True when the cursor is at a `?` that acts as an explicit-mapping-key indicator, i.e. it
    /// is followed by whitespace, a line break, or EOF.
    let private isExplicitKeyIndicator (cur: Cursor) : bool =
        cur.Peek() = Some '?'
        && (match cur.PeekAt(1) with
            | None
            | Some (' ' | '\t' | '\n' | '\r') -> true
            | _ -> false)

    /// Whether a `:` at the cursor's position terminates a block-context plain scalar — true
    /// when it is followed by whitespace, a line break, or EOF (a bare "key:value" without a
    /// following space is NOT a mapping-entry colon, matching YAML's requirement of ": " or
    /// ":<EOL>").
    let private colonTerminatesBlock (cur: Cursor) : bool =
        match cur.PeekAt(1) with
        | None
        | Some (' ' | '\t' | '\n' | '\r') -> true
        | _ -> false

    /// Scans a plain (unquoted) scalar in block context, starting at the cursor's current
    /// position, stopping at end of line, a genuine comment, or a mapping-entry-terminating `:`.
    /// Unlike flow-context plain scalars, block-context plain scalars may freely contain
    /// `, [ ] { }` — those only delimit structure inside an actual flow collection. No line
    /// folding is attempted here (multi-line plain scalars are Phase 5); the scalar is whatever
    /// remains on the current physical line.
    let private scanBlockPlainScalar (cur: Cursor) : string =
        let start = cur.Position
        let mutable contentEnd = cur.Offset
        let mutable finished = false
        while not finished do
            match cur.Peek() with
            | None -> finished <- true
            | Some ('\n' | '\r') -> finished <- true
            | Some '#' when isCommentStart cur -> finished <- true
            | Some ':' when colonTerminatesBlock cur -> finished <- true
            | Some (' ' | '\t') ->
                let savedPos = cur.Position
                while (match cur.Peek() with
                       | Some (' ' | '\t') -> true
                       | _ -> false) do
                    cur.Advance()
                let stopsHere =
                    match cur.Peek() with
                    | None -> true
                    | Some ('\n' | '\r') -> true
                    | Some '#' when isCommentStart cur -> true
                    | Some ':' -> colonTerminatesBlock cur
                    | _ -> false
                if stopsHere then
                    cur.Seek(savedPos)
                    finished <- true
            | Some _ ->
                cur.Advance()
                contentEnd <- cur.Offset
        cur.Source.Substring(start.Offset, contentEnd - start.Offset)

    /// Consumes (without interpreting) a quoted scalar for lookahead purposes only — used by
    /// `looksLikeMappingEntry` to skip past a quoted key before checking for the `:` that would
    /// follow it. Tolerant of an unterminated quote (just stops at EOF); the real parse (via
    /// `parseSingleQuoted`/`parseDoubleQuoted`) is what actually reports an unclosed-quote error.
    let private skipQuotedForLookahead (cur: Cursor) : unit =
        match cur.Peek() with
        | Some '\'' ->
            cur.Advance()
            let mutable closed = false
            while not closed && not cur.IsEof do
                match cur.Peek() with
                | Some '\'' ->
                    cur.Advance()
                    if cur.Peek() = Some '\'' then cur.Advance() else closed <- true
                | Some _ -> cur.Advance()
                | None -> closed <- true
        | Some '"' ->
            cur.Advance()
            let mutable closed = false
            while not closed && not cur.IsEof do
                match cur.Peek() with
                | Some '\\' ->
                    cur.Advance()
                    if not cur.IsEof then cur.Advance()
                | Some '"' ->
                    cur.Advance()
                    closed <- true
                | Some _ -> cur.Advance()
                | None -> closed <- true
        | _ -> ()

    /// Lookahead-only: whether the content starting at the cursor is a mapping entry — a plain
    /// or quoted scalar key immediately followed (after any quote, for a quoted key) by a `:`
    /// that terminates per `colonTerminatesBlock`. Restores the cursor to its original position
    /// before returning either way.
    let private looksLikeMappingEntry (cur: Cursor) : bool =
        let saved = cur.Position
        (match cur.Peek() with
         | Some ('\'' | '"') -> skipQuotedForLookahead cur
         | _ ->
             let mutable stop = false
             while not stop do
                 match cur.Peek() with
                 | None -> stop <- true
                 | Some ('\n' | '\r') -> stop <- true
                 | Some '#' when isCommentStart cur -> stop <- true
                 | Some ':' when colonTerminatesBlock cur -> stop <- true
                 | Some _ -> cur.Advance())
        let isColon =
            match cur.Peek() with
            | Some ':' -> colonTerminatesBlock cur
            | _ -> false
        cur.Seek(saved)
        isColon

    /// Parses a block-mapping key that is a plain or quoted scalar. Resolution applies to plain
    /// keys exactly as it does to values — a key like `42` or `true` yields the resolved
    /// (non-string) `YamlValue`, per the deliberate `Mapping of (YamlValue * YamlValue)[]` design.
    let private parseBlockMappingKey (cur: Cursor) : YamlValue =
        match cur.Peek() with
        | Some '\'' -> parseSingleQuoted cur
        | Some '"' -> parseDoubleQuoted cur
        | _ -> YamlScalar.resolvePlainScalar ((scanBlockPlainScalar cur).Trim())

    /// Scans a plain scalar in block context across as many physical lines as continue it, per
    /// the same line-folding rule used elsewhere (`foldRun`): a continuation line must be
    /// indented strictly more than `parentIndent` (the enclosing mapping/sequence's own indent —
    /// not the scalar's own starting column, so the common `key: this is\n  folded` shape, where
    /// the continuation is indented less than where "this" started, is legal). The scalar ends at
    /// EOF, at a line at or below `parentIndent`, or — even when more indented — at a line that
    /// looks like the start of a new block construct (a mapping entry, a `-` sequence entry, or a
    /// `?` explicit key): such a line is never swallowed as continuation text, since it can never
    /// be valid content mid-scalar; leaving it alone lets the enclosing collection's own
    /// indentation check raise its usual "inconsistent indentation" error for it. A blank line
    /// between two content lines is folded per line-break count exactly as `foldRun` does (0
    /// blanks → space, k blanks → k newlines). The final folded text is what the caller passes to
    /// `resolvePlainScalar` — scalar resolution runs on the whole folded string, not per physical
    /// line.
    let private scanBlockPlainScalarMultiLine (cur: Cursor) (parentIndent: int) : string =
        let sb = StringBuilder()
        sb.Append((scanBlockPlainScalar cur).Trim()) |> ignore
        let mutable continueLoop = true
        while continueLoop do
            if not (cur.IsLineBreak()) then
                continueLoop <- false
            else
                let saved = cur.Position
                cur.SkipLineBreak() |> ignore
                let blanks = countAndSkipBlankLines cur
                if cur.IsEof then
                    cur.Seek(saved)
                    continueLoop <- false
                else
                    let curIndent, tabError = cur.CountIndent()
                    match tabError with
                    | Some off -> raise (tabIndentError cur off)
                    | None -> ()
                    if curIndent <= parentIndent then
                        cur.Seek(saved)
                        continueLoop <- false
                    else
                        cur.Advance(curIndent)
                        if isDashIndicator cur || isExplicitKeyIndicator cur || looksLikeMappingEntry cur then
                            cur.Seek(saved)
                            continueLoop <- false
                        else
                            let lineText = (scanBlockPlainScalar cur).Trim()
                            if blanks = 0 then sb.Append(' ') |> ignore
                            else sb.Append(String.replicate blanks "\n") |> ignore
                            sb.Append(lineText) |> ignore
        sb.ToString()

    // -----------------------------------------------------------------------
    // `|` literal and `>` folded block scalars (Phase 5)
    // -----------------------------------------------------------------------

    /// Chomping mode from a block scalar header's optional `-`/`+` indicator.
    type private Chomp =
        | Clip
        | Strip
        | Keep

    /// Joins `core` (the block scalar's content lines with any trailing blank lines already
    /// excluded — see `parseBlockScalar`) literally: each line as-is (indentation already
    /// stripped to the block's content indent), a blank line contributing an empty line, no
    /// folding of any kind. This is the `|` style.
    let private buildLiteralBody (core: string option list) : string =
        core |> List.map (function None -> "" | Some s -> s) |> String.concat "\n"

    /// Joins `core` per the `>` folded-scalar rule: a single line break between two content lines
    /// that are both at the block's own content indent (not "more-indented") folds to a space; a
    /// run of `k` blank lines folds to `k` newlines; and — the "more-indented lines aren't
    /// folded" exception (YAML 1.2 §8.1.3) — any line break adjacent to a line indented deeper
    /// than the content indent (recognisable here as a stripped line whose text still starts with
    /// a space or tab) is kept as a literal newline instead of being folded to a space, even when
    /// there are zero intervening blank lines.
    let private buildFoldedBody (core: string option list) : string =
        let isMoreIndented (s: string) = s.Length > 0 && (s.[0] = ' ' || s.[0] = '\t')
        let sb = StringBuilder()
        let mutable pendingBlanks = 0
        let mutable prevKind: bool option = None // Some true = more-indented, Some false = plain
        for item in core do
            match item with
            | None -> pendingBlanks <- pendingBlanks + 1
            | Some s ->
                let moreIndented = isMoreIndented s
                if pendingBlanks > 0 then
                    sb.Append(String.replicate pendingBlanks "\n") |> ignore
                    if prevKind = Some true || moreIndented then
                        sb.Append('\n') |> ignore
                elif prevKind.IsSome then
                    if prevKind = Some false && not moreIndented then sb.Append(' ') |> ignore
                    else sb.Append('\n') |> ignore
                sb.Append(s) |> ignore
                prevKind <- Some moreIndented
                pendingBlanks <- 0
        sb.ToString()

    /// Parses a `|` literal or `>` folded block scalar, cursor positioned at the `|`/`>` itself.
    /// `parentIndent` is the enclosing entry's own indent (the dash's column for a sequence entry,
    /// the mapping's indent for a mapping value, or `-1` at the document root) — content lines
    /// must be indented strictly more than this. Always yields `YamlValue.String`; no scalar
    /// resolution ever applies to block scalars.
    ///
    /// Header grammar: `|`/`>` optionally followed by a chomping indicator (`-`/`+`) and/or an
    /// explicit indentation indicator (a digit `1`-`9`), in *either* order (`|2-` and `|-2` are
    /// both accepted, matching the YAML 1.2 grammar's `c-b-block-header`), then optional blanks
    /// and an optional trailing comment.
    ///
    /// Indentation: with an explicit indicator, content indent = `parentIndent + digit`.
    /// Otherwise it is auto-detected from the first non-empty content line — leading blank lines
    /// are skipped over (and do not influence detection) — which is a deliberately lenient
    /// reading of the spec's requirement for an explicit indicator whenever the first line would
    /// otherwise be ambiguous (e.g. it starts with a blank line): auto-detecting from the first
    /// non-empty line is what most implementations do in practice and avoids a spurious error for
    /// the common case.
    ///
    /// Chomping: `-` (strip) drops the final line break entirely; `+` (keep) preserves every
    /// trailing line break/blank line; the default (clip) keeps exactly one trailing line break
    /// and drops any further trailing blank lines.
    let private parseBlockScalar (cur: Cursor) (parentIndent: int) : YamlValue =
        let isFolded = cur.Peek() = Some '>'
        cur.Advance() // consume '|' or '>'
        let mutable chomp = Clip
        let mutable explicitIndent: int option = None
        let mutable seenChomp = false
        let mutable seenIndent = false
        let mutable moreHeader = true
        while moreHeader do
            match cur.Peek() with
            | Some '-' when not seenChomp ->
                chomp <- Strip
                seenChomp <- true
                cur.Advance()
            | Some '+' when not seenChomp ->
                chomp <- Keep
                seenChomp <- true
                cur.Advance()
            | Some c when System.Char.IsDigit c && c <> '0' && not seenIndent ->
                explicitIndent <- Some(int c - int '0')
                seenIndent <- true
                cur.Advance()
            | _ -> moreHeader <- false
        cur.SkipBlanks() |> ignore
        (match cur.Peek() with
         | None
         | Some ('\n' | '\r') -> ()
         | Some '#' when isCommentStart cur -> cur.SkipComment() |> ignore
         | Some c -> raise (cur.Error(sprintf "Invalid character '%c' in block scalar header" c)))
        cur.SkipLineBreak() |> ignore

        let lines = ResizeArray<string option>()
        let mutable contentIndent =
            match explicitIndent with
            | Some n -> parentIndent + n
            | None -> -1
        let mutable terminated = false
        while not terminated && not cur.IsEof do
            let lineStart = cur.Position
            let spaces, tabError = cur.CountIndent()
            match tabError with
            | Some off when contentIndent < 0 || spaces < contentIndent -> raise (tabIndentError cur off)
            | _ -> ()
            cur.Advance(spaces)
            match cur.Peek() with
            | None -> lines.Add(None)
            | Some ('\n' | '\r') ->
                cur.SkipLineBreak() |> ignore
                lines.Add(None)
            | Some _ ->
                if contentIndent < 0 && spaces <= parentIndent then
                    cur.Seek(lineStart)
                    terminated <- true
                else
                    if contentIndent < 0 then
                        contentIndent <- spaces
                    if spaces < contentIndent then
                        cur.Seek(lineStart)
                        terminated <- true
                    else
                        let extra = spaces - contentIndent
                        let restStart = cur.Offset
                        while not (cur.IsEndOfLine()) do
                            cur.Advance()
                        let rest = cur.Source.Substring(restStart, cur.Offset - restStart)
                        lines.Add(Some(System.String(' ', extra) + rest))
                        cur.SkipLineBreak() |> ignore
        let total = lines.Count
        let lastContentIdx =
            let mutable idx = -1
            for i in 0 .. total - 1 do
                if lines.[i].IsSome then idx <- i
            idx
        let core = if lastContentIdx >= 0 then [ for i in 0 .. lastContentIdx -> lines.[i] ] else []
        let trailingBlankCount = if lastContentIdx >= 0 then total - 1 - lastContentIdx else total
        let body0 = if isFolded then buildFoldedBody core else buildLiteralBody core
        let final =
            match chomp with
            | Strip -> body0
            | Clip -> body0 + (if total > 0 then "\n" else "")
            | Keep ->
                let extra = if lastContentIdx >= 0 then 1 else 0
                body0 + String.replicate (trailingBlankCount + extra) "\n"
        YamlValue.String final

    /// Parses one node — anchor, alias, tag, block sequence, block mapping, flow node, or
    /// plain/quoted scalar — starting at the cursor's current position, which must already be
    /// exactly at the node's first content column. `indent` is that column (0-based, i.e. the
    /// count of spaces before it), used as the sibling indent if this node turns out to be a
    /// block collection. `parentIndent` is the enclosing entry's own indent, used only to bound
    /// multi-line plain-scalar continuation (see `scanBlockPlainScalarMultiLine`) and, per the
    /// Phase 6 note above, as the base a nested value after `&anchor`/`!tag` must out-indent — it
    /// is unrelated to `indent` whenever this node is reached inline after a marker (`indent` is
    /// then the marker's content column, which can be well past `parentIndent`).
    let rec private parseNodeAt (cur: Cursor) (indent: int) (parentIndent: int) (state: ParseState) : YamlValue =
        parseNodeAtWithTag cur indent parentIndent state None

    /// `parseNodeAt`, but honouring a tag consumed by an enclosing `!tag` prefix (`None` when
    /// there wasn't one) — see `parseFlowNodeWithTag` for why a tagged scalar is resolved from
    /// its raw text rather than through `resolvePlainScalar` first.
    and private parseNodeAtWithTag
        (cur: Cursor)
        (indent: int)
        (parentIndent: int)
        (state: ParseState)
        (tagOpt: string option)
        : YamlValue =
        match cur.Peek() with
        | Some '&' ->
            cur.Advance()
            let name = scanAnchorName cur
            state.BeginAnchor name
            let value = parseValueAfterMarkerWithTag cur parentIndent state tagOpt
            state.EndAnchor(name, value)
            value
        | Some '*' ->
            cur.Advance()
            let name = scanAnchorName cur
            resolveAlias cur state name
        | Some '!' ->
            let tag = scanTag cur
            parseValueAfterMarkerWithTag cur parentIndent state (Some tag)
        | Some '-' when isDashIndicator cur -> parseBlockSequence cur indent state
        | Some '?' when isExplicitKeyIndicator cur -> parseBlockMapping cur indent state
        | Some ('{' | '[') -> parseFlowNode cur state
        | Some '\'' ->
            if looksLikeMappingEntry cur then parseBlockMapping cur indent state
            else applyTagToScalar cur tagOpt (parseSingleQuoted cur)
        | Some '"' ->
            if looksLikeMappingEntry cur then parseBlockMapping cur indent state
            else applyTagToScalar cur tagOpt (parseDoubleQuoted cur)
        | Some _ ->
            if looksLikeMappingEntry cur then
                parseBlockMapping cur indent state
            else
                let text = scanBlockPlainScalarMultiLine cur parentIndent
                match tagOpt with
                | Some tag -> applyCoreTag cur tag text
                | None -> YamlScalar.resolvePlainScalar text
        | None ->
            match tagOpt with
            | Some tag -> applyCoreTag cur tag ""
            | None -> YamlValue.Null

    /// Parses the value that follows a `-`, `:`, or `?` marker. `blockIndent` is the *marker's
    /// own entry's* indent (the dash's column for a sequence entry, the mapping's indent for a
    /// mapping value/explicit key) — not the marker's own column — since that is what a nested
    /// value on a following line must out-indent. If content follows the marker on the same
    /// line (after optional blanks), that content's own column becomes the anchor indent handed
    /// to `parseNodeAt`, which is what makes compact notation (`- key: value`, `- - a`) work: the
    /// nested collection's effective indent is anchored to the column right after the marker,
    /// not the marker's own column.
    and private parseValueAfterMarker (cur: Cursor) (blockIndent: int) (state: ParseState) : YamlValue =
        parseValueAfterMarkerWithTag cur blockIndent state None

    /// `parseValueAfterMarker`, threading a tag consumed by an enclosing `!tag` prefix through to
    /// wherever the value actually resolves (inline, on an indented following line, or as a block
    /// scalar).
    and private parseValueAfterMarkerWithTag
        (cur: Cursor)
        (blockIndent: int)
        (state: ParseState)
        (tagOpt: string option)
        : YamlValue =
        cur.SkipBlanks() |> ignore
        match cur.Peek() with
        | None ->
            match tagOpt with
            | Some tag -> applyCoreTag cur tag ""
            | None -> YamlValue.Null
        | Some ('\n' | '\r') -> parseIndentedValueWithTag cur blockIndent state tagOpt
        | Some '#' when isCommentStart cur -> parseIndentedValueWithTag cur blockIndent state tagOpt
        | Some ('|' | '>') -> applyTagToScalar cur tagOpt (parseBlockScalar cur blockIndent)
        | Some _ ->
            let inlineIndent = cur.Column - 1
            parseNodeAtWithTag cur inlineIndent blockIndent state tagOpt

    /// Looks for a value on a subsequent, more-indented line (the "key:\n  value" / "-\n  value"
    /// shape). `parentIndent` is the enclosing entry's own indent; a following content line is
    /// only accepted as this entry's value if its indent is strictly greater. If the next content
    /// line is at or below `parentIndent` (or there is none), the cursor is restored to before
    /// the blank-line skip and `Null` is returned — leaving that line for the caller's sibling
    /// loop to see fresh.
    and private parseIndentedValue (cur: Cursor) (parentIndent: int) (state: ParseState) : YamlValue =
        parseIndentedValueWithTag cur parentIndent state None

    /// `parseIndentedValue`, threading a tag through to wherever the value resolves.
    and private parseIndentedValueWithTag
        (cur: Cursor)
        (parentIndent: int)
        (state: ParseState)
        (tagOpt: string option)
        : YamlValue =
        let saved = cur.Position
        skipBlankLines cur
        if cur.IsEof then
            match tagOpt with
            | Some tag -> applyCoreTag cur tag ""
            | None -> YamlValue.Null
        else
            let curIndent, tabError = cur.CountIndent()
            match tabError with
            | Some off -> raise (tabIndentError cur off)
            | None -> ()
            if curIndent <= parentIndent then
                cur.Seek(saved)
                match tagOpt with
                | Some tag -> applyCoreTag cur tag ""
                | None -> YamlValue.Null
            else
                cur.Advance(curIndent)
                match cur.Peek() with
                | Some ('|' | '>') -> applyTagToScalar cur tagOpt (parseBlockScalar cur parentIndent)
                | _ -> parseNodeAtWithTag cur curIndent parentIndent state tagOpt

    /// Parses a block sequence — `- item` entries sharing the indent `indent` (the column of
    /// each `-`) — with the cursor already positioned at the first `-`. Arbitrary nesting and
    /// compact notation (`- - a`, `- key: value`) fall out of `parseValueAfterMarker`/
    /// `parseNodeAt`. Comments and blank lines between entries are transparently skipped.
    and private parseBlockSequence (cur: Cursor) (indent: int) (state: ParseState) : YamlValue =
        let items = ResizeArray<YamlValue>()
        let mutable continueLoop = true
        let mutable isFirst = true
        while continueLoop do
            let atEntry =
                if isFirst then
                    isFirst <- false
                    true
                else
                    skipBlankLines cur
                    if cur.IsEof then
                        false
                    else
                        let curIndent, tabError = cur.CountIndent()
                        match tabError with
                        | Some off -> raise (tabIndentError cur off)
                        | None -> ()
                        if curIndent < indent then
                            false
                        elif curIndent > indent then
                            raise (cur.Error "Inconsistent indentation in block sequence")
                        else
                            cur.Advance(curIndent)
                            true
            if not atEntry then
                continueLoop <- false
            else
                if not (isDashIndicator cur) then
                    raise (cur.Error "Expected '-' to start a block sequence entry")
                let dashIndent = cur.Column - 1
                cur.Advance() // consume '-'
                let value = parseValueAfterMarker cur dashIndent state
                items.Add(value)
        YamlValue.Sequence(items.ToArray())

    /// Parses a block mapping — `key: value` (and/or `? key` / `: value`) entries sharing the
    /// indent `indent` — with the cursor already positioned at the first entry. Both entry forms
    /// may be mixed freely within one mapping, matching YAML's grammar. Comments and blank lines
    /// between entries are transparently skipped. Merge keys (`<<: *base`) are expanded per
    /// `state.MergeKeysEnabled` once every entry has been collected — see `applyMergeKeys`.
    ///
    /// Explicit-key limitation: the value following `?` is parsed inline (flow node, quoted
    /// scalar, or single-line plain scalar) when content follows `?` on the same line; a key that
    /// is itself a multi-line block collection is only supported via the "empty `?`, key starts
    /// on a following more-indented line" path (`? \n  - a\n  - b\n: value`), which falls out of
    /// `parseIndentedValue`'s full recursion for free. A block collection starting inline right
    /// after `? ` on the same line is not supported — not valid YAML in the first place.
    and private parseBlockMapping (cur: Cursor) (indent: int) (state: ParseState) : YamlValue =
        let items = ResizeArray<YamlValue * YamlValue>()
        let mutable continueLoop = true
        let mutable isFirst = true
        while continueLoop do
            let atEntry =
                if isFirst then
                    isFirst <- false
                    true
                else
                    skipBlankLines cur
                    if cur.IsEof then
                        false
                    else
                        let curIndent, tabError = cur.CountIndent()
                        match tabError with
                        | Some off -> raise (tabIndentError cur off)
                        | None -> ()
                        if curIndent < indent then
                            false
                        elif curIndent > indent then
                            raise (cur.Error "Inconsistent indentation in block mapping")
                        else
                            cur.Advance(curIndent)
                            true
            if not atEntry then
                continueLoop <- false
            elif isExplicitKeyIndicator cur then
                cur.Advance() // consume '?'
                let rightAfterQ = cur.Position
                cur.SkipBlanks() |> ignore
                let key =
                    match cur.Peek() with
                    | None -> YamlValue.Null
                    | Some ('\n' | '\r') ->
                        cur.Seek(rightAfterQ)
                        parseValueAfterMarker cur indent state
                    | Some '#' when isCommentStart cur ->
                        cur.Seek(rightAfterQ)
                        parseValueAfterMarker cur indent state
                    | Some ('{' | '[') -> parseFlowNode cur state
                    | Some '\'' -> parseSingleQuoted cur
                    | Some '"' -> parseDoubleQuoted cur
                    | Some _ -> YamlScalar.resolvePlainScalar ((scanBlockPlainScalar cur).Trim())
                cur.SkipBlanks() |> ignore
                let value =
                    if cur.Peek() = Some ':' then
                        cur.Advance()
                        parseValueAfterMarker cur indent state
                    else
                        skipBlankLines cur
                        if cur.IsEof then
                            raise (cur.Error "Expected ':' to continue an explicit mapping key")
                        let colIndent, tabError = cur.CountIndent()
                        match tabError with
                        | Some off -> raise (tabIndentError cur off)
                        | None -> ()
                        if colIndent <> indent then
                            raise (cur.Error "Expected ':' aligned with '?' in an explicit mapping key")
                        cur.Advance(colIndent)
                        if cur.Peek() <> Some ':' then
                            raise (cur.Error "Expected ':' to continue an explicit mapping key")
                        cur.Advance()
                        parseValueAfterMarker cur indent state
                items.Add((key, value))
            elif looksLikeMappingEntry cur then
                let key = parseBlockMappingKey cur
                cur.SkipBlanks() |> ignore
                if cur.Peek() <> Some ':' then
                    raise (cur.Error "Expected ':' after mapping key")
                cur.Advance() // consume ':'
                let value = parseValueAfterMarker cur indent state
                items.Add((key, value))
            else
                raise (cur.Error "Expected a mapping entry")
        let finalItems = if state.MergeKeysEnabled then applyMergeKeys items else items.ToArray()
        YamlValue.Mapping finalItems

    /// Parses a whole document — the Phase 4 entry point. Tries block-style parsing first (real
    /// YAML is overwhelmingly block-style); a flow node, quoted scalar, or bare plain scalar as
    /// the entire document falls out of `parseNodeAt` naturally, matching the Phase 3 behaviour
    /// exactly for those shapes. Leading/trailing blank lines and comments are skipped; an empty
    /// (all-whitespace/comment) document resolves to `YamlValue.Null`. Trailing content after the
    /// node (other than whitespace/comments) is a parse error — full multi-document handling
    /// arrives in Phase 7.
    ///
    /// `disableMergeKeys` turns off `<<: *base` merge-key expansion for this parse (on by default
    /// — see `YamlValueParsing.Parse`).
    let parseDocument (text: string) (disableMergeKeys: bool) : YamlValue =
        let cur = Cursor(text)
        let state = ParseState(not disableMergeKeys)
        skipBlankLines cur
        if cur.IsEof then
            YamlValue.Null
        else
            let curIndent, tabError = cur.CountIndent()
            match tabError with
            | Some off -> raise (tabIndentError cur off)
            | None -> ()
            cur.Advance(curIndent)
            let value =
                match cur.Peek() with
                | Some ('|' | '>') -> parseBlockScalar cur -1
                | _ -> parseNodeAt cur curIndent -1 state
            skipBlankLines cur
            if not cur.IsEof then
                raise (cur.Error "Unexpected content after document")
            value

/// Adds the `Parse` entry point to `YamlValue`. Phase 3 only supports flow-style documents (a
/// single flow node, including a bare scalar) — see `YamlParser.parseDocument`. Full block-style
/// entry lands in Phase 4, at which point this augmentation switches to the combined
/// block/flow parser without changing its public signature. Phase 6 adds an optional
/// `disableMergeKeys` parameter without changing the single-argument `Parse(text)` shape.
[<AutoOpen>]
module YamlValueParsing =

    type YamlValue with
        /// Parses a single YAML document. Comments are discarded; use `YamlDocument.Parse` (once
        /// it exists — Phase 8) to keep them. Raises `YamlParseException` on invalid input.
        ///
        /// `disableMergeKeys` turns off `<<: *base` merge-key expansion — on by default (near-
        /// universal in docker-compose/GitLab CI); when disabled, a mapping entry whose key is
        /// literally `<<` is kept as an ordinary entry rather than being expanded into the
        /// enclosing mapping.
        static member Parse(text: string, ?disableMergeKeys: bool) : YamlValue =
            YamlParser.parseDocument text (defaultArg disableMergeKeys false)
