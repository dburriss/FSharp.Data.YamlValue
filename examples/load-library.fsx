// Shared bootstrap: #load this from any other script in this folder.
//
//     dotnet build src/FSharp.Data.YamlValue/FSharp.Data.YamlValue.fsproj
//     dotnet fsi examples/quickstart.fsx
//
// Rebuild the library after code changes so the scripts pick them up.

#r "../src/FSharp.Data.YamlValue/bin/Debug/net8.0/FSharp.Data.YamlValue.dll"

open FSharp.Data
open FSharp.Data.YamlExtensions
