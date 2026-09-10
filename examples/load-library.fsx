// Shared bootstrap: #load this from any other script in this folder.
//
//     dotnet build src/FSharp.Data.Yaml/FSharp.Data.Yaml.fsproj
//     dotnet fsi examples/quickstart.fsx
//
// Rebuild the library after code changes so the scripts pick them up.

#r "../src/FSharp.Data.Yaml/bin/Debug/net8.0/FSharp.Data.Yaml.dll"

open FSharp.Data
open FSharp.Data.YamlExtensions
