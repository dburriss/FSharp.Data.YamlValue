namespace FSharp.Data

/// A char cursor over YAML source text, used by the (later) recursive-descent parser.
///
/// Conventions:
/// - `Line` and `Column` are both **1-based**.
// - The cursor advances over `\n`; a `\r\n` pair is treated as a single line break by
//   `Advance` (the `\r` is consumed silently, without incrementing column, immediately before
//   the `\n` handling runs) so callers never observe a bare `\r` as a "column".
/// - Indentation is measured in **spaces only** — a tab encountered where indentation is being
///   measured is illegal in YAML and is reported as such by `CountIndent`.
///
/// This module owns cursor-level concerns only (position tracking, lookahead, indent
/// measurement, comment-start detection). Grammar-level decisions belong to the parser.
module internal YamlReader =

    /// The source span of one parsed node, mirroring the shape of the final (expanded)
    /// `YamlValue` tree: a mapping's `Children` are its key and value nodes interleaved
    /// (`k0, v0, k1, v1, ...`, after merge-key expansion), a sequence's are its elements, a
    /// scalar has none. `Raw` is a scalar's source text (decoded content for quoted/block
    /// scalars), used to recover a timestamp's original text. An alias site (or a merged-in
    /// entry) has no `Children` of its own and instead points at the node it expands via
    /// `Origin`; every node below it is then positioned at the site's own `Span`.
    [<AllowNullLiteral>]
    type SpanNode(span: Span, children: SpanNode[], raw: string, origin: SpanNode) =
        member val Span = span with get, set
        member _.Children = children
        member _.Raw = raw
        member _.Origin = origin

    /// Collects `SpanNode`s while a position-tracking parse runs (`Cursor.Recorder` is `null` on
    /// the plain `Parse` path, so every recording call site costs one null check there). Nodes
    /// are added in completion order into a stack of frames: a collection pushes a frame before
    /// parsing its children and pops it into one node afterwards.
    [<AllowNullLiteral>]
    type SpanRecorder() =
        let frames = System.Collections.Generic.Stack<ResizeArray<SpanNode>>()
        do frames.Push(ResizeArray<SpanNode>())

        /// Number of nodes completed so far in the current frame — callers compare it before and
        /// after parsing a child to detect "nothing was consumed" (an implicit null).
        member _.Count = frames.Peek().Count

        member _.AddNode(node: SpanNode) = frames.Peek().Add node

        member _.AddLeaf(start: Pos, finish: Pos, raw: string) =
            frames.Peek().Add(SpanNode({ Start = start; End = finish }, [||], raw, null))

        member _.AddAlias(start: Pos, finish: Pos, origin: SpanNode) =
            frames.Peek().Add(SpanNode({ Start = start; End = finish }, [||], null, origin))

        member _.Last : SpanNode =
            let f = frames.Peek()
            if f.Count = 0 then null else f.[f.Count - 1]

        member _.PopLast() : SpanNode =
            let f = frames.Peek()
            let n = f.[f.Count - 1]
            f.RemoveAt(f.Count - 1)
            n

        member _.Begin() = frames.Push(ResizeArray<SpanNode>())

        /// Pops the current frame, returning the nodes completed in it.
        member _.PopFrame() : SpanNode[] = frames.Pop().ToArray()

        /// Completes a collection that began at `start` and ends at `finish`.
        member this.FinishAt(start: Pos, finish: Pos, children: SpanNode[]) =
            this.AddNode(SpanNode({ Start = start; End = finish }, children, null, null))

        /// Completes a block collection that began at `start`: its end is the end of its last
        /// child (block collections have no closing token).
        member this.Finish(start: Pos, children: SpanNode[]) =
            let finish = if children.Length = 0 then start else children.[children.Length - 1].Span.End
            this.FinishAt(start, finish, children)

        /// Moves the start of the node just completed back to `start` (to cover its anchor/tag).
        member this.ExtendLastStart(start: Pos) =
            let last = this.Last
            if not (isNull last) then last.Span <- { last.Span with Start = start }

        /// The document's root node, if any was recorded.
        member _.Root : SpanNode =
            let f = frames.Peek()
            if f.Count = 0 then null else f.[0]

    /// An immutable position marker that can be restored to (`Cursor.Seek`), cheap to copy.
    [<Struct>]
    type Position =
        { Offset: int
          Line: int
          Column: int }

    /// A mutable forward-scanning cursor over a source string.
    type Cursor(source: string) =
        let mutable offset = 0
        let mutable line = 1
        let mutable column = 1

        /// The full source text, for building `YamlParseException`s with a rendered snippet.
        member _.Source = source

        /// Current 0-based offset into `Source`.
        member _.Offset = offset

        /// Current 1-based line number.
        member _.Line = line

        /// Current 1-based column number.
        member _.Column = column

        /// The current position, capturable and restorable via `Seek`.
        member _.Position : Position = { Offset = offset; Line = line; Column = column }

        /// The current position as a public `Pos`.
        member _.Pos : Pos = { Offset = offset; Line = line; Column = column }

        /// Records node spans when non-null (set only by the `*WithPositions` entry points).
        member val Recorder : SpanRecorder = null with get, set

        /// Skips a leading byte-order mark without counting it as a column.
        member _.SkipBom() =
            if source.Length > 0 && source.[0] = '\uFEFF' then offset <- 1

        /// Restores a previously captured position.
        member _.Seek(pos: Position) =
            offset <- pos.Offset
            line <- pos.Line
            column <- pos.Column

        /// True when the cursor has consumed all of `Source`.
        member _.IsEof = offset >= source.Length

        /// True when the cursor is at or past `Source.Length - n`, i.e. fewer than `n` chars
        /// remain from the current position (inclusive).
        member _.IsEofAt(n: int) = offset + n > source.Length

        /// The char at the current position, or `None` at EOF.
        member _.Peek() : char option =
            if offset < source.Length then Some source.[offset] else None

        /// The char `n` positions ahead of the current one (0 = current), or `None` if that
        /// position is past EOF.
        member _.PeekAt(n: int) : char option =
            let i = offset + n
            if i >= 0 && i < source.Length then Some source.[i] else None

        /// A substring of up to `n` chars starting at the current position (shorter at EOF).
        member _.PeekString(n: int) : string =
            let len = min n (source.Length - offset)
            if len <= 0 then "" else source.Substring(offset, len)

        /// Whether the current position starts with `text` (case-sensitive).
        member this.Matches(text: string) : bool =
            let len = text.Length
            not (this.IsEofAt len)
            && String.length text = len
            && YamlPortable.regionEquals source offset text len

        // Advances by one char, correctly updating line/column. `\r\n` and lone `\n` both count
        // as a single line break; a lone `\r` (old Mac line endings) also counts as one. Does
        /// nothing at EOF.
        member _.Advance() : unit =
            if offset < source.Length then
                let c = source.[offset]
                if c = '\r' then
                    offset <- offset + 1
                    // Swallow a following '\n' as part of the same line break.
                    if offset < source.Length && source.[offset] = '\n' then
                        offset <- offset + 1
                    line <- line + 1
                    column <- 1
                elif c = '\n' then
                    offset <- offset + 1
                    line <- line + 1
                    column <- 1
                else
                    offset <- offset + 1
                    column <- column + 1

        /// Advances by `n` chars (calls `Advance` `n` times; no-ops past EOF).
        member this.Advance(n: int) : unit =
            for _ in 1 .. n do
                this.Advance()

        /// Consumes the current char and returns it, or `None` at EOF.
        member this.Read() : char option =
            match this.Peek() with
            | Some c ->
                this.Advance()
                Some c
            | None -> None

        // True when the char at the current position is `' '` or `'\t'`.
        member this.IsBlank() =
            match this.Peek() with
            | Some ' ' | Some '\t' -> true
            | _ -> false

        // True when the char at the current position is a line break (`\n` or `\r`).
        member this.IsLineBreak() =
            match this.Peek() with
            | Some '\n' | Some '\r' -> true
            | _ -> false

        /// True at EOF or at a line break — i.e. nothing more to read on the current line.
        member this.IsEndOfLine() = this.IsEof || this.IsLineBreak()

        /// Skips spaces and tabs (not line breaks). Returns the number of chars skipped.
        member this.SkipBlanks() : int =
            let start = offset
            while this.IsBlank() do
                this.Advance()
            offset - start

        // Skips exactly one line break (`\r\n`, `\n`, or `\r`) if the cursor is positioned at
        /// one. Returns whether a line break was skipped.
        member this.SkipLineBreak() : bool =
            if this.IsLineBreak() then
                this.Advance()
                true
            else
                false

        /// Counts leading spaces from the current position, without consuming them. Tabs
        /// encountered before any non-blank, non-tab char are illegal as indentation in YAML;
        /// `tabError` is set to the offset of the first illegal tab found, if any (relative to
        /// the current position), so the caller can raise a `YamlParseException` with proper
        /// line/col. A line consisting only of blanks (or ending in EOF/newline) still counts
        /// all its spaces.
        member _.CountIndent() : int * int option =
            let mutable i = offset
            let mutable spaces = 0
            let mutable tabError = None
            let mutable stop = false
            while not stop && i < source.Length do
                match source.[i] with
                | ' ' ->
                    spaces <- spaces + 1
                    i <- i + 1
                | '\t' ->
                    if tabError.IsNone then
                        tabError <- Some (i - offset)
                    stop <- true
                | _ -> stop <- true
            (spaces, tabError)

        /// Whether the indentation at the current position is at least `indent` spaces (used to
        /// decide whether a block construct continues at the expected nesting level).
        member this.IndentAtLeast(indent: int) : bool =
            let spaces, _ = this.CountIndent()
            spaces >= indent

        /// True when the cursor is positioned at the start of a comment: a `#` that is either at
        /// the very start of a line, or preceded by whitespace. A `#` embedded directly in a
        /// scalar (e.g. `abc#def`) is NOT a comment and this returns false for it.
        ///
        /// `isAfterWhitespaceOrStart` should be true when the immediately preceding char (if any,
        /// on the current line) was a space/tab, or the cursor is at column 1 — the parser is
        /// expected to track this as it consumes scalars, since the reader itself does not look
        /// backwards past the current position.
        member this.IsAtCommentStart(isAfterWhitespaceOrStart: bool) : bool =
            isAfterWhitespaceOrStart
            && (match this.Peek() with
                | Some '#' -> true
                | _ -> false)

        /// Skips a comment starting at the current position (assumed to already be positioned at
        /// the `#`) through end of line, NOT consuming the terminating line break itself. Returns
        /// the comment text (without the leading `#`), for reuse by later comment-capture phases.
        /// For Phase 2 this is just a mechanism — no comment-text side table exists yet.
        member this.SkipComment() : string =
            let start = offset
            while not (this.IsEndOfLine()) do
                this.Advance()
            source.Substring(start, offset - start).TrimStart('#')

        /// Skips blanks, then a comment if one starts there (per `IsAtCommentStart`, with
        /// `isAfterWhitespaceOrStart` implied true since blanks were just skipped or the cursor
        /// was already at line start), stopping at end of line either way. Convenience for
        /// skipping trailing whitespace + comment on a line.
        member this.SkipBlanksAndComment() : unit =
            this.SkipBlanks() |> ignore
            match this.Peek() with
            | Some '#' -> this.SkipComment() |> ignore
            | _ -> ()

        /// Builds a `YamlParseException` positioned at the cursor's current line/column.
        member this.Error(message: string) : YamlParseException =
            YamlParseException(message, source, line, column)

        /// Builds a `YamlParseException` positioned at an explicit line/column (e.g. for an
        /// error discovered via lookahead, such as an illegal indentation tab).
        member _.ErrorAt(message: string, atLine: int, atColumn: int) : YamlParseException =
            YamlParseException(message, source, atLine, atColumn)
