module Campfire.Kit.Differential.Program

open System

[<EntryPoint>]
let main _ =
    let mutable line = Console.ReadLine()
    while not (isNull line) do
        Console.Out.WriteLine(Operations.run (nonNull line))
        line <- Console.ReadLine()
    0
