// `YamlReader` and `YamlScalar` are `internal` — implementation details not part of the public
// API surface documented in `docs/reference.md`. The test assembly needs to exercise them
// directly (see `ScalarTests.fs`, `YamlReaderTests.fs`), so it is declared a friend assembly.
module internal FSharp.Data.AssemblyInfo

[<assembly: System.Runtime.CompilerServices.InternalsVisibleTo("FSharp.Data.Yaml.Tests")>]
do ()
