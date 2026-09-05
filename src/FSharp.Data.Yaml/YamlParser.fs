namespace FSharp.Data

open System.Text

/// Recursive-descent parser. Phase 3 implemented **flow-style** grammar — `{...}` mappings,
/// `[...]` sequences, quoted and plain flow scalars, arbitrarily nested. Phase 4 adds
/// **block-style** (indentation-based) mappings and sequences, compact notation, and explicit
/// `? key` / `: value` pairs, calling into the flow parser wherever a flow node may appear (a
/// block mapping value or sequence entry can always be a flow collection). `YamlValue.Parse` now
/// tries block-style parsing first — real YAML is overwhelmingly block-style — falling back
/// naturally to flow parsing wherever the grammar allows a flow node.
module internal YamlParser =

    open YamlReader

    /// Characters that delimit a flow scalar/collection: comma and the four bracket/brace
    /// chars. `:` is handled separately since whether it terminates a plain scalar depends on
    /// what follows it (see `scanFlowPlainScalar`).
    let private isFlowIndicator (c: char) =
        c = ',' || c = '[' || c = ']' || c = '{' || c = '}'

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

    /// Parses a single-quoted flow scalar, cursor positioned at the opening `'`. Always yields
    /// `YamlValue.String` — no scalar resolution applies to quoted scalars.
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
        YamlValue.String(YamlScalar.unescapeSingleQuoted raw)

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
        try
            YamlValue.String(YamlScalar.unescapeDoubleQuoted raw)
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

    /// Parses one flow node — mapping, sequence, quoted scalar, or plain scalar — starting at
    /// the cursor's current position (surrounding whitespace/comments must already be skipped by
    /// the caller, matching the convention used throughout this module: every `parse*` function
    /// leaves the cursor immediately after what it consumed, and every caller calls
    /// `skipFlowWhitespace` before looking at the next token).
    let rec parseFlowNode (cur: Cursor) : YamlValue =
        match cur.Peek() with
        | Some '{' -> parseFlowMapping cur
        | Some '[' -> parseFlowSequence cur
        | Some '\'' -> parseSingleQuoted cur
        | Some '"' -> parseDoubleQuoted cur
        | Some ':' -> raise (cur.Error "Expected a value, found ':'")
        | Some c when isFlowIndicator c ->
            raise (cur.Error(sprintf "Expected a value, found '%c'" c))
        | Some _ ->
            let text = scanFlowPlainScalar cur
            YamlScalar.resolvePlainScalar text
        | None -> raise (cur.Error "Unexpected end of input, expected a value")

    /// Parses one flow-sequence entry. Supports YAML's "compact" single-pair flow mapping
    /// shorthand inside a sequence (`[a: b, c]`, equivalent to `[{a: b}, c]`) — after parsing the
    /// first node, a following `:` (not already consumed, since `scanFlowPlainScalar` stops
    /// before a terminating colon) turns the entry into a one-pair mapping.
    and private parseFlowSequenceEntry (cur: Cursor) : YamlValue =
        let node = parseFlowNode cur
        skipFlowWhitespace cur
        match cur.Peek() with
        | Some ':' ->
            cur.Advance()
            skipFlowWhitespace cur
            let value =
                match cur.Peek() with
                | Some (',' | ']') -> YamlValue.Null
                | _ -> parseFlowNode cur
            YamlValue.Mapping [| (node, value) |]
        | _ -> node

    /// Parses a flow mapping, cursor positioned at the opening `{`. Supports trailing commas
    /// (`{a: 1,}`) and key-only entries with an implicit `Null` value (`{a}` / `{a,}`).
    and private parseFlowMapping (cur: Cursor) : YamlValue =
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
                let key = parseFlowNode cur
                skipFlowWhitespace cur
                let value =
                    match cur.Peek() with
                    | Some ':' ->
                        cur.Advance()
                        skipFlowWhitespace cur
                        match cur.Peek() with
                        | Some (',' | '}') -> YamlValue.Null
                        | _ -> parseFlowNode cur
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
        YamlValue.Mapping(items.ToArray())

    /// Parses a flow sequence, cursor positioned at the opening `[`. Supports trailing commas
    /// (`[1, 2,]`) and the compact flow-pair shorthand via `parseFlowSequenceEntry`.
    and private parseFlowSequence (cur: Cursor) : YamlValue =
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
                let elem = parseFlowSequenceEntry cur
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

    /// Parses one node — block sequence, block mapping, flow node, or plain/quoted scalar —
    /// starting at the cursor's current position, which must already be exactly at the node's
    /// first content column. `indent` is that column (0-based, i.e. the count of spaces before
    /// it), used as the sibling indent if this node turns out to be a block collection.
    let rec private parseNodeAt (cur: Cursor) (indent: int) : YamlValue =
        match cur.Peek() with
        | Some '-' when isDashIndicator cur -> parseBlockSequence cur indent
        | Some '?' when isExplicitKeyIndicator cur -> parseBlockMapping cur indent
        | Some ('{' | '[') -> parseFlowNode cur
        | Some '\'' ->
            if looksLikeMappingEntry cur then parseBlockMapping cur indent
            else parseSingleQuoted cur
        | Some '"' ->
            if looksLikeMappingEntry cur then parseBlockMapping cur indent
            else parseDoubleQuoted cur
        | Some _ ->
            if looksLikeMappingEntry cur then
                parseBlockMapping cur indent
            else
                let text = scanBlockPlainScalar cur
                YamlScalar.resolvePlainScalar (text.Trim())
        | None -> YamlValue.Null

    /// Parses the value that follows a `-`, `:`, or `?` marker. `blockIndent` is the *marker's
    /// own entry's* indent (the dash's column for a sequence entry, the mapping's indent for a
    /// mapping value/explicit key) — not the marker's own column — since that is what a nested
    /// value on a following line must out-indent. If content follows the marker on the same
    /// line (after optional blanks), that content's own column becomes the anchor indent handed
    /// to `parseNodeAt`, which is what makes compact notation (`- key: value`, `- - a`) work: the
    /// nested collection's effective indent is anchored to the column right after the marker,
    /// not the marker's own column.
    and private parseValueAfterMarker (cur: Cursor) (blockIndent: int) : YamlValue =
        cur.SkipBlanks() |> ignore
        match cur.Peek() with
        | None -> YamlValue.Null
        | Some ('\n' | '\r') -> parseIndentedValue cur blockIndent
        | Some '#' when isCommentStart cur -> parseIndentedValue cur blockIndent
        | Some _ ->
            let inlineIndent = cur.Column - 1
            parseNodeAt cur inlineIndent

    /// Looks for a value on a subsequent, more-indented line (the "key:\n  value" / "-\n  value"
    /// shape). `parentIndent` is the enclosing entry's own indent; a following content line is
    /// only accepted as this entry's value if its indent is strictly greater. If the next content
    /// line is at or below `parentIndent` (or there is none), the cursor is restored to before
    /// the blank-line skip and `Null` is returned — leaving that line for the caller's sibling
    /// loop to see fresh.
    and private parseIndentedValue (cur: Cursor) (parentIndent: int) : YamlValue =
        let saved = cur.Position
        skipBlankLines cur
        if cur.IsEof then
            YamlValue.Null
        else
            let curIndent, tabError = cur.CountIndent()
            match tabError with
            | Some off -> raise (tabIndentError cur off)
            | None -> ()
            if curIndent <= parentIndent then
                cur.Seek(saved)
                YamlValue.Null
            else
                cur.Advance(curIndent)
                parseNodeAt cur curIndent

    /// Parses a block sequence — `- item` entries sharing the indent `indent` (the column of
    /// each `-`) — with the cursor already positioned at the first `-`. Arbitrary nesting and
    /// compact notation (`- - a`, `- key: value`) fall out of `parseValueAfterMarker`/
    /// `parseNodeAt`. Comments and blank lines between entries are transparently skipped.
    and private parseBlockSequence (cur: Cursor) (indent: int) : YamlValue =
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
                let value = parseValueAfterMarker cur dashIndent
                items.Add(value)
        YamlValue.Sequence(items.ToArray())

    /// Parses a block mapping — `key: value` (and/or `? key` / `: value`) entries sharing the
    /// indent `indent` — with the cursor already positioned at the first entry. Both entry forms
    /// may be mixed freely within one mapping, matching YAML's grammar. Comments and blank lines
    /// between entries are transparently skipped.
    ///
    /// Explicit-key limitation: the value following `?` is parsed inline (flow node, quoted
    /// scalar, or single-line plain scalar) when content follows `?` on the same line; a key that
    /// is itself a multi-line block collection is only supported via the "empty `?`, key starts
    /// on a following more-indented line" path (`? \n  - a\n  - b\n: value`), which falls out of
    /// `parseIndentedValue`'s full recursion for free. A block collection starting inline right
    /// after `? ` on the same line is not supported — not valid YAML in the first place.
    and private parseBlockMapping (cur: Cursor) (indent: int) : YamlValue =
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
                        parseValueAfterMarker cur indent
                    | Some '#' when isCommentStart cur ->
                        cur.Seek(rightAfterQ)
                        parseValueAfterMarker cur indent
                    | Some ('{' | '[') -> parseFlowNode cur
                    | Some '\'' -> parseSingleQuoted cur
                    | Some '"' -> parseDoubleQuoted cur
                    | Some _ -> YamlScalar.resolvePlainScalar ((scanBlockPlainScalar cur).Trim())
                cur.SkipBlanks() |> ignore
                let value =
                    if cur.Peek() = Some ':' then
                        cur.Advance()
                        parseValueAfterMarker cur indent
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
                        parseValueAfterMarker cur indent
                items.Add((key, value))
            elif looksLikeMappingEntry cur then
                let key = parseBlockMappingKey cur
                cur.SkipBlanks() |> ignore
                if cur.Peek() <> Some ':' then
                    raise (cur.Error "Expected ':' after mapping key")
                cur.Advance() // consume ':'
                let value = parseValueAfterMarker cur indent
                items.Add((key, value))
            else
                raise (cur.Error "Expected a mapping entry")
        YamlValue.Mapping(items.ToArray())

    /// Parses a whole document — the Phase 4 entry point. Tries block-style parsing first (real
    /// YAML is overwhelmingly block-style); a flow node, quoted scalar, or bare plain scalar as
    /// the entire document falls out of `parseNodeAt` naturally, matching the Phase 3 behaviour
    /// exactly for those shapes. Leading/trailing blank lines and comments are skipped; an empty
    /// (all-whitespace/comment) document resolves to `YamlValue.Null`. Trailing content after the
    /// node (other than whitespace/comments) is a parse error — full multi-document handling
    /// arrives in Phase 7.
    let parseDocument (text: string) : YamlValue =
        let cur = Cursor(text)
        skipBlankLines cur
        if cur.IsEof then
            YamlValue.Null
        else
            let curIndent, tabError = cur.CountIndent()
            match tabError with
            | Some off -> raise (tabIndentError cur off)
            | None -> ()
            cur.Advance(curIndent)
            let value = parseNodeAt cur curIndent
            skipBlankLines cur
            if not cur.IsEof then
                raise (cur.Error "Unexpected content after document")
            value

/// Adds the `Parse` entry point to `YamlValue`. Phase 3 only supports flow-style documents (a
/// single flow node, including a bare scalar) — see `YamlParser.parseDocument`. Full block-style
/// entry lands in Phase 4, at which point this augmentation switches to the combined
/// block/flow parser without changing its public signature.
[<AutoOpen>]
module YamlValueParsing =

    type YamlValue with
        /// Parses a single YAML document. Comments are discarded; use `YamlDocument.Parse` (once
        /// it exists — Phase 8) to keep them. Raises `YamlParseException` on invalid input.
        static member Parse(text: string) : YamlValue = YamlParser.parseDocument text
