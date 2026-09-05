namespace FSharp.Data

open System.Text

/// Recursive-descent parser. Phase 3 implements **flow-style** grammar only — `{...}` mappings,
/// `[...]` sequences, quoted and plain flow scalars, arbitrarily nested. Block-style
/// (indentation-based) mappings/sequences land in Phase 4; until then, `YamlValue.Parse` only
/// accepts a document whose entire content is a single flow node (including a bare scalar, since
/// JSON permits top-level scalars too).
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

    /// Parses a whole document consisting of a single flow node — the Phase 3 entry point.
    /// Leading/trailing blank lines and comments are skipped; an empty (all-whitespace/comment)
    /// document resolves to `YamlValue.Null`, matching YAML's "empty document" semantics.
    /// Trailing content after the node (other than whitespace/comments) is a parse error — full
    /// multi-document handling arrives in Phase 7.
    let parseDocument (text: string) : YamlValue =
        let cur = Cursor(text)
        skipFlowWhitespace cur
        if cur.IsEof then
            YamlValue.Null
        else
            let value = parseFlowNode cur
            skipFlowWhitespace cur
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
