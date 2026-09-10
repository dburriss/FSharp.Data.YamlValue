#load "load-library.fsx"
open FSharp.Data

let config =
    YamlValue.Mapping [|
        YamlValue.String "name", YamlValue.String "web"
        YamlValue.String "port", YamlValue.Number 8080M
        YamlValue.String "tags",
        YamlValue.Sequence [| YamlValue.String "http"; YamlValue.String "public" |]
    |]

printfn "-- block style --"
printfn "%s" (config.ToString(YamlSaveOptions.None))

printfn "-- flow style --"
printfn "%s" (config.ToString(YamlSaveOptions.Flow))

// Strings that would resolve to something else on reparse get quoted
// automatically, so Parse(v.ToString(...)) = v always holds.
let tricky = YamlValue.Mapping [| YamlValue.String "version", YamlValue.String "true" |]
printfn "-- quoting a string that looks like a bool --"
printfn "%s" (tricky.ToString(YamlSaveOptions.None))

let roundTripped = YamlValue.Parse(tricky.ToString(YamlSaveOptions.None))
printfn "round-trips equal: %b" (roundTripped = tricky)
