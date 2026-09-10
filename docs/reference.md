# API Reference

- [`YamlValue`](#yamlvalue) — the document type
- [`YamlDocument`](#yamldocument) — value plus comments and directives
- [`YamlSaveOptions`](#yamlsaveoptions)
- [`YamlParseException`](#yamlparseexception)
- [`YamlPathStep` / `YamlNodeComments`](#comment-types)
- [`YamlExtensions`](#yamlextensions) — the `As*` accessor family
- [`YamlExtensions` module](#yamlextensions-module) — the `?` operator
- [`YamlBuilders`](#yamlbuilders) — `SetProperty`/`SetPath`/`RemoveProperty`/`RemovePath`, and `YamlPath`

---

## YamlValue

**Namespace:** `FSharp.Data`
**Assembly:** `FSharp.Data.Yaml.dll`

Represents a YAML node. The type is `[<RequireQualifiedAccess>]`, so cases are written
`YamlValue.String`, `YamlValue.Mapping`, and so on.

### Union cases

| Case | Signature | Description |
|---|---|---|
| `String` | `String of string` | A YAML string. Quoted and block scalars always land here. |
| `Number` | `Number of decimal` | A YAML number stored as a decimal — integers, and floats that fit the decimal range exactly. |
| `Float` | `Float of float` | A YAML number stored as a float, for values that do not fit in decimal, and for `.inf`, `-.inf` and `.nan`. |
| `Boolean` | `Boolean of bool` | A YAML boolean. Core schema only: `true`/`false` and their capitalised forms, never `yes`/`no`/`on`/`off`. |
| `Timestamp` | `Timestamp of DateTimeOffset` | A YAML timestamp — an ISO-8601 date or date-time. Dates without a zone are read as UTC. |
| `Mapping` | `Mapping of properties: (YamlValue * YamlValue)[]` | A YAML mapping, as an array of key-value pairs. Keys are `YamlValue` because YAML permits any node as a key; order is preserved. |
| `Sequence` | `Sequence of elements: YamlValue[]` | A YAML sequence of nodes. |
| `Null` | `Null` | A YAML null — `null`, `~`, or an empty value. |

### Instance members

| Member | Signature | Description |
|---|---|---|
| `IsString` | `this.IsString : bool` | Whether the value is a string. |
| `IsNumber` | `this.IsNumber : bool` | Whether the value is a number. |
| `IsFloat` | `this.IsFloat : bool` | Whether the value is a float. |
| `IsBoolean` | `this.IsBoolean : bool` | Whether the value is a boolean. |
| `IsTimestamp` | `this.IsTimestamp : bool` | Whether the value is a timestamp. |
| `IsMapping` | `this.IsMapping : bool` | Whether the value is a mapping. |
| `IsSequence` | `this.IsSequence : bool` | Whether the value is a sequence. |
| `IsNull` | `this.IsNull : bool` | Whether the value is null. |
| `ToString` | `ToString(?indentationSpaces: int) : string` | Serializes to block-style YAML. Indentation defaults to 2 spaces. |
| `ToString` | `ToString(saveOptions: YamlSaveOptions, ?indentationSpaces: int) : string` | Serializes with the given formatting options. |
| `WriteTo` | `WriteTo(w: TextWriter, saveOptions: YamlSaveOptions, ?indentationSpaces: int) : unit` | Serializes to a `TextWriter`. |

### Static members

| Member | Signature | Description |
|---|---|---|
| `Parse` | `Parse(text: string) : YamlValue` | Parses a single YAML document. Comments are discarded; use `YamlDocument.Parse` to keep them. Raises `YamlParseException` on invalid input. |
| `TryParse` | `TryParse(text: string) : YamlValue option` | Attempts to parse a YAML document; returns `None` on failure. |
| `ParseMultiple` | `ParseMultiple(text: string) : YamlValue seq` | Parses a `---`-separated multi-document stream. |
| `Load` | `Load(stream: Stream) : YamlValue` | Loads a document from a stream. |
| `Load` | `Load(reader: TextReader) : YamlValue` | Loads a document from a text reader. |
| `Load` | `Load(uri: string, ?encoding: Encoding) : YamlValue` | Loads a document from a file path or URL. Encoding defaults to UTF-8. |
| `AsyncLoad` | `AsyncLoad(uri: string, ?encoding: Encoding) : Async<YamlValue>` | Asynchronously loads a document from a file path or URL. |

---

## YamlDocument

**Namespace:** `FSharp.Data`

A parsed document together with everything `YamlValue` deliberately leaves out — comments and
directives. Comments are held in a side table keyed by path rather than embedded in the node
tree, so `YamlValue` stays clean to pattern match on.

### Record fields

| Field | Type | Description |
|---|---|---|
| `Value` | `YamlValue` | The document's root node. |
| `Comments` | `Map<YamlPath, YamlNodeComments>` | Comments attached to nodes, keyed by their path from the root. |
| `Directives` | `(string * string) list` | `%YAML` and `%TAG` directives, in source order. |
| `Trailing` | `string list` | Comments appearing after the last node in the document. |

### Instance members

| Member | Signature | Description |
|---|---|---|
| `ToString` | `ToString(?indentationSpaces: int) : string` | Serializes the document, re-emitting comments and directives. |
| `ToString` | `ToString(saveOptions: YamlSaveOptions, ?indentationSpaces: int) : string` | Serializes with the given options. Pass `SuppressComments` to drop comments. |
| `WriteTo` | `WriteTo(w: TextWriter, saveOptions: YamlSaveOptions, ?indentationSpaces: int) : unit` | Serializes to a `TextWriter`. |
| `TryGetComments` | `TryGetComments(path: YamlPath) : YamlNodeComments option` | Comments attached to the node at the given path, if any. |

### Static members

| Member | Signature | Description |
|---|---|---|
| `Parse` | `Parse(text: string) : YamlDocument` | Parses a single document, preserving comments and directives. |
| `TryParse` | `TryParse(text: string) : YamlDocument option` | Attempts to parse; returns `None` on failure. |
| `ParseMultiple` | `ParseMultiple(text: string) : YamlDocument seq` | Parses a multi-document stream, preserving comments. |
| `Load` | `Load(stream: Stream) : YamlDocument` | Loads from a stream. |
| `Load` | `Load(reader: TextReader) : YamlDocument` | Loads from a text reader. |
| `Load` | `Load(uri: string, ?encoding: Encoding) : YamlDocument` | Loads from a file path or URL. |
| `AsyncLoad` | `AsyncLoad(uri: string, ?encoding: Encoding) : Async<YamlDocument>` | Asynchronously loads from a file path or URL. |

---

## YamlSaveOptions

**Namespace:** `FSharp.Data`

A `[<Flags>]` enumeration controlling serialization.

| Case | Value | Description |
|---|---|---|
| `None` | `0` | Block style, 2-space indentation, comments preserved. The default. |
| `DisableFormatting` | `1` | Omits the newlines and indentation that are purely cosmetic. |
| `Flow` | `2` | Emits flow style throughout — `{a: 1, b: [2, 3]}` — rather than block style. |
| `SuppressComments` | `4` | Drops comments when serializing a `YamlDocument`. No effect on `YamlValue`. |
| `ExplicitDocumentMarkers` | `8` | Emits a leading `---` and a trailing `...`. |

---

## YamlParseException

**Namespace:** `FSharp.Data`
**Inherits:** `System.Exception`

Raised by `Parse`, `ParseMultiple`, `Load` and `AsyncLoad` on malformed input.

| Member | Signature | Description |
|---|---|---|
| `Line` | `this.Line : int` | 1-based line number where parsing failed. |
| `Column` | `this.Column : int` | 1-based column number where parsing failed. |
| `Snippet` | `this.Snippet : string` | The offending source line with a caret marking the column. |
| `Message` | `this.Message : string` | Description of the failure, including position and snippet. |

---

## Comment types

**Namespace:** `FSharp.Data`

Supporting types for `YamlDocument.Comments`.

```fsharp
type YamlPathStep =
    | Key   of YamlValue
    | Index of int

type YamlPath = YamlPathStep list        // root-first

type YamlNodeComments =
    { Leading:  string list               // whole-line comments above the node
      Trailing: string option }           // comment on the same line, after the node
```

A path addresses a node from the document root: `[Key (YamlValue.String "services"); Key (YamlValue.String "web"); Index 0]`.
The root node itself has the empty path.

---

## YamlExtensions

**Namespace:** `FSharp.Data`

Extension methods for working with `YamlValue` in a less safe, but more convenient way. Each
raises an exception when the value is not of the expected shape — they are the "I know what this
document looks like" half of the API.

```fsharp
open FSharp.Data.YamlExtensions
```

### Static members

| Member | Signature | Description |
|---|---|---|
| `AsBoolean` | `AsBoolean(x: YamlValue) : bool` | Gets the boolean value of an element, assuming it is a boolean. |
| `AsInteger` | `AsInteger(x: YamlValue, ?cultureInfo: CultureInfo) : int` | Gets a number as an integer, assuming the value fits in an integer. |
| `AsInteger64` | `AsInteger64(x: YamlValue, ?cultureInfo: CultureInfo) : int64` | Gets a number as a 64-bit integer, assuming the value fits. |
| `AsDecimal` | `AsDecimal(x: YamlValue, ?cultureInfo: CultureInfo) : decimal` | Gets a number as a decimal, assuming the value fits in a decimal. |
| `AsFloat` | `AsFloat(x: YamlValue, ?cultureInfo: CultureInfo, ?missingValues: string[]) : float` | Gets a number as a float, assuming the value is convertible. Values listed in `missingValues` become `nan`. |
| `AsString` | `AsString(x: YamlValue, ?cultureInfo: CultureInfo) : string` | Gets the string value of an element, assuming the value is a scalar. Returns the empty string for `YamlValue.Null`. |
| `AsDateTime` | `AsDateTime(x: YamlValue, ?cultureInfo: CultureInfo) : DateTime` | Gets the datetime value of an element — either a `Timestamp`, or a string containing a well-formed ISO date. |
| `AsDateTimeOffset` | `AsDateTimeOffset(x: YamlValue, ?cultureInfo: CultureInfo) : DateTimeOffset` | Gets the datetime offset of an element — either a `Timestamp`, or a string containing an ISO date-time with offset. |
| `AsTimeSpan` | `AsTimeSpan(x: YamlValue, ?cultureInfo: CultureInfo) : TimeSpan` | Gets the timespan value of an element, assuming it is a string containing a well-formed time span. |
| `AsGuid` | `AsGuid(x: YamlValue) : Guid` | Gets the guid value of an element, assuming it is a guid. |
| `AsArray` | `AsArray(x: YamlValue) : YamlValue[]` | Gets all the elements of a value. Returns an empty array if the value is not a sequence. |
| `AsSequence` | `AsSequence(x: YamlValue) : YamlValue[]` | Alias for `AsArray`, using YAML's terminology. |
| `AsMapping` | `AsMapping(x: YamlValue) : (YamlValue * YamlValue)[]` | Gets all key-value pairs. Returns an empty array if the value is not a mapping. |
| `GetProperty` | `GetProperty(x: YamlValue, propertyName: string) : YamlValue` | Gets a property of a mapping. Fails if the value is not a mapping, or if the property is not present. |
| `TryGetProperty` | `TryGetProperty(x: YamlValue, propertyName: string) : YamlValue option` | Tries to get a property. Returns `None` if the value is not a mapping, or the property is not present. |
| `Properties` | `Properties(x: YamlValue) : (string * YamlValue)[]` | Gets the string-keyed properties of a mapping. Non-string keys are skipped — use `Entries` for all of them. |
| `Entries` | `Entries(x: YamlValue) : (YamlValue * YamlValue)[]` | Gets every key-value pair of a mapping, including non-string keys. |
| `Item` | `Item(x: YamlValue, propertyName: string) : YamlValue` (inline) | Assuming the value is a mapping, gets the value with the given name. |
| `Item` | `Item(x: YamlValue, index: int) : YamlValue` (inline) | Assuming the value is a sequence, gets the value at the given index. |
| `GetEnumerator` | `GetEnumerator(x: YamlValue) : IEnumerator` (inline) | Gets all the elements of a value, assuming it is a sequence. Enables `for x in value do`. |
| `InnerText` | `InnerText(x: YamlValue) : string` | Gets the inner text of an element — scalars as strings, mappings and sequences as their concatenated contents. |

---

## YamlExtensions module

**Namespace:** `FSharp.Data`

### Functions and values

| Function | Signature | Description |
|---|---|---|
| `( ? )` | `yamlObject: YamlValue -> propertyName: string -> YamlValue` | Gets a property of a YAML mapping. |

```fsharp
info?name?first
```

### Type extensions

| Extension | Signature | Description |
|---|---|---|
| `Properties` | `this.Properties : unit -> (string * YamlValue)[]` | The string-keyed properties of a mapping, as name-value pairs. |
| `Entries` | `this.Entries : unit -> (YamlValue * YamlValue)[]` | Every key-value pair of a mapping, including non-string keys. |

---

## YamlBuilders

**Namespace:** `FSharp.Data.Yaml.Builders`

`YamlValue` has no write API of its own — like `JsonValue`, it's immutable. `YamlBuilders` is an
opt-in construction/editing API, kept in its own namespace and file (`YamlBuilders.fs`) separate
from the read-only `YamlExtensions`. Every member returns a **new** `YamlValue`; none mutate `this`.

```fsharp
open FSharp.Data.Yaml.Builders
```

### `YamlValue` extension members

| Member | Signature | Description |
|---|---|---|
| `SetProperty` | `SetProperty(name: string, value: YamlValue) : YamlValue` | Sets a string-keyed property, replacing it if present or appending it otherwise. `YamlValue.Null` auto-vivifies into a single-entry mapping; any other non-mapping value raises. |
| `RemoveProperty` | `RemoveProperty(name: string) : YamlValue` | Removes a string-keyed property. No-op if the value is not a mapping, or the property is not present. |
| `SetPath` | `SetPath(path: string, value: YamlValue, ?overwriteScalars: bool) : YamlValue` | Sets the value addressed by the string-path DSL (e.g. `"services.web.ports[0]"`). |
| `SetPath` | `SetPath(path: YamlPath, value: YamlValue, ?overwriteScalars: bool) : YamlValue` | Same, addressed by a `YamlPath` built via the fluent builder or `YamlPath.OfSteps`. |
| `RemovePath` | `RemovePath(path: string) : YamlValue` | Removes the value addressed by the string-path DSL. No-op if any step of the path doesn't exist. |
| `RemovePath` | `RemovePath(path: YamlPath) : YamlValue` | Same, addressed by a `YamlPath`. |
| `TryGetPath` | `TryGetPath(path: string) : YamlValue option` | Reads the value addressed by the string-path DSL. `None` if any step doesn't exist (missing key, out-of-range index, or descending into a scalar/`Null`). |
| `TryGetPath` | `TryGetPath(path: YamlPath) : YamlValue option` | Same, addressed by a `YamlPath`. |
| `GetPath` | `GetPath(path: string) : YamlValue` | Same as `TryGetPath`, but raises if the path is not found. |
| `GetPath` | `GetPath(path: YamlPath) : YamlValue` | Same, addressed by a `YamlPath`. |

**`SetPath` semantics.** Missing intermediate mappings/sequences are auto-vivified (`mkdir -p`
style); a sequence is padded with `YamlValue.Null` when the index is beyond its current length.
A scalar (non-`Null`) node encountered where a container is needed to keep descending is
overwritten by default — pass `overwriteScalars = false` to raise instead.

**`RemovePath` semantics.** A no-op (returns the value unchanged) if any step of the path is
missing. Removing a sequence index splices the element out (later elements shift down) rather
than leaving a `Null` hole. Emptied parent mappings/sequences are left in place, not pruned.

**`TryGetPath`/`GetPath` semantics.** `TryGetPath` returns `None` (and `GetPath` raises) the
moment any step of the path can't be followed — a missing mapping key, an out-of-range sequence
index, or a scalar/`Null` node where a container is needed to keep descending. The empty path
(`""` or `YamlPath.Root`) returns the value itself.

### The string-path DSL

Dot-separated segments address mapping keys; `[n]` addresses a sequence index; a segment
containing a literal `.`, `[`, or `]` is bracket-quoted, e.g. `services["a.b"].image`
(`\"` and `\\` are unescaped inside the quotes). The DSL only expresses string-keyed mapping
steps — non-string keys need the `YamlPath` builder's `Key(YamlValue)` overload. A malformed
path string raises `System.FormatException`.

### `YamlPath`

**Namespace:** `FSharp.Data.Yaml.Builders`

A fluent builder for a `YamlPathStep list` (the same root-first path type used by
`YamlDocument.Comments`), aimed at callers — particularly from C# — for whom constructing
`Key`/`Index` union cases directly is unidiomatic. Prefer the string-path DSL for the common
case; reach for `YamlPath` for non-string keys or paths assembled programmatically.

| Member | Signature | Description |
|---|---|---|
| `Root` | `static YamlPath.Root : YamlPath` | The empty path — the document root itself. |
| `OfSteps` | `static YamlPath.OfSteps(steps: YamlPathStep list) : YamlPath` | Wraps a raw root-first step list. |
| `Key` | `Key(name: string) : YamlPath` | Appends a string-keyed mapping step. |
| `Key` | `Key(key: YamlValue) : YamlPath` | Appends a mapping step with an arbitrary (non-string) key. |
| `Index` | `Index(index: int) : YamlPath` | Appends a sequence-index step. |
| `Steps` | `this.Steps : YamlPathStep list` | The underlying root-first step list. |

```fsharp
let path = YamlPath.Root.Key("services").Key("web").Index(0)
doc.SetPath(path, YamlValue.String "nginx:1.27")
```

```csharp
var path = YamlPath.Root.Key("services").Key("web").Index(0);
var updated = doc.SetPath(path, YamlValue.NewString("nginx:1.27"));
```
