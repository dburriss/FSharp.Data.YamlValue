/// Opt-in construction/editing API for `YamlValue`, per `plans/yamlvalue-builders.md`. `YamlValue`
/// is immutable, same as `FSharp.Data.JsonValue` — every member here returns a *new* `YamlValue`
/// rather than mutating in place. Kept out of `YamlExtensions.fs` deliberately: that file is the
/// read-only, pattern-match-friendly half of the API, this is the "build/edit a document" half,
/// and the split lets callers opt into it explicitly.
///
/// These members only become visible on `YamlValue` once this module is opened:
/// `open FSharp.Data.YamlValueBuilders`.
module FSharp.Data.YamlValueBuilders

open System
open FSharp.Data

/// A path from the document root to a node, as a fluent chain of mapping-key and sequence-index
/// steps. Wraps the same `YamlPathStep`/`YamlPath` (a root-first `YamlPathStep list`) used for
/// `YamlDocument.Comments` — this is an alternate, equally-typed way to build one, aimed at
/// callers (particularly from C#) for whom constructing `Key`/`Index` union cases directly is
/// unidiomatic. Prefer the string-path DSL (`YamlValue.SetPath("a.b[0]", ...)`) for the common
/// case; reach for this builder — or a raw `YamlPathStep list` — for non-string keys or paths
/// assembled programmatically (loops, dynamic segments).
[<Sealed>]
type YamlPath private (steps: YamlPathStep list) =

    /// The empty path — the document root itself.
    static member Root: YamlPath = YamlPath([])

    /// Constructs a `YamlPath` directly from a root-first step list, e.g. one already computed
    /// for `YamlDocument.Comments`.
    static member OfSteps(steps: YamlPathStep list): YamlPath = YamlPath(steps)

    /// Appends a string-keyed mapping step.
    member _.Key(name: string): YamlPath = YamlPath(steps @ [ Key(YamlValue.String name) ])

    /// Appends a mapping step with an arbitrary (non-string) key.
    member _.Key(key: YamlValue): YamlPath = YamlPath(steps @ [ Key key ])

    /// Appends a sequence-index step.
    member _.Index(index: int): YamlPath = YamlPath(steps @ [ Index index ])

    /// The underlying root-first step list.
    member _.Steps: YamlPathStep list = steps

/// Parses the string-path DSL (`"services.web.ports[0]"`) into a `YamlPathStep list`. Dot
/// separates mapping-key segments; `[n]` addresses a sequence index; `["a.b"]` bracket-quotes a
/// key that itself contains `.`, `[`, or `]`. The DSL only expresses string-keyed mapping steps —
/// non-string keys need `YamlPath`'s `Key(YamlValue)` overload or a raw `YamlPathStep list`.
module internal YamlPathDsl =

    let private fail (path: string) (pos: int) (message: string) : 'a =
        raise (FormatException(sprintf "Malformed YAML path '%s' at position %d: %s" path pos message))

    /// Reads a bare (unquoted) key segment starting at `pos`, up to the next `.`, `[`, or the end
    /// of the string.
    let private readBare (path: string) (pos: int) : string * int =
        let mutable i = pos
        while i < path.Length && path.[i] <> '.' && path.[i] <> '[' do
            i <- i + 1
        if i = pos then
            fail path pos "expected a key"
        path.Substring(pos, i - pos), i

    /// Reads a `["..."]`-quoted key segment, unescaping `\"` and `\\`. `pos` is the index of the
    /// opening quote.
    let private readQuoted (path: string) (pos: int) : string * int =
        let sb = Text.StringBuilder()
        let mutable i = pos + 1
        let mutable closed = false
        while not closed do
            if i >= path.Length then
                fail path pos "unterminated quoted key"
            match path.[i] with
            | '"' -> closed <- true
            | '\\' when i + 1 < path.Length -> sb.Append(path.[i + 1]) |> ignore; i <- i + 2
            | c -> sb.Append(c) |> ignore; i <- i + 1
        sb.ToString(), i + 1

    /// Reads a `[...]` step starting at the `[`, returning the resulting `YamlPathStep` and the
    /// position just past the closing `]`.
    let private readBracket (path: string) (pos: int) : YamlPathStep * int =
        let inner = pos + 1
        if inner < path.Length && path.[inner] = '"' then
            let key, afterKey = readQuoted path inner
            if afterKey >= path.Length || path.[afterKey] <> ']' then
                fail path afterKey "expected closing ']'"
            Key(YamlValue.String key), afterKey + 1
        else
            let mutable i = inner
            while i < path.Length && path.[i] <> ']' do
                i <- i + 1
            if i >= path.Length then
                fail path pos "unterminated '['"
            let digits = path.Substring(inner, i - inner)
            match Int32.TryParse(digits, Globalization.NumberStyles.Integer, Globalization.CultureInfo.InvariantCulture) with
            | true, n when n >= 0 -> Index n, i + 1
            | _ -> fail path inner (sprintf "expected a non-negative index, got '%s'" digits)

    let parse (path: string) : YamlPathStep list =
        if String.IsNullOrEmpty path then
            []
        else
            let rec loop (pos: int) (acc: YamlPathStep list) : YamlPathStep list =
                if pos >= path.Length then
                    List.rev acc
                elif path.[pos] = '[' then
                    let step, next = readBracket path pos
                    loop next (step :: acc)
                elif path.[pos] = '.' then
                    if acc.IsEmpty then fail path pos "a path cannot start with '.'"
                    let name, next = readBare path (pos + 1)
                    loop next (Key(YamlValue.String name) :: acc)
                else
                    let name, next = readBare path pos
                    loop next (Key(YamlValue.String name) :: acc)
            loop 0 []

type YamlValue with

    // -----------------------------------------------------------------
    // Shallow mapping edits
    // -----------------------------------------------------------------

    /// Sets a string-keyed property, replacing it if present or appending it otherwise. Returns a
    /// new `YamlValue` — `this` is left unchanged. If `this` is `YamlValue.Null`, a fresh
    /// single-entry mapping is created; any other non-mapping value raises.
    member this.SetProperty(name: string, value: YamlValue) : YamlValue =
        let key = YamlValue.String name
        match this with
        | YamlValue.Mapping properties ->
            match properties |> Array.tryFindIndex (fun (k, _) -> k = key) with
            | Some i -> YamlValue.Mapping(properties |> Array.mapi (fun idx (k, v) -> if idx = i then (k, value) else (k, v)))
            | None -> YamlValue.Mapping(Array.append properties [| (key, value) |])
        | YamlValue.Null -> YamlValue.Mapping [| (key, value) |]
        | _ -> failwithf "YamlValue.SetProperty: not a mapping: %O" this

    /// Removes a string-keyed property. A no-op (returns `this` unchanged) if `this` is not a
    /// mapping, or the property is not present.
    member this.RemoveProperty(name: string) : YamlValue =
        let key = YamlValue.String name
        match this with
        | YamlValue.Mapping properties ->
            if properties |> Array.exists (fun (k, _) -> k = key) then
                YamlValue.Mapping(properties |> Array.filter (fun (k, _) -> k <> key))
            else
                this
        | _ -> this

    // -----------------------------------------------------------------
    // Path-addressed edits
    // -----------------------------------------------------------------

    /// Sets the value at `path`, auto-vivifying missing intermediate mappings/sequences along the
    /// way (like `mkdir -p`) and padding a sequence with `YamlValue.Null` if `path` addresses an
    /// index beyond its current length. If a scalar (non-`Null`) node is encountered where a
    /// container is needed to continue descending, it is overwritten by default; pass
    /// `overwriteScalars = false` to raise instead. Returns a new `YamlValue` — `this` is left
    /// unchanged.
    member this.SetPath(path: YamlPath, value: YamlValue, ?overwriteScalars: bool) : YamlValue =
        let overwriteScalars = defaultArg overwriteScalars true

        let rec setAt (steps: YamlPathStep list) (node: YamlValue) : YamlValue =
            match steps with
            | [] -> value
            | Key k :: rest ->
                let properties =
                    match node with
                    | YamlValue.Mapping ps -> ps
                    | YamlValue.Null -> [||]
                    | _ when overwriteScalars -> [||]
                    | _ -> failwithf "YamlValue.SetPath: cannot descend into a scalar at key %O (pass overwriteScalars = true to allow)" k
                match properties |> Array.tryFindIndex (fun (pk, _) -> pk = k) with
                | Some i ->
                    let childNew = setAt rest (snd properties.[i])
                    YamlValue.Mapping(properties |> Array.mapi (fun idx (pk, pv) -> if idx = i then (pk, childNew) else (pk, pv)))
                | None ->
                    let childNew = setAt rest YamlValue.Null
                    YamlValue.Mapping(Array.append properties [| (k, childNew) |])
            | Index idx :: rest ->
                if idx < 0 then failwithf "YamlValue.SetPath: negative index %d" idx
                let elements =
                    match node with
                    | YamlValue.Sequence es -> es
                    | YamlValue.Null -> [||]
                    | _ when overwriteScalars -> [||]
                    | _ -> failwithf "YamlValue.SetPath: cannot descend into a scalar at index %d (pass overwriteScalars = true to allow)" idx
                let padded =
                    if idx < elements.Length then elements
                    else Array.append elements (Array.create (idx - elements.Length + 1) YamlValue.Null)
                let childNew = setAt rest padded.[idx]
                YamlValue.Sequence(padded |> Array.mapi (fun i e -> if i = idx then childNew else e))

        setAt path.Steps this

    /// Sets the value at the string-path DSL location `path` (e.g. `"services.web.ports[0]"`).
    /// See the `YamlPath` overload for auto-vivify/padding/`overwriteScalars` semantics.
    member this.SetPath(path: string, value: YamlValue, ?overwriteScalars: bool) : YamlValue =
        this.SetPath(YamlPath.OfSteps(YamlPathDsl.parse path), value, ?overwriteScalars = overwriteScalars)

    /// Removes the value at `path`. A no-op (returns `this` unchanged) if any step of `path` does
    /// not exist. Removing a sequence index splices the element out (shifts later elements down)
    /// rather than leaving a `YamlValue.Null` hole. Emptied parent mappings/sequences are left in
    /// place, not pruned. Returns a new `YamlValue` — `this` is left unchanged.
    member this.RemovePath(path: YamlPath) : YamlValue =
        let rec removeAt (steps: YamlPathStep list) (node: YamlValue) : YamlValue =
            match steps with
            | [] -> node
            | [ Key k ] ->
                match node with
                | YamlValue.Mapping properties when properties |> Array.exists (fun (pk, _) -> pk = k) ->
                    YamlValue.Mapping(properties |> Array.filter (fun (pk, _) -> pk <> k))
                | _ -> node
            | [ Index idx ] ->
                match node with
                | YamlValue.Sequence es when idx >= 0 && idx < es.Length ->
                    YamlValue.Sequence(es |> Array.indexed |> Array.filter (fun (i, _) -> i <> idx) |> Array.map snd)
                | _ -> node
            | Key k :: rest ->
                match node with
                | YamlValue.Mapping properties ->
                    match properties |> Array.tryFindIndex (fun (pk, _) -> pk = k) with
                    | Some i ->
                        let childNew = removeAt rest (snd properties.[i])
                        YamlValue.Mapping(properties |> Array.mapi (fun idx (pk, pv) -> if idx = i then (pk, childNew) else (pk, pv)))
                    | None -> node
                | _ -> node
            | Index idx :: rest ->
                match node with
                | YamlValue.Sequence es when idx >= 0 && idx < es.Length ->
                    let childNew = removeAt rest es.[idx]
                    YamlValue.Sequence(es |> Array.mapi (fun i e -> if i = idx then childNew else e))
                | _ -> node

        removeAt path.Steps this

    /// Removes the value at the string-path DSL location `path`. See the `YamlPath` overload for
    /// no-op/splice/no-prune semantics.
    member this.RemovePath(path: string) : YamlValue =
        this.RemovePath(YamlPath.OfSteps(YamlPathDsl.parse path))

    // -----------------------------------------------------------------
    // Path-addressed reads
    // -----------------------------------------------------------------

    /// Reads the value at `path`. Returns `None` if any step of `path` does not exist (a missing
    /// mapping key, an out-of-range sequence index, or descending into a scalar/`Null`).
    member this.TryGetPath(path: YamlPath) : YamlValue option =
        let rec go (steps: YamlPathStep list) (node: YamlValue) : YamlValue option =
            match steps with
            | [] -> Some node
            | Key k :: rest ->
                match node with
                | YamlValue.Mapping properties ->
                    properties |> Array.tryFind (fun (pk, _) -> pk = k) |> Option.bind (fun (_, v) -> go rest v)
                | _ -> None
            | Index idx :: rest ->
                match node with
                | YamlValue.Sequence es when idx >= 0 && idx < es.Length -> go rest es.[idx]
                | _ -> None
        go path.Steps this

    /// Reads the value at the string-path DSL location `path` (e.g. `"services.web.ports[0]"`).
    /// Returns `None` under the same conditions as the `YamlPath` overload.
    member this.TryGetPath(path: string) : YamlValue option =
        this.TryGetPath(YamlPath.OfSteps(YamlPathDsl.parse path))

    /// Reads the value at `path`, raising if any step does not exist. See `TryGetPath` for the
    /// `None`/missing conditions this instead surfaces as an exception.
    member this.GetPath(path: YamlPath) : YamlValue =
        match this.TryGetPath(path) with
        | Some v -> v
        | None -> failwithf "YamlValue.GetPath: path not found: %A" path.Steps

    /// Reads the value at the string-path DSL location `path`, raising if not found.
    member this.GetPath(path: string) : YamlValue =
        match this.TryGetPath(path) with
        | Some v -> v
        | None -> failwithf "YamlValue.GetPath: path not found: %s" path
