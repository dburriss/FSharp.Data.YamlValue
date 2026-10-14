// Generates the Fable test runner from the shared xUnit test files.
//
// xUnit and FsCheck cannot run under Fable, so tests/FSharp.Data.YamlValue.Fable compiles the very
// same test files (with a tiny `Xunit` shim) and this script writes, next to it:
//
//   * Runner.generated.fs         - calls every [<Fact>] / [<Theory>] function directly
//   * FableTestData.generated.fs  - embeds tests/FSharp.Data.YamlValue.Tests/data/* as strings,
//                                   because Fable has no file system access
//
// Usage: dotnet fsi scripts/fable-gen-runner.fsx
open System
open System.IO
open System.Text
open System.Text.RegularExpressions

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let testsDir = Path.Combine(root, "tests", "FSharp.Data.YamlValue.Tests")
let outDir = Path.Combine(root, "tests", "FSharp.Data.YamlValue.Fable")

/// Test files left out of the Fable run: RoundTripTests is FsCheck-based (see docs/fable.md).
let skipFiles = set [ "RoundTripTests.fs"; "PlaceholderTests.fs" ]

let fsprojOrder () =
    let text = File.ReadAllText(Path.Combine(testsDir, "FSharp.Data.YamlValue.Tests.fsproj"))
    [ for m in Regex.Matches(text, "<Compile Include=\"([^\"]+\\.fs)\"") -> m.Groups.[1].Value ]

/// Splits `a, "b,c", 3` on top-level commas.
let splitTopLevel (text: string) : string list =
    let parts = ResizeArray<string>()
    let cur = StringBuilder()
    let mutable depth = 0
    let mutable inStr = false
    let mutable i = 0
    while i < text.Length do
        let c = text.[i]
        if inStr then
            cur.Append c |> ignore
            if c = '\\' then
                cur.Append text.[i + 1] |> ignore
                i <- i + 1
            elif c = '"' then
                inStr <- false
        elif c = '"' then
            inStr <- true
            cur.Append c |> ignore
        elif c = '(' || c = '[' then
            depth <- depth + 1
            cur.Append c |> ignore
        elif c = ')' || c = ']' then
            depth <- depth - 1
            cur.Append c |> ignore
        elif c = ',' && depth = 0 then
            parts.Add(cur.ToString().Trim())
            cur.Clear() |> ignore
        else
            cur.Append c |> ignore
        i <- i + 1
    if cur.ToString().Trim() <> "" then parts.Add(cur.ToString().Trim())
    List.ofSeq parts

type Style =
    | Unit
    | Curried
    | Tupled

/// Returns the call style and the (name, type) of each parameter.
let parseParams (paramsText: string) : Style * (string * string) list =
    let t = paramsText.Trim()
    if t = "()" then
        Unit, []
    else
        let groups = [ for m in Regex.Matches(t, @"\(([^()]*)\)") -> m.Groups.[1].Value ]
        let style, items =
            match groups with
            | [ g ] when g.Contains "," -> Tupled, splitTopLevel g
            | _ -> Curried, groups |> List.map (fun g -> g.Trim())
        let parsed =
            items
            |> List.map (fun item ->
                let idx = item.IndexOf ':'
                item.Substring(0, idx).Trim(), item.Substring(idx + 1).Trim())
        style, parsed

type TestFn =
    { Name: string
      Params: string
      Attrs: string list }

/// Reads the module name and every [<Fact>]/[<Theory>] function (outside `#if !FABLE_COMPILER`).
let parseFile (path: string) : string * TestFn list =
    let lines = File.ReadAllLines path
    let moduleName =
        lines
        |> Array.pick (fun l ->
            let m = Regex.Match(l, @"^module\s+([\w.]+)")
            if m.Success then Some m.Groups.[1].Value else None)
    let tests = ResizeArray<TestFn>()
    let mutable attrs: string list = []
    let mutable skipDepth = 0
    for line in lines do
        let s = line.Trim()
        if s = "#if !FABLE_COMPILER" then
            skipDepth <- skipDepth + 1
        elif s.StartsWith "#if" && skipDepth > 0 then
            skipDepth <- skipDepth + 1
        elif s.StartsWith "#else" && skipDepth = 1 then
            skipDepth <- 0
        elif s.StartsWith "#endif" && skipDepth > 0 then
            skipDepth <- skipDepth - 1
        elif skipDepth > 0 then
            attrs <- []
        elif Regex.IsMatch(line, @"^\[<.*>\]\s*$") then
            attrs <- attrs @ [ s ]
        else
            let m = Regex.Match(line, @"^let\s+``([^`]+)``\s*(.*?)\s*=\s*(.*)$")
            if m.Success && not attrs.IsEmpty then
                if attrs |> List.exists (fun a -> a.StartsWith "[<Fact" || a.StartsWith "[<Theory") then
                    tests.Add { Name = m.Groups.[1].Value; Params = m.Groups.[2].Value; Attrs = attrs }
                attrs <- []
            elif s <> "" && not (s.StartsWith "//") then
                attrs <- []
    moduleName, List.ofSeq tests

let call (moduleName: string) (name: string) (style: Style) (args: string list) =
    let fn = moduleName + ".``" + name + "``"
    match style with
    | Unit -> fn + " ()"
    | Tupled -> fn + " (" + String.Join(", ", args) + ")"
    | Curried -> fn + " " + String.Join(" ", args |> List.map (fun a -> "(" + a + ")"))

let fsString (s: string) =
    "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\""

let entries =
    [ for f in fsprojOrder () do
          if not (skipFiles.Contains f) then
              let moduleName, tests = parseFile (Path.Combine(testsDir, f))
              let short = moduleName.Split('.') |> Array.last
              for t in tests do
                  let style, ps = parseParams t.Params
                  let inlineRows =
                      [ for a in t.Attrs do
                            let m = Regex.Match(a, @"^\[<InlineData\((.*)\)>\]$")
                            if m.Success then splitTopLevel m.Groups.[1].Value ]
                  let classData =
                      t.Attrs
                      |> List.tryPick (fun a ->
                          let m = Regex.Match(a, @"^\[<ClassData\(typeof<([\w.]+)>\)>\]$")
                          if m.Success then Some m.Groups.[1].Value else None)
                  let label = short + " / " + t.Name
                  if not inlineRows.IsEmpty then
                      for row in inlineRows do
                          yield label + " (" + String.Join(", ", row) + ")", call moduleName t.Name style row
                  else
                      match classData with
                      | Some cd ->
                          let unboxed = ps |> List.mapi (fun i (_, typ) -> "unbox<" + typ + "> row.[" + string i + "]")
                          yield
                              label,
                              "for row in (" + moduleName + "." + cd + "() :> seq<obj[]>) do "
                              + call moduleName t.Name style unboxed
                      | None -> yield label, call moduleName t.Name style [] ]

let runner = StringBuilder()
runner.Append("// <auto-generated> by scripts/fable-gen-runner.fsx - do not edit.\nmodule Runner\n\nopen System\nopen FSharp.Data\n\n")
|> ignore
runner.Append("let all : (string * (unit -> unit)) list =\n    [\n") |> ignore
for (label, body) in entries do
    runner.Append("      " + fsString label + ", (fun () -> " + body + ")\n") |> ignore
runner.Append("    ]\n") |> ignore
File.WriteAllText(Path.Combine(outDir, "Runner.generated.fs"), runner.ToString().Replace("\r\n", "\n"))

let data = StringBuilder()
data.Append("// <auto-generated> by scripts/fable-gen-runner.fsx - do not edit.\nmodule FableTestData\n\nlet files : (string * string) list =\n    [\n")
|> ignore
for p in Directory.GetFiles(Path.Combine(testsDir, "data")) |> Array.sort do
    data.Append("      " + fsString (Path.GetFileName p) + ", " + fsString (File.ReadAllText p) + "\n") |> ignore
data.Append("    ]\n\nlet private table = dict files\n\nlet read (fileName: string) : string = table.[fileName]\n\n")
|> ignore
data.Append(
    "let names (prefix: string) : string list =\n    files |> List.map fst |> List.filter (fun n -> n.StartsWith(prefix, System.StringComparison.Ordinal))\n"
)
|> ignore
File.WriteAllText(Path.Combine(outDir, "FableTestData.generated.fs"), data.ToString())

eprintfn "generated %d test entries" entries.Length
