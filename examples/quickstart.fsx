#load "load-library.fsx"
open FSharp.Data
open FSharp.Data.YamlExtensions

let info = YamlValue.Load(__SOURCE_DIRECTORY__ + "/person.yml")

printfn "name: %s" (info?name.AsString())
printfn "born: %d" (info?born.AsInteger())
printfn "active: %b" (info?active.AsBoolean())

printfn "siblings:"
for sibling in info?siblings do
    printfn "  - %s" (sibling.AsString())

printfn "city: %s" (info?address?city.AsString())

// Quoted scalars always stay strings, even if they look numeric.
printfn "zip: %s" (info?address?zip.AsString())
