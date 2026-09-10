#load "load-library.fsx"
open FSharp.Data

let rec describe (value: YamlValue) : string =
    match value with
    | YamlValue.String s -> sprintf "string %s" s
    | YamlValue.Number n -> sprintf "number %M" n
    | YamlValue.Float f -> sprintf "float %f" f
    | YamlValue.Boolean b -> sprintf "boolean %b" b
    | YamlValue.Timestamp t -> sprintf "timestamp %O" t
    | YamlValue.Null -> "null"
    | YamlValue.Sequence xs -> xs |> Array.map describe |> String.concat ", "
    | YamlValue.Mapping ps ->
        ps
        |> Array.map (fun (k, v) -> sprintf "%s = %s" (describe k) (describe v))
        |> String.concat "; "

let info = YamlValue.Load(__SOURCE_DIRECTORY__ + "/person.yml")
printfn "%s" (describe info)

// The Norway problem: yes/no/on/off are deliberately left as strings.
match YamlValue.Parse "no" with
| YamlValue.String "no" -> printfn "\"no\" parsed as the string \"no\", not a boolean"
| _ -> failwith "unexpected"
