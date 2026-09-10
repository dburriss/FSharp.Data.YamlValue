#load "load-library.fsx"
open FSharp.Data
open FSharp.Data.YamlExtensions

let doc = YamlDocument.Load(__SOURCE_DIRECTORY__ + "/commented-config.yml")

printfn "name: %s" (doc.Value?name.AsString())

// YamlValue.Parse would have discarded the comments entirely; YamlDocument keeps
// them in a path-keyed side table alongside the value.
printfn "-- round-tripped, comments and all --"
printfn "%s" (doc.ToString(YamlSaveOptions.None))

printfn "-- comments suppressed --"
printfn "%s" (doc.ToString(YamlSaveOptions.SuppressComments))
