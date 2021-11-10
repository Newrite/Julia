namespace Julia.Discord

open System
open System.Threading
open System.Threading.Tasks

open Discord

open Discord.WebSocket

open Akkling

open FSharp.UMX

open Julia.Core

module Discord =

  [<NoComparison>]
  type private JuliaContext<'a> = {
    State:   DiscordSocketClient
    Mailbox: Actor<'a>
  }
  
  let private token = Environment.GetEnvironmentVariable("JuliaToken")

  let private reciveLogAsync log: Task = task {
    JuliaMessages.Log log
    |> Sys.Proxy.Discord.Message.julia
  }

  let private readyAsync(): Task = task { return printfn "Discord: Successfull connected" }

  let private messageReceivedAsync socketMessage: Task = task {
    JuliaMessages.ReciveMessage socketMessage
    |> Sys.Proxy.Discord.Message.julia
  }

  let private guildAdd guild: Task = task {
    JuliaMessages.JoinedGuild guild
    |> Sys.Proxy.Discord.Message.julia
  }

  let inline private initClient() =
    let dclient = new DiscordSocketClient()
    dclient.add_Log(reciveLogAsync)
    dclient.add_Ready(readyAsync)
    dclient.add_MessageReceived(messageReceivedAsync)
    dclient.add_JoinedGuild(guildAdd)
    dclient

  module Julia =

    module private JuliaMessages =

      let inline private createGuildActors (guild: SocketGuild) =

        printfn "Init spawn actors fot guild %s" guild.Name

        let actorGuildName:       ActorName = % sprintf "%s%d"   %Sys.Names.Discord.guildActor guild.Id
        let actorGuildFunc        = GuildActor.guildActor        <| Sys.ProxyDiscord.Create guild <| guild

        let actorGuildWriterName: ActorName = % sprintf "%s%d"   %Sys.Names.Discord.guildWriter guild.Id
        let actorGuildWriterFunc  = GuildWriter.guildWriterActor <| Sys.ProxyDiscord.Create guild <| guild

        let actorBardName:        ActorName = % sprintf "%s%d"   %Sys.Names.Discord.bard       guild.Id
        let actorBardFunc         = Bard.bardActor               <| Sys.ProxyDiscord.Create guild <| guild
                                                                 
        let actorSongkeeperName:  ActorName = % sprintf "%s%d"   %Sys.Names.Discord.songkeeper guild.Id
        let actorSongkeeperFunc   = Songkeeper.songkeeperActor   <| Sys.ProxyDiscord.Create guild <| guild
                                                                 
        let actorYoutuberName:    ActorName = % sprintf "%s%d"   %Sys.Names.Discord.youtuber   guild.Id
        let actorYoutuberFunc     = Youtuber.youtuberActor       <| Sys.ProxyDiscord.Create guild <| guild

        Sys.juliavisor <! SupervisorMessages.CreateSystemActor(actorGuildFunc,       actorGuildName)
        Sys.juliavisor <! SupervisorMessages.CreateSystemActor(actorBardFunc,        actorBardName)
        Sys.juliavisor <! SupervisorMessages.CreateSystemActor(actorSongkeeperFunc,  actorSongkeeperName)
        Sys.juliavisor <! SupervisorMessages.CreateSystemActor(actorYoutuberFunc,    actorYoutuberName)
        Sys.juliavisor <! SupervisorMessages.CreateSystemActor(actorGuildWriterFunc, actorGuildWriterName)

      let inline reciveMessage ctx (sm: SocketMessage) cycle =
        printfn $"DISCORD: {sm.Channel}:\t {sm.Author.Username}:\t {sm.Content}"

        match sm.Author with
        | :? SocketGuildUser as user ->
          let context =
            { Client  = ctx.State
              Guild   = user.Guild 
              Message = sm }
          GuildActorMessages.GuildMessage context
          |> Sys.Proxy.Discord.Message.guildActor context.Guild.Id
        | _ -> ()

        cycle ctx.State

      let inline log ctx logMessage cycle =
        printfn "DLOG: %s" <| logMessage.ToString()
        cycle ctx.State

      let inline initFinish ctx cycle =
        while isNull ctx.State || ctx.State.Guilds.Count < 1 do
          Thread.Sleep(1000)
        for guild in ctx.State.Guilds do
          createGuildActors guild
        cycle ctx.State

      let inline joinedGuild ctx guild cycle =
        printfn "joined guild"
        createGuildActors guild
        cycle ctx.State

    module private LifecycleEvent =

      let inline preStart ctx cycle =
        printfn "Start Julia discord client"
        let d = initClient()
        d.LoginAsync(TokenType.Bot, token).Wait()
        d.StartAsync().Wait()
        ctx.Mailbox.Self <! box JuliaMessages.InitFinish
        cycle d

      let inline postStop ctx cycle =
        unhandled()

      let inline preRestart ctx cause message cycle =
        unhandled()

      let inline postRestart ctx cause cycle =
        unhandled()

    let juliaActor (mb: Actor<_>) =
      let rec cycle dclient = actor {

        let! (msg: obj) = mb.Receive()

        let ctx = { State = dclient; Mailbox = mb }

        match msg with
        | LifecycleEvent le -> 
          match le with
          | PreStart                    -> return! LifecycleEvent.preStart    ctx cycle
          | PostStop                    -> return! LifecycleEvent.postStop    ctx cycle
          | PreRestart (cause, message) -> return! LifecycleEvent.preRestart  ctx cause message cycle
          | PostRestart cause           -> return! LifecycleEvent.postRestart ctx cause cycle
        | :? JuliaMessages as jm ->
          match jm with
          | JuliaMessages.ReciveMessage sm -> return! JuliaMessages.reciveMessage ctx sm cycle
          | JuliaMessages.Log l            -> return! JuliaMessages.log           ctx l cycle
          | JuliaMessages.InitFinish       -> return! JuliaMessages.initFinish    ctx cycle
          | JuliaMessages.JoinedGuild g    -> return! JuliaMessages.joinedGuild   ctx g cycle
        | some ->
          printfn "Ignored MSG: %A" some
          return! Unhandled
      }

      cycle null