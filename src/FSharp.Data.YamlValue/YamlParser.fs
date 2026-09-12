namespace FSharp.Data

open System.IO
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

    /// Collects leading/trailing comments encountered during a comment-capturing parse
    /// (`YamlDocument.Parse` and friends), keyed by the `YamlPath` of the node they attach to.
    /// Entirely absent (`ParseState.Sink = None`) on the plain `YamlValue.Parse` path, so nothing
    /// here is allocated or touched beyond a cheap `Option` check at each potential capture point
    /// — see `captureTrailingComment`/`pushPathAndRecordLeading` below.
    type internal CommentSink() =
        let leading = Dictionary<YamlPath, ResizeArray<string>>()
        let trailing = Dictionary<YamlPath, string>()
        let docTrailing = ResizeArray<string>()

        /// Appends one or more already-trimmed leading comment lines to the node at `path`, in
        /// source order. A no-op when `lines` is empty.
        member _.AddLeading(path: YamlPath, lines: string list) : unit =
            if not (List.isEmpty lines) then
                match leading.TryGetValue path with
                | true, existing -> existing.AddRange lines
                | false, _ -> leading.[path] <- ResizeArray<string>(lines: string list)

        /// Records the single same-line trailing comment for the node at `path`.
        member _.SetTrailing(path: YamlPath, text: string) : unit = trailing.[path] <- text

        /// Records one comment line appearing after the document's last node.
        member _.AddDocumentTrailing(text: string) : unit = docTrailing.Add text

        /// Builds the final `Map<YamlPath, YamlNodeComments>` from everything collected so far.
        member _.ToCommentsMap() : Map<YamlPath, YamlNodeComments> =
            let paths = HashSet<YamlPath>()
            for kv in leading do
                paths.Add kv.Key |> ignore
            for kv in trailing do
                paths.Add kv.Key |> ignore
            paths
            |> Seq.map (fun p ->
                let lead =
                    match leading.TryGetValue p with
                    | true, v -> List.ofSeq v
                    | false, _ -> []
                let trail =
                    match trailing.TryGetValue p with
                    | true, v -> Some v
                    | false, _ -> None
                p, { Leading = lead; Trailing = trail })
            |> Map.ofSeq

        /// Comments appearing after the document's last node, in source order.
        member _.Trailing: string list = List.ofSeq docTrailing

    /// Per-document parsing state threaded through both the flow and block parsers: the anchor
    /// table (name -> the already-fully-parsed `YamlValue` it names — a shared reference to the
    /// same immutable array-backed value, not a copy, since every alias site just looks the value
    /// up and reuses it), the set of anchor names whose node is still being constructed (used to
    /// guard against a recursive alias — see `resolveAlias`), whether merge-key expansion is
    /// enabled for this parse, and (Phase 8) an optional comment sink plus the path of the node
    /// currently being parsed — both meaningful only when comment capture is enabled.
    type internal ParseState(mergeKeysEnabled: bool, ?commentSink: CommentSink) =
        let anchors = Dictionary<string, YamlValue>()
        let inFlight = HashSet<string>()
        let pathStack = ResizeArray<YamlPathStep>()
        let pendingLeading = ResizeArray<string>()

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

        /// `Some` only for a comment-capturing parse (`YamlDocument.Parse` and friends); `None`
        /// for the plain `YamlValue.Parse` path.
        member _.Sink = commentSink

        /// The path (root-first) of the node currently being parsed, built up by
        /// `PushPath`/`PopPath` as the block mapping/sequence parsers descend. Only meaningful
        /// (and only maintained) when `Sink` is `Some`.
        member _.CurrentPath: YamlPath = List.ofSeq pathStack

        /// Pushes one more step onto the current path — called right before parsing a mapping
        /// entry's value or a sequence entry's value.
        member _.PushPath(step: YamlPathStep) = pathStack.Add step

        /// Pops the step pushed by the matching `PushPath`, once that entry's value has been
        /// fully parsed.
        member _.PopPath() = pathStack.RemoveAt(pathStack.Count - 1)

        /// A single buffer, shared across every nesting level of this parse, holding comment
        /// lines collected by the most recent "skip to the next block entry" call that haven't
        /// yet been claimed as some entry's leading comments. It is deliberately *not* local to
        /// each `parseBlockMapping`/`parseBlockSequence` call: when a nested collection's own
        /// "is there another entry" check comes back false (a dedent back out to an enclosing
        /// collection, most commonly), whatever it collected along the way must remain available
        /// for the *enclosing* collection's next entry to claim — e.g. `parent:\n  child: 1\n#
        /// note\nsibling: 2` — the nested `child` mapping collects "note" while looking for a
        /// second entry of its own, finds none (dedent), and returns without touching the buffer;
        /// the outer mapping's loop then resumes, sees "sibling" as its next entry, and claims
        /// "note" as its leading comment. Only meaningful when `Sink` is `Some`; whatever is left
        /// unclaimed once the whole document has been parsed becomes `YamlDocument.Trailing`.
        member _.PendingLeading = pendingLeading

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
            raise (cur.Error("Recursive alias '*" + name + "' refers to an anchor still being constructed"))
        match state.TryGetAnchor name with
        | Some v -> v
        | None -> raise (cur.Error("Unknown anchor '*" + name + "'"))

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
            | _ -> raise (cur.Error("Invalid value '" + rawText + "' for tag !!bool"))
        | "!!int" ->
            match System.Decimal.TryParse(
                      rawText,
                      System.Globalization.NumberStyles.AllowLeadingSign,
                      System.Globalization.CultureInfo.InvariantCulture) with
            | true, d -> YamlValue.Number d
            | false, _ -> raise (cur.Error("Invalid value '" + rawText + "' for tag !!int"))
        | "!!float" ->
            match System.Double.TryParse(
                      rawText,
                      System.Globalization.NumberStyles.Float,
                      System.Globalization.CultureInfo.InvariantCulture) with
            | true, f -> YamlValue.Float f
            | false, _ -> raise (cur.Error("Invalid value '" + rawText + "' for tag !!float"))
        | "!!timestamp" ->
            match YamlScalar.resolvePlainScalar rawText with
            | YamlValue.Timestamp _ as t -> t
            | _ ->
                match System.DateTimeOffset.TryParse(
                          rawText,
                          System.Globalization.CultureInfo.InvariantCulture,
                          System.Globalization.DateTimeStyles.AssumeUniversal) with
                | true, dto -> YamlValue.Timestamp dto
                | false, _ -> raise (cur.Error("Invalid value '" + rawText + "' for tag !!timestamp"))
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
            raise (cur.Error("Expected a value, found '" + string c + "'"))
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
    ///
    /// `collect`, when `Some`, receives each full comment line's text (trimmed, `#` and
    /// surrounding whitespace stripped) in source order — Phase 8's hook for gathering leading
    /// comments ahead of the next block entry. Every call site that doesn't care passes `None`,
    /// which costs nothing beyond the `Option` match already needed to decide that.
    let rec private skipBlankLines (cur: Cursor) (collect: ResizeArray<string> option) : unit =
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
                    let text = cur.SkipComment()
                    (match collect with
                     | Some buf ->
                         let trimmed = text.Trim()
                         if trimmed <> "" then buf.Add trimmed
                     | None -> ())
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

    /// True when the cursor is positioned (at column 1) at the start of a `---`/`...` document
    /// marker line — i.e. exactly `marker` followed by whitespace, a genuine comment, a line
    /// break, or EOF (never a marker embedded in a longer token like `----` or `...abc`).
    /// Document markers are reserved at column 1 only — a `---`/`...` appearing anywhere else
    /// (inline after a mapping key, indented inside a block scalar, etc.) is ordinary content.
    let private matchesDocMarker (cur: Cursor) (marker: string) : bool =
        cur.Matches(marker)
        && (match cur.PeekAt(marker.Length) with
            | None -> true
            | Some (' ' | '\t' | '\n' | '\r' | '#') -> true
            | _ -> false)

    /// True at a `---` document-start marker, cursor at column 1.
    let private isDocumentStartMarkerAt (cur: Cursor) : bool =
        cur.Column = 1 && matchesDocMarker cur "---"

    /// True at a `...` document-end marker, cursor at column 1.
    let private isDocumentEndMarkerAt (cur: Cursor) : bool =
        cur.Column = 1 && matchesDocMarker cur "..."

    /// True at either document marker — used everywhere a block construct must stop rather than
    /// swallow the next document's boundary as more of its own content.
    let private isAnyDocMarkerAt (cur: Cursor) : bool =
        isDocumentStartMarkerAt cur || isDocumentEndMarkerAt cur

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

    /// (Phase 8) When comment capture is enabled, looks for a same-line trailing `#` comment
    /// right after the cursor's current position — skipping only blanks, never a line break —
    /// and records it against `path` in the sink. A no-op, leaving the cursor untouched, when:
    /// capture is disabled; the cursor is already at column 1 (meaning whatever was just parsed
    /// already consumed through its own trailing line break — a block scalar, a nested block
    /// collection, or EOF — so there is no "same line" left to look at); or there simply is no
    /// comment there.
    let private captureTrailingComment (cur: Cursor) (state: ParseState) (path: YamlPath) : unit =
        match state.Sink with
        | None -> ()
        | Some sink ->
            if cur.Column <> 1 then
                let saved = cur.Position
                cur.SkipBlanks() |> ignore
                match cur.Peek() with
                | Some '#' when isCommentStart cur ->
                    let text = (cur.SkipComment()).Trim()
                    if text <> "" then sink.SetTrailing(path, text)
                | _ -> cur.Seek(saved)

    /// (Phase 8) When comment capture is enabled, records `leading` (already-collected comment
    /// lines) against the child path `state.CurrentPath @ [step]` and pushes `step` onto the
    /// path stack for the duration of parsing that child's value. The caller must pair this with
    /// `popPathIfCapturing` once the value has been fully parsed. Returns the child path — `[]`
    /// (never used) when capture is disabled.
    let private pushPathAndRecordLeading (state: ParseState) (step: YamlPathStep) (leading: string list) : YamlPath =
        match state.Sink with
        | None -> []
        | Some sink ->
            let childPath = state.CurrentPath @ [ step ]
            if not (List.isEmpty leading) then sink.AddLeading(childPath, leading)
            state.PushPath step
            childPath

    /// Pops the path step pushed by `pushPathAndRecordLeading`, when comment capture is enabled.
    let private popPathIfCapturing (state: ParseState) : unit =
        match state.Sink with
        | Some _ -> state.PopPath()
        | None -> ()


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
                    if curIndent <= parentIndent || isAnyDocMarkerAt cur then
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
    ///
    /// `state` is used only for (Phase 8) comment capture: a trailing comment on the header line
    /// itself (`|  # comment`) describes the whole block scalar, not its content, so it is
    /// recorded — when capture is enabled — against `state.CurrentPath`, i.e. whatever entry this
    /// block scalar is the value of (the caller has already pushed that path before reaching
    /// here; see the `pushPathAndRecordLeading` call sites in `parseBlockMapping`/
    /// `parseBlockSequence`).
    let private parseBlockScalar (cur: Cursor) (parentIndent: int) (state: ParseState) : YamlValue =
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
         | Some '#' when isCommentStart cur ->
             match state.Sink with
             | None -> cur.SkipComment() |> ignore
             | Some sink ->
                 let text = (cur.SkipComment()).Trim()
                 if text <> "" then sink.SetTrailing(state.CurrentPath, text)
         | Some c -> raise (cur.Error("Invalid character '" + string c + "' in block scalar header")))
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
            if spaces = 0 && isAnyDocMarkerAt cur then
                cur.Seek(lineStart)
                terminated <- true
            else
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
        | Some ('|' | '>') -> applyTagToScalar cur tagOpt (parseBlockScalar cur blockIndent state)
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
        skipBlankLines cur None
        if cur.IsEof then
            match tagOpt with
            | Some tag -> applyCoreTag cur tag ""
            | None -> YamlValue.Null
        else
            let curIndent, tabError = cur.CountIndent()
            match tabError with
            | Some off -> raise (tabIndentError cur off)
            | None -> ()
            if curIndent <= parentIndent || isAnyDocMarkerAt cur then
                cur.Seek(saved)
                match tagOpt with
                | Some tag -> applyCoreTag cur tag ""
                | None -> YamlValue.Null
            else
                cur.Advance(curIndent)
                match cur.Peek() with
                | Some ('|' | '>') -> applyTagToScalar cur tagOpt (parseBlockScalar cur parentIndent state)
                | _ -> parseNodeAtWithTag cur curIndent parentIndent state tagOpt

    /// Parses a block sequence — `- item` entries sharing the indent `indent` (the column of
    /// each `-`) — with the cursor already positioned at the first `-`. Arbitrary nesting and
    /// compact notation (`- - a`, `- key: value`) fall out of `parseValueAfterMarker`/
    /// `parseNodeAt`. Comments and blank lines between entries are transparently skipped — when
    /// comment capture is enabled (Phase 8), a run of leading comments immediately above a
    /// (non-first) entry is attached to that entry's `Index i` path, and a same-line comment
    /// after an entry's value is attached there too as its trailing comment.
    and private parseBlockSequence (cur: Cursor) (indent: int) (state: ParseState) : YamlValue =
        let items = ResizeArray<YamlValue>()
        let mutable continueLoop = true
        let mutable isFirst = true
        let mutable idx = 0
        while continueLoop do
            let atEntry =
                if isFirst then
                    isFirst <- false
                    true
                else
                    let collect = match state.Sink with Some _ -> Some state.PendingLeading | None -> None
                    skipBlankLines cur collect
                    if cur.IsEof || isAnyDocMarkerAt cur then
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
                // Leave `state.PendingLeading` untouched — see its doc comment: whatever was
                // collected while looking for another entry here remains available for an
                // enclosing collection's next entry to claim, or becomes document trailing if
                // nothing ever does.
                continueLoop <- false
            else
                if not (isDashIndicator cur) then
                    raise (cur.Error "Expected '-' to start a block sequence entry")
                let leading = List.ofSeq state.PendingLeading
                state.PendingLeading.Clear()
                let dashIndent = cur.Column - 1
                cur.Advance() // consume '-'
                let childPath = pushPathAndRecordLeading state (Index idx) leading
                let value = parseValueAfterMarker cur dashIndent state
                popPathIfCapturing state
                captureTrailingComment cur state childPath
                items.Add(value)
                idx <- idx + 1
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
    ///
    /// When comment capture is enabled (Phase 8), a run of leading comments immediately above a
    /// (non-first) entry is attached to that entry's `Key k` path, and a same-line comment after
    /// an entry's value is attached there too as its trailing comment.
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
                    let collect = match state.Sink with Some _ -> Some state.PendingLeading | None -> None
                    skipBlankLines cur collect
                    if cur.IsEof || isAnyDocMarkerAt cur then
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
                // Leave `state.PendingLeading` untouched — see its doc comment: whatever was
                // collected while looking for another entry here remains available for an
                // enclosing collection's next entry to claim, or becomes document trailing if
                // nothing ever does.
                continueLoop <- false
            elif isExplicitKeyIndicator cur then
                let leading = List.ofSeq state.PendingLeading
                state.PendingLeading.Clear()
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
                let childPath = pushPathAndRecordLeading state (Key key) leading
                let value =
                    if cur.Peek() = Some ':' then
                        cur.Advance()
                        parseValueAfterMarker cur indent state
                    else
                        skipBlankLines cur None
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
                popPathIfCapturing state
                captureTrailingComment cur state childPath
                items.Add((key, value))
            elif looksLikeMappingEntry cur then
                let leading = List.ofSeq state.PendingLeading
                state.PendingLeading.Clear()
                let key = parseBlockMappingKey cur
                cur.SkipBlanks() |> ignore
                if cur.Peek() <> Some ':' then
                    raise (cur.Error "Expected ':' after mapping key")
                cur.Advance() // consume ':'
                let childPath = pushPathAndRecordLeading state (Key key) leading
                let value = parseValueAfterMarker cur indent state
                popPathIfCapturing state
                captureTrailingComment cur state childPath
                items.Add((key, value))
            else
                raise (cur.Error "Expected a mapping entry")
        let finalItems = if state.MergeKeysEnabled then applyMergeKeys items else items.ToArray()
        YamlValue.Mapping finalItems

    // -----------------------------------------------------------------------
    // Documents & directives (Phase 7) — `---`/`...` markers, `%YAML`/`%TAG` directives, and
    // `---`-separated multi-document streams.
    // -----------------------------------------------------------------------

    /// Parses one `%directive` line, cursor positioned at the leading `%` (column 1, guaranteed
    /// by the only caller, `parseDirectivesAndMarker`). Consumes through and including the line's
    /// trailing line break (or EOF) and returns `(name, value)` — `value` is the raw text after
    /// the name, blanks-trimmed, comment and line ending excluded.
    ///
    /// Validates the two directives the plan calls out by name: `%YAML` must be `MAJOR.MINOR`
    /// (e.g. `1.2`); `%TAG` must be `<handle> <prefix>` where `handle` is `!`, `!!`, or
    /// `!word!`. Both are recorded verbatim (not reinterpreted — see the module doc comment for
    /// what's deliberately out of scope). Any other directive name is recorded without further
    /// validation; a bare `%` with no name at all is malformed.
    let private parseDirectiveLine (cur: Cursor) : string * string =
        let startPos = cur.Position
        cur.Advance() // '%'
        let nameStart = cur.Offset
        while (match cur.Peek() with
               | Some c when not (System.Char.IsWhiteSpace c) -> true
               | _ -> false) do
            cur.Advance()
        let name = cur.Source.Substring(nameStart, cur.Offset - nameStart)
        cur.SkipBlanks() |> ignore
        let valueStart = cur.Offset
        while not (cur.IsEndOfLine()) && not (isCommentStart cur) do
            cur.Advance()
        let value = cur.Source.Substring(valueStart, cur.Offset - valueStart).TrimEnd(' ', '\t')
        let malformed (detail: string) : 'a =
            raise (cur.ErrorAt(detail, startPos.Line, startPos.Column))
        match name with
        | "" -> malformed "Malformed directive: expected a name after '%'"
        | "YAML" ->
            let parts = value.Split('.')
            let isDigits (s: string) = s.Length > 0 && s |> Seq.forall System.Char.IsDigit
            if parts.Length <> 2 || not (isDigits parts.[0]) || not (isDigits parts.[1]) then
                malformed ("Malformed %YAML directive — expected 'MAJOR.MINOR', got '" + value + "'")
        | "TAG" ->
            let parts = value.Split([| ' '; '\t' |], 2)
            let handleOk (h: string) =
                h = "!" || h = "!!" || (h.Length >= 2 && h.StartsWith "!" && h.EndsWith "!")
            if parts.Length <> 2 || not (handleOk parts.[0]) || parts.[1].Trim() = "" then
                malformed ("Malformed %TAG directive — expected '<handle> <prefix>', got '" + value + "'")
        | _ -> ()
        cur.SkipBlanksAndComment()
        cur.SkipLineBreak() |> ignore
        (name, value)

    /// Scans and consumes any `%` directives at the start of a document, then the document's
    /// `---` marker if one follows (mandatory when directives were seen; optional otherwise, per
    /// the plan: "`---` starts a new document (also legal as the very first line, optional)").
    /// Returns the directives in source order and whether a marker was consumed. Raises on a
    /// duplicate `%YAML` directive, a duplicate `%TAG` handle, or directives with no following
    /// `---` — directives "may only appear before a `---` that starts the document they apply
    /// to", so a document with directives but no marker is malformed.
    ///
    /// This runs *before* `parseDocumentBody`, so — when comment capture is enabled — any comment
    /// lines skipped here (whether before the very first directive, between directives, or before
    /// the `---` marker) are the earliest thing in the whole document; per the plan's "leading
    /// comments before the document's top node" case, they are attached to the root path `[]`.
    /// (A comment between directives/marker and directives themselves is a corner deliberately
    /// collapsed into this same root-leading bucket rather than modelled separately.)
    let private parseDirectivesAndMarker (cur: Cursor) (state: ParseState) : (string * string) list * bool =
        let pending = match state.Sink with Some _ -> Some(ResizeArray<string>()) | None -> None
        skipBlankLines cur pending
        let directives = ResizeArray<string * string>()
        let mutable yamlSeen = false
        let tagHandles = HashSet<string>()
        let mutable scanning = true
        while scanning do
            skipBlankLines cur pending
            if not cur.IsEof && cur.Column = 1 && cur.Peek() = Some '%' then
                let linePos = cur.Position
                let name, value = parseDirectiveLine cur
                match name with
                | "YAML" ->
                    if yamlSeen then
                        raise (
                            YamlParseException(
                                "Duplicate %YAML directive in the same document",
                                cur.Source,
                                linePos.Line,
                                linePos.Column
                            )
                        )
                    yamlSeen <- true
                | "TAG" ->
                    let handle = value.Split([| ' '; '\t' |], 2).[0]
                    if not (tagHandles.Add handle) then
                        raise (
                            YamlParseException(
                                "Duplicate %TAG directive for handle '" + handle + "'",
                                cur.Source,
                                linePos.Line,
                                linePos.Column
                            )
                        )
                | _ -> ()
                directives.Add((name, value))
            else
                scanning <- false
        skipBlankLines cur pending
        let hasMarker = not cur.IsEof && isDocumentStartMarkerAt cur
        if directives.Count > 0 && not hasMarker then
            raise (cur.Error "A '%' directive must be followed by a '---' document start marker")
        if hasMarker then
            cur.Advance(3)
            cur.SkipBlanks() |> ignore // leaves any same-line inline content (`--- foo`) for parseValueAfterMarker
        (match state.Sink, pending with
         | Some sink, Some buf when buf.Count > 0 -> sink.AddLeading([], List.ofSeq buf)
         | _ -> ())
        (List.ofSeq directives, hasMarker)

    /// Parses the document's root node. When a `---` marker was just consumed (`hadMarker`),
    /// this is exactly the "value that follows a marker" shape already handled by
    /// `parseValueAfterMarker` (inline same-line content, content on a following more-indented
    /// line, or a block scalar) — reused here with a synthetic `blockIndent` of `-1` so a nested
    /// value only needs to be indented past nothing at all. Without a marker, this is the
    /// original Phase 4 root-parsing logic: an explicit `CountIndent` + tab check on the true
    /// first line, since (unlike after a marker) leading whitespace there *is* indentation.
    ///
    /// Root-level leading comments (Phase 8) are captured earlier, by `parseDirectivesAndMarker`
    /// — since that always runs first and would otherwise silently consume them (looking for
    /// `%` directives / a `---` marker) before this function ever saw them. A leading `---`
    /// marker's own trailing comment handling is left to `parseValueAfterMarker`/
    /// `parseIndentedValueWithTag`, which do not collect — a deliberately-skipped corner for this
    /// phase.
    let private parseDocumentBody (cur: Cursor) (hadMarker: bool) (state: ParseState) : YamlValue =
        if hadMarker then
            parseValueAfterMarker cur -1 state
        else
            skipBlankLines cur None
            if cur.IsEof || isAnyDocMarkerAt cur then
                YamlValue.Null
            else
                let curIndent, tabError = cur.CountIndent()
                match tabError with
                | Some off -> raise (tabIndentError cur off)
                | None -> ()
                cur.Advance(curIndent)
                match cur.Peek() with
                | Some ('|' | '>') -> parseBlockScalar cur -1 state
                | _ -> parseNodeAt cur curIndent -1 state

    /// `skipBlankLines`, but — when comment capture is enabled — every comment line encountered is
    /// recorded straight into the sink's document-level `Trailing` list rather than being
    /// collected for later attribution to a node. Used everywhere a skip happens *after* the root
    /// node has been fully parsed, where there is no longer any "next node" a comment could be
    /// leading for.
    let private skipAndCollectTrailing (cur: Cursor) (state: ParseState) : unit =
        match state.Sink with
        | None -> skipBlankLines cur None
        | Some sink ->
            let buf = ResizeArray<string>()
            skipBlankLines cur (Some buf)
            for c in buf do
                sink.AddDocumentTrailing c

    /// Consumes an optional `...` document-end marker (and its trailing comment/line break), if
    /// one is present at the cursor's current position. A no-op otherwise — the `...` marker is
    /// always optional per the plan. Everything skipped here (blank/comment lines before an
    /// optional `...`, and a same-line comment after it) is, by construction, past the document's
    /// last node — so under comment capture it is recorded as document-level `Trailing`. Also
    /// flushes whatever is left in `state.PendingLeading` first — comments the innermost block
    /// collection collected while looking for one more entry that never materialised (see its
    /// doc comment); with the whole document now parsed, nothing will ever claim them as a
    /// node's leading comments, so — in source order, ahead of anything gathered here — they too
    /// become document-level `Trailing`.
    let private consumeDocumentEndCollecting (cur: Cursor) (state: ParseState) : unit =
        (match state.Sink with
         | Some sink when state.PendingLeading.Count > 0 ->
             for c in state.PendingLeading do
                 sink.AddDocumentTrailing c
             state.PendingLeading.Clear()
         | _ -> ())
        skipAndCollectTrailing cur state
        if not cur.IsEof && isDocumentEndMarkerAt cur then
            cur.Advance(3)
            (match state.Sink with
             | None -> cur.SkipBlanksAndComment()
             | Some sink ->
                 cur.SkipBlanks() |> ignore
                 match cur.Peek() with
                 | Some '#' when isCommentStart cur ->
                     let text = (cur.SkipComment()).Trim()
                     if text <> "" then sink.AddDocumentTrailing text
                 | _ -> ())
            cur.SkipLineBreak() |> ignore

    /// Parses a single document from `cur` (directives, optional `---`, the node itself, optional
    /// `...`), leaving the cursor positioned right after it — at EOF, at a following `---` marker
    /// that starts the next document in a stream, or (if neither) at whatever unexpected content
    /// follows, for the caller to turn into a parse error. Returns the node together with the
    /// directives captured for it, in source order. When comment capture is enabled, also records
    /// the root node's own same-line trailing comment (path `[]`) before anything past it is
    /// swept up as document-level `Trailing` by `consumeDocumentEndCollecting`.
    let private parseOneDocument (cur: Cursor) (state: ParseState) : YamlValue * (string * string) list =
        let directives, hadMarker = parseDirectivesAndMarker cur state
        let value = parseDocumentBody cur hadMarker state
        captureTrailingComment cur state []
        consumeDocumentEndCollecting cur state
        (value, directives)

    /// Parses a whole document — the Phase 4 entry point, extended in Phase 7 with directives and
    /// an optional leading `---` marker. Leading/trailing blank lines and comments are skipped; an
    /// empty (all-whitespace/comment) document resolves to `YamlValue.Null`. A second document
    /// (introduced by another `---`) or any other trailing content is a parse error — for a
    /// genuine multi-document stream, use `parseStreamWithDirectives`/`ParseMultiple`.
    ///
    /// `disableMergeKeys` turns off `<<: *base` merge-key expansion for this parse (on by default
    /// — see `YamlValueParsing.Parse`). `captureComments` (Phase 8) turns on the `ParseState`
    /// comment sink and path tracking used throughout this module — left `false` (the default
    /// route, via `parseDocumentWithDirectives`/`parseDocument`) for the plain `YamlValue.Parse`
    /// path, so that path never allocates a sink and every comment-capture call site above takes
    /// its cheap `None` branch. `YamlDocument.Parse` goes through `parseDocumentWithComments`
    /// instead, which passes `true`.
    let parseDocumentFull
        (text: string)
        (disableMergeKeys: bool)
        (captureComments: bool)
        : YamlValue * (string * string) list * Map<YamlPath, YamlNodeComments> * string list =
        let cur = Cursor(text)
        let sink = if captureComments then Some(CommentSink()) else None
        let state = ParseState(not disableMergeKeys, ?commentSink = sink)
        let value, directives = parseOneDocument cur state
        skipAndCollectTrailing cur state
        if not cur.IsEof then
            if isDocumentStartMarkerAt cur then
                raise (
                    cur.Error
                        "Multiple documents found in stream; use YamlValue.ParseMultiple or YamlDocument.ParseMultiple to parse a multi-document stream"
                )
            else
                raise (cur.Error "Unexpected content after document")
        let commentsMap = match sink with Some s -> s.ToCommentsMap() | None -> Map.empty
        let trailingList = match sink with Some s -> s.Trailing | None -> []
        (value, directives, commentsMap, trailingList)

    /// `parseDocumentFull` with comment capture off — the plain `YamlValue.Parse` entry point
    /// (via `parseDocument`, below).
    let parseDocumentWithDirectives (text: string) (disableMergeKeys: bool) : YamlValue * (string * string) list =
        let value, directives, _, _ = parseDocumentFull text disableMergeKeys false
        (value, directives)

    /// `parseDocumentWithDirectives`, discarding the directives — the plain `YamlValue.Parse`
    /// entry point.
    let parseDocument (text: string) (disableMergeKeys: bool) : YamlValue =
        parseDocumentWithDirectives text disableMergeKeys |> fst

    /// `parseDocumentFull` with comment capture on — the `YamlDocument.Parse` entry point.
    let parseDocumentWithComments
        (text: string)
        (disableMergeKeys: bool)
        : YamlValue * (string * string) list * Map<YamlPath, YamlNodeComments> * string list =
        parseDocumentFull text disableMergeKeys true

    /// Parses a `---`-separated stream of zero or more documents, each with its own directives
    /// (reset per document, per the plan), fresh comment sink (when `captureComments`), and
    /// optional `---`/`...` markers. An empty (all-whitespace/comment) stream yields zero
    /// documents. See `parseDocumentFull` for why `captureComments` is threaded rather than always
    /// on.
    let parseStreamFull
        (text: string)
        (disableMergeKeys: bool)
        (captureComments: bool)
        : (YamlValue * (string * string) list * Map<YamlPath, YamlNodeComments> * string list) list =
        let cur = Cursor(text)
        let results =
            ResizeArray<YamlValue * (string * string) list * Map<YamlPath, YamlNodeComments> * string list>()
        skipBlankLines cur None
        let mutable continueLoop = not cur.IsEof
        while continueLoop do
            let sink = if captureComments then Some(CommentSink()) else None
            let state = ParseState(not disableMergeKeys, ?commentSink = sink)
            let value, directives = parseOneDocument cur state
            skipAndCollectTrailing cur state
            let commentsMap = match sink with Some s -> s.ToCommentsMap() | None -> Map.empty
            let trailingList = match sink with Some s -> s.Trailing | None -> []
            results.Add((value, directives, commentsMap, trailingList))
            if cur.IsEof then
                continueLoop <- false
            elif isDocumentStartMarkerAt cur then
                () // next iteration's parseDirectivesAndMarker consumes it
            else
                raise (cur.Error "Unexpected content after document")
        List.ofSeq results

    /// `parseStreamFull` with comment capture off — the plain `YamlValue.ParseMultiple` entry
    /// point (via `parseStream`, below).
    let parseStreamWithDirectives
        (text: string)
        (disableMergeKeys: bool)
        : (YamlValue * (string * string) list) list =
        parseStreamFull text disableMergeKeys false
        |> List.map (fun (v, d, _, _) -> (v, d))

    /// `parseStreamWithDirectives`, discarding each document's directives — the plain
    /// `YamlValue.ParseMultiple` entry point.
    let parseStream (text: string) (disableMergeKeys: bool) : YamlValue list =
        parseStreamWithDirectives text disableMergeKeys |> List.map fst

    /// `parseStreamFull` with comment capture on — the `YamlDocument.ParseMultiple` entry point.
    let parseStreamWithComments
        (text: string)
        (disableMergeKeys: bool)
        : (YamlValue * (string * string) list * Map<YamlPath, YamlNodeComments> * string list) list =
        parseStreamFull text disableMergeKeys true

/// Adds the `Parse` entry point to `YamlValue`. Phase 3 only supports flow-style documents (a
/// single flow node, including a bare scalar) — see `YamlParser.parseDocument`. Full block-style
/// entry lands in Phase 4, at which point this augmentation switches to the combined
/// block/flow parser without changing its public signature. Phase 6 adds an optional
/// `disableMergeKeys` parameter without changing the single-argument `Parse(text)` shape.
[<AutoOpen>]
module YamlValueParsing =

    /// Shared `Load`/`AsyncLoad` plumbing for both `YamlValue` and `YamlDocument`: reads the text
    /// behind a `uri`, which may be an `http(s)` URL (fetched via `HttpClient`) or a local file
    /// path (read via `File.OpenRead`), with an optional explicit `Encoding` (UTF-8 detected from
    /// a BOM, or defaulted to UTF-8, when none is given).
    module internal YamlLoad =
        let private tryWebUri (uri: string) : System.Uri option =
            match System.Uri.TryCreate(uri, System.UriKind.Absolute) with
            | true, u when u.Scheme = System.Uri.UriSchemeHttp || u.Scheme = System.Uri.UriSchemeHttps -> Some u
            | _ -> None

        /// Asynchronously reads the full text at `uri` using `encoding` if given, else UTF-8 (with
        /// BOM detection for local files).
        let readTextAsync (uri: string) (encoding: Encoding option) : Async<string> =
            async {
                match tryWebUri uri with
                | Some u ->
                    use client = new System.Net.Http.HttpClient()
                    let! bytes = client.GetByteArrayAsync(u) |> Async.AwaitTask
                    let enc = defaultArg encoding Encoding.UTF8
                    return enc.GetString(bytes)
                | None ->
                    use fs = File.OpenRead(uri)
                    use reader =
                        match encoding with
                        | Some enc -> new StreamReader(fs, enc)
                        | None -> new StreamReader(fs, true)
                    return! reader.ReadToEndAsync() |> Async.AwaitTask
            }

        /// Synchronous convenience wrapper over `readTextAsync`, for the non-async `Load`
        /// overloads.
        let readText (uri: string) (encoding: Encoding option) : string =
            readTextAsync uri encoding |> Async.RunSynchronously

    type YamlValue with
        /// Parses a single YAML document. Comments are discarded; use `YamlDocument.Parse` to
        /// keep them (directives are captured there too; comment capture itself remains Phase
        /// 8's job). Raises `YamlParseException` on invalid input — including a second `---`-
        /// introduced document following the first; use `ParseMultiple` for a genuine
        /// multi-document stream.
        ///
        /// `disableMergeKeys` turns off `<<: *base` merge-key expansion — on by default (near-
        /// universal in docker-compose/GitLab CI); when disabled, a mapping entry whose key is
        /// literally `<<` is kept as an ordinary entry rather than being expanded into the
        /// enclosing mapping.
        static member Parse(text: string, ?disableMergeKeys: bool) : YamlValue =
            YamlParser.parseDocument text (defaultArg disableMergeKeys false)

        /// Attempts to parse a single YAML document; returns `None` on any parse failure
        /// (`YamlParseException`) rather than throwing.
        static member TryParse(text: string) : YamlValue option =
            try
                Some(YamlValue.Parse text)
            with :? YamlParseException ->
                None

        /// Parses a `---`-separated multi-document stream. Each document may carry its own
        /// `%YAML`/`%TAG` directives and optional `---`/`...` markers; an empty (all-whitespace/
        /// comment) stream yields zero documents.
        static member ParseMultiple(text: string) : YamlValue seq =
            YamlParser.parseStream text false :> seq<_>

        /// Loads a single document from a stream, read to end as text (using the stream's own
        /// default text decoding — see the `TextReader`/`uri` overloads for explicit encoding
        /// control).
        static member Load(stream: Stream) : YamlValue =
            use reader = new StreamReader(stream)
            YamlValue.Parse(reader.ReadToEnd())

        /// Loads a single document from a `TextReader`.
        static member Load(reader: TextReader) : YamlValue =
            YamlValue.Parse(reader.ReadToEnd())

        /// Loads a single document from a file path or `http(s)` URL. `encoding` defaults to
        /// UTF-8 (with BOM detection for local files).
        static member Load(uri: string, ?encoding: Encoding) : YamlValue =
            YamlValue.Parse(YamlLoad.readText uri encoding)

        /// Asynchronously loads a single document from a file path or `http(s)` URL. `encoding`
        /// defaults to UTF-8 (with BOM detection for local files).
        static member AsyncLoad(uri: string, ?encoding: Encoding) : Async<YamlValue> =
            async {
                let! text = YamlLoad.readTextAsync uri encoding
                return YamlValue.Parse text
            }

/// Adds the `Parse`/`TryParse`/`ParseMultiple`/`Load`/`AsyncLoad` family to `YamlDocument`,
/// mirroring `YamlValue`'s but also capturing `%YAML`/`%TAG` directives (in `Directives`, source
/// order, reset per document) and — since Phase 8 — comments (`Comments`, `Trailing`), via
/// `YamlParser.parseDocumentWithComments`/`parseStreamWithComments`.
[<AutoOpen>]
module YamlDocumentParsing =

    /// Wraps a parsed value + directives + comments + trailing comments into a `YamlDocument`.
    let private toDocument
        (value: YamlValue, directives: (string * string) list, comments: Map<YamlPath, YamlNodeComments>, trailing: string list)
        : YamlDocument =
        { Value = value
          Comments = comments
          Directives = directives
          Trailing = trailing }

    type YamlDocument with
        /// Parses a single document, preserving directives and comments. Leading comments above a
        /// node and a same-line trailing comment after it are attached to that node's `YamlPath`
        /// in `Comments`; comments after the document's last node land in `Trailing`. Raises
        /// `YamlParseException` on invalid input, including a second `---`-introduced document;
        /// use `ParseMultiple` for a genuine multi-document stream.
        static member Parse(text: string) : YamlDocument =
            YamlParser.parseDocumentWithComments text false |> toDocument

        /// Attempts to parse a single document; returns `None` on any parse failure rather than
        /// throwing.
        static member TryParse(text: string) : YamlDocument option =
            try
                Some(YamlDocument.Parse text)
            with :? YamlParseException ->
                None

        /// Parses a multi-document stream, preserving each document's own directives and comments.
        static member ParseMultiple(text: string) : YamlDocument seq =
            YamlParser.parseStreamWithComments text false
            |> Seq.map toDocument

        /// Comments attached to the node at `path`, if any — a convenience wrapper over
        /// `Comments`.
        member this.TryGetComments(path: YamlPath) : YamlNodeComments option = Map.tryFind path this.Comments

        /// Loads a single document from a stream.
        static member Load(stream: Stream) : YamlDocument =
            use reader = new StreamReader(stream)
            YamlDocument.Parse(reader.ReadToEnd())

        /// Loads a single document from a `TextReader`.
        static member Load(reader: TextReader) : YamlDocument =
            YamlDocument.Parse(reader.ReadToEnd())

        /// Loads a single document from a file path or `http(s)` URL. `encoding` defaults to
        /// UTF-8 (with BOM detection for local files).
        static member Load(uri: string, ?encoding: Encoding) : YamlDocument =
            YamlDocument.Parse(YamlValueParsing.YamlLoad.readText uri encoding)

        /// Asynchronously loads a single document from a file path or `http(s)` URL. `encoding`
        /// defaults to UTF-8 (with BOM detection for local files).
        static member AsyncLoad(uri: string, ?encoding: Encoding) : Async<YamlDocument> =
            async {
                let! text = YamlValueParsing.YamlLoad.readTextAsync uri encoding
                return YamlDocument.Parse text
            }
