#load "load-library.fsx"
open FSharp.Data
open FSharp.Data.YamlExtensions
open System.IO

let text = File.ReadAllText(__SOURCE_DIRECTORY__ + "/k8s-stream.yml")

for doc in YamlValue.ParseMultiple text do
    printfn "%s/%s" (doc?kind.AsString()) (doc?name.AsString())
