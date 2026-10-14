module Program

open System

[<EntryPoint>]
let main _ =
    let mutable passed = 0
    let failures = ResizeArray<string * string>()

    for (name, run) in Runner.all do
        try
            run ()
            passed <- passed + 1
        with e ->
            failures.Add((name, e.Message))

    for (name, message) in failures do
        printfn "FAILED: %s\n    %s" name (message.Replace("\n", "\n    "))

    printfn "Fable run: %d passed, %d failed, %d total" passed failures.Count (passed + failures.Count)
    if failures.Count = 0 then 0 else 1
