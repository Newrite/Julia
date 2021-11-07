open Julia.Core
open Julia.Discord

open Akkling

Sys.supervisor <! SupervisorMessages.CreateSystemActor(Discord.Julia.juliaActor, Sys.Names.client)

System.Console.ReadKey() |> ignore