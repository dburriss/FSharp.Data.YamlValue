namespace FSharp.Data

open System.Collections.Generic
open FSharp.Data.YamlReader

module internal YamlPositions =

    /// RFC 6901 escaping of one reference token.
    let escapeToken (token: string) : string =
        if token.IndexOf('~') >= 0 || token.IndexOf('/') >= 0 then
            token.Replace("~", "~0").Replace("/", "~1")
        else
            token

    /// Follows alias origins to the span node that actually holds the expanded node's structure.
    let rec private resolve (n: SpanNode) : SpanNode =
        if isNull n then null elif isNull n.Origin then n else resolve n.Origin

    /// Walks the final value tree alongside its span tree, filling both tables. A node that
    /// came from an alias or merge (its span node has an `Origin`) and everything beneath it is
    /// positioned at the alias/merge site (`over`). Duplicate mapping keys: only the first owns
    /// the pointer. Non-string keys (and everything under them) get no entry.
    let rec private walk
        (spans: Dictionary<string, Span>)
        (v: YamlValue)
        (node: SpanNode)
        (over: Span option)
        (parent: Span)
        (ptr: string)
        : unit =
        let over =
            if not (isNull node) && not (isNull node.Origin) && over.IsNone then Some node.Span else over
        let span =
            match over with
            | Some s -> s
            | None -> if isNull node then parent else node.Span
        let r = resolve node
        spans.[ptr] <- span
        match v with
        | YamlValue.Mapping pairs ->
            let positional = not (isNull r) && r.Children.Length = pairs.Length * 2
            let seen = HashSet<string>()
            for j in 0 .. pairs.Length - 1 do
                match pairs.[j] with
                | YamlValue.String key, value when seen.Add key ->
                    let child = if positional then r.Children.[j * 2 + 1] else null
                    walk spans value child over span (ptr + "/" + escapeToken key)
                | _ -> ()
        | YamlValue.Sequence elems ->
            let positional = not (isNull r) && r.Children.Length = elems.Length
            for i in 0 .. elems.Length - 1 do
                let child = if positional then r.Children.[i] else null
                walk spans elems.[i] child over span (ptr + "/" + string i)
        | _ -> ()

    let toSpans (value: YamlValue) (root: SpanNode) : Map<string, Span> =
        let spans = Dictionary<string, Span>()
        let origin: Pos = { Line = 1; Column = 1; Offset = 0 }
        walk spans value root None { Start = origin; End = origin } ""
        spans |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq

    /// Converts a raised `YamlParseException` into the value form, recovering the bare message
    /// and the 0-based offset of its line/column.
    let toError (text: string) (e: YamlParseException) : YamlParseError =
        let suffix = " at line " + string e.Line + ", column " + string e.Column + "\n"
        let cut = e.Message.LastIndexOf suffix
        let message = if cut >= 0 then e.Message.Substring(0, cut) else e.Message
        let mutable i = if text.Length > 0 && text.[0] = '﻿' then 1 else 0
        let mutable line = 1
        while line < e.Line && i < text.Length do
            if text.[i] = '\r' then
                i <- i + 1
                if i < text.Length && text.[i] = '\n' then i <- i + 1
                line <- line + 1
            elif text.[i] = '\n' then
                i <- i + 1
                line <- line + 1
            else
                i <- i + 1
        { Message = message
          Pos = ({ Line = e.Line; Column = e.Column; Offset = min text.Length (i + e.Column - 1) }: Pos) }

/// Position-tracking parse entry points for `YamlValue`. These are additions beside `Parse`, which
/// is untouched: it never tracks anything and its result and cost are the same as before.
[<AutoOpen>]
module YamlPositionParsing =

    type YamlValue with
        /// Parses a single YAML document like `YamlValue.Parse` and also returns the source
        /// `Span` of every node, keyed by its JSON Pointer (RFC 6901) over the expanded tree:
        /// `""` is the root, `/a/0/b` a nested node, `~0` stands for `~` and `~1` for `/`, and
        /// the key `""` becomes `/`. There is one entry per node, scalars included.
        ///
        /// - `Line`/`Column` are 1-based, `Column` counts UTF-16 units, `Offset` is a 0-based
        ///   index into `text`, `End` is exclusive, and `\r\n` is one line break. A leading BOM
        ///   is skipped and is not a column (note: plain `Parse` does not skip a BOM).
        /// - A node's span covers its anchor and tag if it has them (`&a !!str x`), a quoted
        ///   scalar's quotes, and a block scalar from its `|`/`>` indicator to the end of its last
        ///   content line. An omitted (null) value such as `a:` or a bare `-` is a zero-width
        ///   span just after its `:`/`-`.
        /// - Duplicate mapping keys: the first occurrence owns the pointer (as with
        ///   `TryGetProperty`); later duplicates and what is under them get no entry.
        /// - Only string keys get pointer entries; a node under a non-string key has none.
        /// - Aliases: an alias site owns the whole expanded subtree. The node at the alias and
        ///   every node below it map to the span of the `*name` token, not to the anchor
        ///   definition, so every pointer into the expanded tree has a span.
        /// - Merge keys: entries merged in by `<<` map (key and everything below) to the span of
        ///   the `<<` entry's value (the `*alias`, or the whole `[*a, *b]` / inline mapping). The
        ///   `<<` key itself has no pointer once it has been expanded; with
        ///   `disableMergeKeys = true` it is an ordinary `/<<` entry.
        static member ParseWithPositions(text: string, ?disableMergeKeys: bool) : YamlValue * Map<string, Span> =
            let value, root = YamlParser.parseDocumentWithSpans text (defaultArg disableMergeKeys false)
            value, YamlPositions.toSpans value root

        /// Like `ParseWithPositions` but returns `Error` (with the failure position) instead of
        /// raising `YamlParseException`.
        static member TryParseWithPositions(text: string, ?disableMergeKeys: bool)
            : Result<YamlValue * Map<string, Span>, YamlParseError> =
            try
                Ok(YamlValue.ParseWithPositions(text, ?disableMergeKeys = disableMergeKeys))
            with :? YamlParseException as e ->
                Error(YamlPositions.toError text e)

        /// `ParseMultiple` with a span table per document. Offsets and lines are relative to the
        /// whole stream.
        static member ParseMultipleWithPositions(text: string) : (YamlValue * Map<string, Span>) seq =
            YamlParser.parseStreamWithSpans text false
            |> List.map (fun (v, n) -> v, YamlPositions.toSpans v n)
            :> seq<_>
