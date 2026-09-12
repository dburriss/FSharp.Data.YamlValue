namespace FSharp.Data

open System
open System.Text

/// Represents a YAML node. The type is `[<RequireQualifiedAccess>]`, so cases are written
/// `YamlValue.String`, `YamlValue.Mapping`, and so on.
///
/// `Mapping` holds `YamlValue` keys (not `string`) because YAML permits any node as a mapping
/// key. Use `YamlExtensions.Properties`/`GetProperty`/the `?` operator for the common
/// string-keyed case, and `YamlExtensions.Entries` to see every pair including non-string keys.
[<RequireQualifiedAccess>]
[<StructuredFormatDisplay("{_Print}")>]
type YamlValue =
    /// A YAML string. Quoted and block scalars always land here, regardless of their content.
    | String of string
    /// A YAML number stored as a decimal — integers, and floats that fit the decimal range
    /// exactly. Kept separate from `Float` so exact values aren't silently widened.
    | Number of decimal
    /// A YAML number stored as a float, for values that do not fit in `decimal`, and for
    /// `.inf`, `-.inf` and `.nan`.
    | Float of float
    /// A YAML boolean. Core schema only: `true`/`false` and their capitalised forms — never
    /// `yes`/`no`/`on`/`off` (the "Norway problem").
    | Boolean of bool
    /// A YAML timestamp — an ISO-8601 date or date-time. Dates without a zone are read as UTC.
    | Timestamp of DateTimeOffset
    /// A YAML mapping, as an array of key-value pairs. Keys are `YamlValue` because YAML
    /// permits any node as a key; order is preserved.
    | Mapping of properties: (YamlValue * YamlValue)[]
    /// A YAML sequence of nodes.
    | Sequence of elements: YamlValue[]
    /// A YAML null — `null`, `~`, or an empty value.
    | Null

    // IsString, IsNumber, IsFloat, IsBoolean, IsTimestamp, IsMapping, IsSequence and IsNull are
    // provided automatically by the union's default augmentation (one bool property per case).

    /// A short, readable debug representation used by `StructuredFormatDisplay`
    /// (shown by F# Interactive and debugger visualizers).
    member private this._Print =
        let rec print (v: YamlValue) =
            match v with
            | YamlValue.String s -> "\"" + s + "\""
            | YamlValue.Number n -> string n
            | YamlValue.Float f -> string f
            | YamlValue.Boolean b -> if b then "true" else "false"
            | YamlValue.Timestamp t -> t.ToString("o")
            | YamlValue.Null -> "null"
            | YamlValue.Sequence elements ->
                let items = elements |> Array.map print |> String.concat "; "
                "[" + items + "]"
            | YamlValue.Mapping properties ->
                let items =
                    properties
                    |> Array.map (fun (k, v) -> print k + ": " + print v)
                    |> String.concat "; "
                "{" + items + "}"
        print this

/// A single step in a path from the document root down to a node — either a mapping key or a
/// sequence index.
type YamlPathStep =
    /// A mapping key. The key is a `YamlValue` because YAML permits any node as a key.
    | Key of YamlValue
    /// A sequence index.
    | Index of int

/// A path addressing a node from the document root, root-first. The root node itself has the
/// empty path.
type YamlPath = YamlPathStep list

/// Comments attached to a single node: whole-line comments appearing above it, and an optional
/// trailing comment on the same line after it.
type YamlNodeComments =
    { /// Whole-line comments above the node, in source order.
      Leading: string list
      /// A comment on the same line, after the node, if any.
      Trailing: string option }

/// A parsed document together with everything `YamlValue` deliberately leaves out — comments
/// and directives. Comments are held in a side table keyed by path rather than embedded in the
/// node tree, so `YamlValue` stays clean to pattern match on.
type YamlDocument =
    { /// The document's root node.
      Value: YamlValue
      /// Comments attached to nodes, keyed by their path from the root.
      Comments: Map<YamlPath, YamlNodeComments>
      /// `%YAML` and `%TAG` directives, in source order.
      Directives: (string * string) list
      /// Comments appearing after the last node in the document.
      Trailing: string list }

/// A `[<Flags>]` enumeration controlling YAML serialization.
[<Flags>]
type YamlSaveOptions =
    /// Block style, 2-space indentation, comments preserved. The default.
    | None = 0
    /// Omits the newlines and indentation that are purely cosmetic.
    | DisableFormatting = 1
    /// Emits flow style throughout — `{a: 1, b: [2, 3]}` — rather than block style.
    | Flow = 2
    /// Drops comments when serializing a `YamlDocument`. No effect on `YamlValue`.
    | SuppressComments = 4
    /// Emits a leading `---` and a trailing `...`.
    | ExplicitDocumentMarkers = 8

/// Renders the source line at `line`/`column` with a `^` caret underneath pointing at the
/// offending column, for use in `YamlParseException` messages.
module internal YamlParseSnippet =

    /// Builds a two-line snippet: the offending source line, then a caret line pointing at
    /// `column` (1-based). `line` is also 1-based. Falls back to an empty snippet if `line` is
    /// out of range for `source`.
    let render (source: string) (line: int) (column: int) : string =
        let lines =
            source.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n')

        if line >= 1 && line <= lines.Length then
            let sourceLine = lines.[line - 1]
            let caretPos = max 0 (column - 1)
            let sb = StringBuilder()
            sb.Append(sourceLine) |> ignore
            sb.Append('\n') |> ignore
            sb.Append(' ', caretPos) |> ignore
            sb.Append('^') |> ignore
            sb.ToString()
        else
            ""

/// Raised by `Parse`, `ParseMultiple`, `Load` and `AsyncLoad` on malformed YAML input.
type YamlParseException(message: string, line: int, column: int, snippet: string) =
    inherit Exception(
        message + " at line " + string line + ", column " + string column + "\n" + snippet)

    /// 1-based line number where parsing failed.
    member _.Line = line

    /// 1-based column number where parsing failed.
    member _.Column = column

    /// The offending source line with a caret marking the column.
    member _.Snippet = snippet

    /// Constructs a `YamlParseException` from the full source text, computing the caret snippet
    /// for the given 1-based line/column.
    new(message: string, source: string, line: int, column: int) =
        YamlParseException(message, line, column, YamlParseSnippet.render source line column)
