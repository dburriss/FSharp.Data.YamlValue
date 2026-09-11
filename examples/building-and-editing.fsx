#load "load-library.fsx"
open FSharp.Data
open FSharp.Data.YamlValueBuilders
open FSharp.Data.YamlExtensions

let doc = YamlValue.Load(__SOURCE_DIRECTORY__ + "/docker-compose.yml")

let updated =
    doc
        .SetPath("services.web.image", YamlValue.String "nginx:1.27")
        .SetPath("services.web.ports[0]", YamlValue.Number 8080M)
        .RemovePath("services.db")

printfn "%s" (updated.ToString(YamlSaveOptions.None))

printfn "web.image is now: %s" (updated?services?web?image.AsString())
printfn "db present: %b" (updated?services.TryGetProperty("db") |> Option.isSome)
