open Julia.Core
open Julia.Discord

open Akkling

Sys.juliavisor <! SupervisorMessages.CreateSystemActor(Discord.Julia.juliaActor, Sys.Names.Discord.client)

System.Console.ReadKey() |> ignore