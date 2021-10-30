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
open System.Collections.Generic

module Discord =
  
  let private token = Environment.GetEnvironmentVariable("JuliaToken")

  let private reciveLogAsync log: Task = task {
    JuliaMessages.Log log
    |> Sys.Proxy.Message.julia
  }

  let private readyAsync(): Task = task { return printfn "Connected" }

  let private messageReceivedAsync socketMessage: Task = task {
    JuliaMessages.ReciveMessage socketMessage
    |> Sys.Proxy.Message.julia
  }

  let private guildAdd (guild: SocketGuild): Task = task {
    JuliaMessages.JoinedGuild guild
    |> Sys.Proxy.Message.julia
  }

  let inline private initClient() =
    let dclient = new DiscordSocketClient()
    dclient.add_Log(reciveLogAsync)
    dclient.add_Ready(readyAsync)
    dclient.add_MessageReceived(messageReceivedAsync)
    dclient.add_JoinedGuild(guildAdd)
    dclient

  module private Guild =

    let [<Literal>] private Restart = "Restart"

    let commandsSystem = [ 
      {| command = Restart
         message = GuildActorMessages.RestartActors
         desc    = "Перезапускает акторов гильдии"|}
    ]

    module private LifecycleEvent =

      let inline preStart (ctx: GuildActorContext<_>) cycle =
        printfn "Actor for guild %s start" ctx.State.Name
        cycle ctx.State

      let inline postStop (ctx: GuildActorContext<_>) cycle =
        printfn "PostStop"
        ignored()

      let inline preRestart (ctx: GuildActorContext<_>) cause message cycle =
        printfn "PreRestart cause: %A message: %A" cause message
        ignored()

      let inline postRestart (ctx: GuildActorContext<_>) cause cycle =
        printfn "PostRestart cause: %A" cause
        ignored()

    module private GuildActorMessages =

      let private parseCommands (ctx: GuildActorContext<_>) (sm: SocketMessage) =

        let msgArray: string[] = sm.Content.ToLower().Split(' ')

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
            Utils.sendEmbed sm <| Utils.answerEmbed "Help" (commandsString.Substring(0, commandsString.Length - 4))

        let bardCommands() =
          Bard.commands
          |> List.iter (fun commands ->
            if msgArray.[0].ToLower().StartsWith(prefix + commands.command.ToLower()) then
              commands.message sm
              |> Sys.Proxy.Message.bard ctx.State.Id
          )

        let guildCommandsSystem() =
          if sm.Author.Id = 161572135301677057UL then
            commandsSystem
            |> List.iter (fun commands ->
              if msgArray.[0].ToLower().StartsWith(prefix + commands.command.ToLower()) then
                commands.message sm
                |> Sys.Proxy.Message.guildActor ctx.State.Id
            )

        if msgArray.Length > 0 then
          bardCommands()
          guildCommandsSystem()
          helpCommands()
      
      let inline guildMessage (ctx: GuildActorContext<_>) sm cycle =
        parseCommands ctx sm
        cycle ctx.State

      let inline restartActors (ctx: GuildActorContext<_>) sm cycle =
        let id = ctx.State.Id

        Utils.sendEmbed sm <| Utils.answerEmbed Restart "Перезапускаются акторы гильдии"

        BardMessages.Restart sm
        |> Sys.Proxy.Message.bard id

        SongkeeperMessages.Restart
        |> Sys.Proxy.Message.songkeeper id

        GuildActorMessages.Restart sm
        |> Sys.Proxy.Message.guildActor id

        cycle ctx.State
    
    let guildActor (sg: SocketGuild) (mb: Actor<_>) =
      let rec cycle guild = actor {

        let! (msg: obj) = mb.Receive()

        let ctx: GuildActorContext<_> = { State = guild; Mailbox = mb }

        match msg with
        | LifecycleEvent le -> 
          match le with
          | PreStart                    -> return! LifecycleEvent.preStart ctx cycle
          | PostStop                    -> return! LifecycleEvent.postStop ctx cycle
          | PreRestart (cause, message) -> return! LifecycleEvent.preRestart ctx cause message cycle
          | PostRestart cause           -> return! LifecycleEvent.postRestart ctx cause cycle
        | :? GuildActorMessages as gam ->
          match gam with
          | GuildActorMessages.GuildMessage sm  -> return! GuildActorMessages.guildMessage ctx sm cycle
          | GuildActorMessages.RestartActors sm -> return! GuildActorMessages.restartActors ctx sm cycle
          | GuildActorMessages.Restart sm       -> failwith "Restart"
        | _ -> return! Ignore

      }

      cycle sg

  module Julia =

    module private JuliaMessages =

      let private createGuildActors (guild: SocketGuild) =

        let actorGuildName: string<actor_name> = % sprintf "%s%d" %Sys.Names.guildActor guild.Id
        let actorGuildFunc = Guild.guildActor guild
        Sys.supervisor <! SupervisorMessages.CreateSystemActor(actorGuildFunc, actorGuildName)

        let actorBardName: string<actor_name> = % sprintf "%s%d" %Sys.Names.bard guild.Id
        let actorBardFunc = Bard.bardActor guild
        Sys.supervisor <! SupervisorMessages.CreateSystemActor(actorBardFunc, actorBardName)

        let actorSongKeeperName: string<actor_name> = % sprintf "%s%d" %Sys.Names.songkeeper guild.Id
        Sys.supervisor <! SupervisorMessages.CreateSystemActor(SongKeeper.songKeeperActor, actorSongKeeperName)

      let inline reciveMessage (ctx: JuliaContext<_>) (sm: SocketMessage) cycle =
        printfn "Recive: A: %s M: %s" sm.Author.Username sm.Content

        match sm.Author with
        | :? SocketGuildUser as user ->
          GuildActorMessages.GuildMessage sm
          |> Sys.Proxy.Message.guildActor user.Guild.Id
        | _ -> ()

        cycle ctx.State

      let inline log (ctx: JuliaContext<_>) logMessage cycle =
        printfn "%s" <| logMessage.ToString()
        cycle ctx.State

      let inline initFinish (ctx: JuliaContext<_>) cycle =
        while ctx.State = null || ctx.State.Guilds.Count < 1 do
          Thread.Sleep(1000)
        for guild in ctx.State.Guilds do
          printfn "Guild %s spawn actors" guild.Name
          createGuildActors guild
        cycle ctx.State

      let inline joinedGuild (ctx: JuliaContext<_>) guild cycle =
        createGuildActors guild
        cycle ctx.State

    module private LifecycleEvent =

      let inline preStart (ctx: JuliaContext<_>) cycle =
        printfn "PreStart discord client"
        let d = initClient()
        d.LoginAsync(TokenType.Bot, token).Wait()
        d.StartAsync().Wait()
        ctx.Mailbox.Self <! box JuliaMessages.InitFinish
        cycle d

      let inline postStop (ctx: JuliaContext<_>) cycle =
        printfn "PostStop"
        ignored()

      let inline preRestart (ctx: JuliaContext<_>) cause message cycle =
        printfn "PreRestart cause: %A message: %A" cause message
        ignored()

      let inline postRestart (ctx: JuliaContext<_>) cause cycle =
        printfn "PostRestart cause: %A" cause
        ignored()

    let juliaActor (mb: Actor<_>) =
      let rec cycle (dclient: DiscordSocketClient) = actor {

        let! (msg: obj) = mb.Receive()

        let ctx: JuliaContext<_> = { State = dclient; Mailbox = mb }

        match msg with
        | LifecycleEvent le -> 
          match le with
          | PreStart                    -> return! LifecycleEvent.preStart ctx cycle
          | PostStop                    -> return! LifecycleEvent.postStop ctx cycle
          | PreRestart (cause, message) -> return! LifecycleEvent.preRestart ctx cause message cycle
          | PostRestart cause           -> return! LifecycleEvent.postRestart ctx cause cycle
        | :? JuliaMessages as jm ->
          match jm with
          | JuliaMessages.ReciveMessage sm -> return! JuliaMessages.reciveMessage ctx sm cycle
          | JuliaMessages.Log l            -> return! JuliaMessages.log ctx l cycle
          | JuliaMessages.InitFinish       -> return! JuliaMessages.initFinish ctx cycle
          | JuliaMessages.JoinedGuild g    -> return! JuliaMessages.joinedGuild ctx g cycle
        | _ as some ->
          printfn "Ignored MSG: %A" some
          return! Ignore
      }

      cycle null