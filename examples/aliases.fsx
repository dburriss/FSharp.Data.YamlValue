#load "load-library.fsx"
open FSharp.Data
open FSharp.Data.YamlExtensions

// See anchors-and-merge-keys.fsx for `<<: *anchor` merging. This script is about a
// plain alias `*anchor` used directly as a value — it resolves during parsing into a
// plain tree, with the anchor's subtree *shared*, not copied, across every alias site.

let config = YamlValue.Load(__SOURCE_DIRECTORY__ + "/aliases.yml")

printfn "primary: %s:%d" (config?primary?host.AsString()) (config?primary?port.AsInteger())
printfn "replica: %s:%d" (config?replica?host.AsString()) (config?replica?port.AsInteger())

// primary and replica are structurally equal, because they came from the same anchor.
printfn "primary = replica: %b" (config?primary = config?replica)

// An alias can point at a sequence just as well as a mapping.
printfn "failover regions: %s" (config?failover.AsArray() |> Array.map (fun r -> r.AsString()) |> String.concat ", ")

// A *recursive* anchor - one that would alias back into itself - can't be represented
// in an immutable tree, so the parser rejects it rather than looping forever.
try
    YamlValue.Parse "a: &anchor\n  b: *anchor" |> ignore
    printfn "unexpected: recursive anchor parsed"
with :? YamlParseException as ex ->
    printfn "recursive anchor rejected: %s" ex.Message
