open JuliaDiscord.Core
open JuliaDiscord.Client

open Akkling

Sys.supervisor <! SupervisorMessages.CreateSystemActor(Discord.Julia.juliaActor, Sys.Names.client)

System.Console.ReadKey() |> ignore