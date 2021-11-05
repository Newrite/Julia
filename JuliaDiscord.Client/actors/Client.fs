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
open JuliaDiscord.Core

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

  module Julia =

    module private JuliaMessages =

      let private createGuildActors (guild: SocketGuild) =

        let actorGuildName: string<actor_name> = % sprintf "%s%d" %Sys.Names.guildActor guild.Id
        let actorGuildFunc = GuildActor.guildActor <| Sys.Proxy.create guild
        Sys.supervisor <! SupervisorMessages.CreateSystemActor(actorGuildFunc, actorGuildName)

        let actorBardName: string<actor_name> = % sprintf "%s%d" %Sys.Names.bard guild.Id
        let actorBardFunc = Bard.bardActor <| Sys.Proxy.create guild
        Sys.supervisor <! SupervisorMessages.CreateSystemActor(actorBardFunc, actorBardName)

        let actorSongkeeperName: string<actor_name> = % sprintf "%s%d" %Sys.Names.songkeeper guild.Id
        let actorSongkeeperFunc = Songkeeper.songkeeperActor <| Sys.Proxy.create guild
        Sys.supervisor <! SupervisorMessages.CreateSystemActor(actorSongkeeperFunc, actorSongkeeperName)

        let actorYoutuberName: string<actor_name> = % sprintf "%s%d" %Sys.Names.youtuber guild.Id
        let actorYoutuberFunc = Youtuber.youtuberActor <| Sys.Proxy.create guild
        Sys.supervisor <! SupervisorMessages.CreateSystemActor(actorYoutuberFunc, actorYoutuberName)

      let inline reciveMessage (ctx: JuliaContext<_>) (sm: SocketMessage) cycle =
        printfn "Recive: A: %s M: %s" sm.Author.Username sm.Content

        match sm.Author with
        | :? SocketGuildUser as user ->
          let context =
            { Client  = ctx.State
              Guild   = user.Guild 
              Message = sm }
          GuildActorMessages.GuildMessage context
          |> Sys.Proxy.Message.guildActor context.Guild.Id
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
        | _ as some ->
          printfn "Ignored MSG: %A" some
          return! Ignore
      }

      cycle null