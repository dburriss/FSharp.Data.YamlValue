#load "load-library.fsx"
open FSharp.Data
open FSharp.Data.YamlExtensions

let compose = YamlValue.Load(__SOURCE_DIRECTORY__ + "/docker-compose.yml")

// <<: *defaults merges the anchor's keys into each service, which then
// override individual keys of their own (image, ports).
printfn "web.restart: %s" (compose?services?web?restart.AsString())
printfn "web.logging: %s" (compose?services?web?logging.AsString())
printfn "web.image:   %s" (compose?services?web?image.AsString())

printfn "db.restart:  %s" (compose?services?db?restart.AsString())
printfn "db.image:    %s" (compose?services?db?image.AsString())
