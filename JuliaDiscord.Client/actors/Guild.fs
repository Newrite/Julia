namespace JuliaDiscord.Client

open System
open System.Threading
open System.Threading.Tasks

open Discord
open Discord.Net
open Discord.WebSocket

open Akka
open Akkling
open Akka.Actor
open Akkling.Actors

open YoutubeExplode.Videos.Streams

open System.Diagnostics

open FSharp.UMX

open JuliaDiscord.Core

module GuildActor =

    let [<Literal>] private Restart = "Restart"
    let [<Literal>] private Status = "Status"

    let commandsSystem = [ 
      {| command = Restart
         message = GuildActorMessages.ActorsRestart
         desc    = "Перезапускает акторов гильдии"|}
      {| command = Status
         message = GuildActorMessages.ActorsStatus
         desc    = "Выдает статус акторов гильдии"|}
    ]

    module private LifecycleEvent =

      let inline preStart (ctx: GuildActorContext<_, _>) cycle =
        printfn "Actor for guild %s start" ctx.Proxy.Guild.Name
        cycle()

      let inline postStop (ctx: GuildActorContext<_, _>) cycle =
        printfn "PostStop"
        ignored()

      let inline preRestart (ctx: GuildActorContext<_, _>) cause message cycle =
        printfn "PreRestart cause: %A message: %A" cause message
        ignored()

      let inline postRestart (ctx: GuildActorContext<_, _>) cause cycle =
        printfn "PostRestart cause: %A" cause
        ignored()

    module private GuildActorMessages =

      let private parseCommands (ctx: GuildActorContext<_, _>) (gmc: GuildMessageContext) =

        let msgArray: string[] = gmc.Message.Content.ToLower().Split(' ')

        let prefix = ">"

        let helpCommands() =
          if msgArray.[0].ToLower().StartsWith(prefix + "help") then
            let mutable commandsString = ""
            commandsString <- commandsString + "**Комманды барда**\n"
            for command in Bard.commands do
              commandsString <- commandsString + sprintf "**%s**::\t\t %s\n---\n" command.command command.desc
            commandsString <- commandsString + "---\n**Комманды системы**\n"
            for command in commandsSystem do
              commandsString <- commandsString + sprintf "**%s**::\t\t %s\n---\n" command.command command.desc
            Utils.sendEmbed gmc <| Utils.answerEmbed "Help" (commandsString.Substring(0, commandsString.Length - 4))

        let bardCommands() =
          Bard.commands
          |> List.iter (fun commands ->
            if msgArray.[0].ToLower().StartsWith(prefix + commands.command.ToLower()) then
              commands.message gmc
              |> ctx.Proxy.Message.Bard
          )

        let guildCommandsSystem() =
          if gmc.Message.Author.Id = 161572135301677057UL then
            commandsSystem
            |> List.iter (fun commands ->
              if msgArray.[0].ToLower().StartsWith(prefix + commands.command.ToLower()) then
                commands.message gmc
                |> ctx.Proxy.Message.GuildActor
            )

        if msgArray.Length > 0 then
          bardCommands()
          guildCommandsSystem()
          helpCommands()
      
      let inline guildMessage (ctx: GuildActorContext<_, _>) gmc cycle =
        parseCommands ctx gmc
        cycle()

      let inline actorsRestart (ctx: GuildActorContext<_, _>) gmc cycle =

        Utils.sendEmbed gmc <| Utils.answerEmbed Restart "Перезапускаются акторы гильдии"

        GuildSystemMessage.Restart gmc
        |> ctx.Proxy.GuildSystemMessage.Bard

        GuildSystemMessage.Restart gmc
        |> ctx.Proxy.GuildSystemMessage.Songkeeper

        GuildSystemMessage.Restart gmc
        |> ctx.Proxy.GuildSystemMessage.Youtuber

        GuildSystemMessage.Restart gmc
        |> ctx.Proxy.GuildSystemMessage.GuildActor

        cycle()

      let inline actorsStatus (ctx: GuildActorContext<_, _>) gmc cycle =

        task {
        
          Utils.sendEmbed gmc <| Utils.answerEmbed Status "Опрашиваются акторы гильдии"

          let! (bardAnswer: string) =
            GuildSystemAsk.Status gmc
            |> ctx.Proxy.GuildSystemAsk.Bard

          let! (songkeeperAnswer: string) =
            GuildSystemAsk.Status gmc
            |> ctx.Proxy.GuildSystemAsk.Songkeeper

          let! (youtuberAnswer: string) =
            GuildSystemAsk.Status gmc
            |> ctx.Proxy.GuildSystemAsk.Youtuber

          let! (guildActorAnswer: string) =
            GuildSystemAsk.Status gmc
            |> ctx.Proxy.GuildSystemAsk.GuildActor

          Utils.sendEmbed gmc
          <| Utils.answerEmbed Status ($"**Bard**\n{bardAnswer}\n**Songkeeper**\n{songkeeperAnswer}\n" +
              $"**Youtuber**\n{youtuberAnswer}\n**GuildActor**\n{guildActorAnswer}")
        } |> ignore

        cycle()

    module private GuildSystemMessage =
      
      let inline restart (ctx: GuildActorContext<_, _>) gmc cycle =
        failwith "restart"

    module private GuildSystemAsk =
      
      let inline status (ctx: GuildActorContext<_, _>) gmc cycle =
        ctx.Mailbox.Sender() <! "Жив, цел, арёл!"
        cycle()
    
    let guildActor (guildProxy: GuildProxy<_>) (mb: Actor<_>) =
      let rec cycle() = actor {

        let! (msg: obj) = mb.Receive()

        let ctx: GuildActorContext<_, _> = { Mailbox = mb; Proxy = guildProxy }

        match msg with
        | LifecycleEvent le ->
          match le with
          | PreStart                    -> return! LifecycleEvent.preStart    ctx cycle
          | PostStop                    -> return! LifecycleEvent.postStop    ctx cycle
          | PreRestart (cause, message) -> return! LifecycleEvent.preRestart  ctx cause message cycle
          | PostRestart cause           -> return! LifecycleEvent.postRestart ctx cause cycle
        | :? GuildActorMessages as gam ->
          match gam with
          | GuildActorMessages.GuildMessage gmc  -> return! GuildActorMessages.guildMessage  ctx gmc cycle
          | GuildActorMessages.ActorsRestart gmc -> return! GuildActorMessages.actorsRestart ctx gmc cycle
          | GuildActorMessages.ActorsStatus gmc  -> return! GuildActorMessages.actorsStatus  ctx gmc cycle
        | :? GuildSystemMessage as gsm ->
          match gsm with
          | GuildSystemMessage.Restart sm -> return! GuildSystemMessage.restart ctx sm cycle
        | :? GuildSystemAsk as gsa ->
          match gsa with
          | GuildSystemAsk.Status sm -> return! GuildSystemAsk.status ctx sm cycle
            
        | _ -> return! Ignore

      }

      cycle()