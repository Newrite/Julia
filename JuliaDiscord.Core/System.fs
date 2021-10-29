namespace JuliaDiscord.Core

open System

open Discord
open Discord.Net
open Discord.WebSocket

open Akka
open Akkling
open Akka.Actor
open Akkling.Actors

open YoutubeExplode.Videos.Streams

open System.Diagnostics
open System.Collections.Generic

open FSharp.UMX

module private SuperVisorMessages =
  
  let inline createActor actorFunc actorName (ctx: SupervisorContext<_>) cycleFunc =
    spawn ctx.Mailbox %actorName <| props actorFunc |> ignore
    cycleFunc()
    
  let inline createSupervisorActor actorFunc actorName visorSrategy (ctx: SupervisorContext<_>) cycleFunc =
    spawn ctx.Mailbox %actorName
      <| { props actorFunc with
            SupervisionStrategy = Some(visorSrategy()) } |> ignore
    cycleFunc()

  let inline createSystemActor actorFunc actorName system cycleFunc =
    spawn system %actorName <| props actorFunc |> ignore
    cycleFunc()
    
  let inline createSystemSupervisorActor actorFunc actorName visorSrategy system cycleFunc =
    spawn system %actorName
      <| { props actorFunc with
            SupervisionStrategy = Some(visorSrategy()) } |> ignore
    cycleFunc()

  let inline actorMessage actorName cmd (ctx: SupervisorContext<_>) cycleFunc =
    let actor = select ctx.Mailbox %actorName
    actor <! cmd
    cycleFunc()

  let inline getActor actorName (ctx: SupervisorContext<_>) cycleFunc =
    let actor = select ctx.Mailbox %actorName
    ctx.Mailbox.Sender() <! actor
    cycleFunc()

module Sys =

  module Names =

    let system: string<actor_name> = % "discord-bot"

    let client: string<actor_name> = % "client"

    let supervisor: string<actor_name> = % "supervisor"

    let datakeeper: string<actor_name> = % "datakeeper"

    let bard: string<actor_name> = % "bard"

    let songkeeper: string<actor_name> = % "songkeeper"

    let guildActor: string<actor_name> = % "guildactor"

    let getSystemPath (name: string<actor_name>) = sprintf "akka://%s/user/%s" %system %name

    let getSystemGuildPath (name: string<actor_name>) (id: uint64) = sprintf "akka://%s/user/%s%d" %system %name id

  let instance =
    System.create %Names.system <| Configuration.defaultConfig()

  let private supervisorStrategy() =
    Strategy.OneForOne(
      (fun ex ->
        printfn "Invoking supervision strategy"

        match ex with
        | _ -> Directive.Restart),
      -1,
      TimeSpan.FromSeconds(5.)
    )

  let private supervisorActor (mailbox: Actor<_>) =
    let rec cycle() = actor {

      let! message = mailbox.Receive()

      let ctx = { Mailbox = mailbox }

      match message with
      | SupervisorMessages.CreateActor (actorFunc, actorName) ->
        return! SuperVisorMessages.createActor actorFunc actorName ctx cycle

      | SupervisorMessages.CreateSystemActor (actorFunc, actorName) ->
        return! SuperVisorMessages.createSystemActor actorFunc actorName instance cycle

      | SupervisorMessages.CreateSupervisorActor (actorFunc, actorName, visorSrategy) ->
        return! SuperVisorMessages.createSupervisorActor actorFunc actorName visorSrategy ctx cycle

      | SupervisorMessages.CreateSystemSupervisorActor (actorFunc, actorName, visorSrategy) ->
        return! SuperVisorMessages.createSystemSupervisorActor actorFunc actorName visorSrategy instance cycle

      | SupervisorMessages.ActorMessage (actorName, cmd) ->
        return! SuperVisorMessages.actorMessage actorName cmd ctx cycle

      | SupervisorMessages.GetActor actorName ->
        return! SuperVisorMessages.getActor actorName ctx cycle

    }

    cycle ()

  let supervisor: IActorRef<SupervisorMessages<obj>> =
    spawn instance %Names.supervisor
    <| { props supervisorActor with
           SupervisionStrategy = Some(supervisorStrategy()) }

  module Proxy =

    module Message =

      let inline songkeeper guildid (msg: SongkeeperMessages) =
        let path = Names.getSystemGuildPath Names.songkeeper guildid
        let songkeeper = select instance path
        songkeeper <! msg

      let inline guildActor guildid (msg: GuildActorMessages) =
        let path = Names.getSystemGuildPath Names.guildActor guildid
        let guildActor = select instance path
        guildActor <! msg

      let inline bard guildid (msg: BardMessages) =
        let path = Names.getSystemGuildPath Names.bard guildid
        let bardActor = select instance path
        bardActor <! msg

      let inline julia (msg: JuliaMessages) =
        let path = Names.getSystemPath Names.client
        let julia = select instance path
        julia <! msg

    module Ask =

      let inline songkeeper guildid (ask: SongkeeperAsk) =
        let path = Names.getSystemGuildPath Names.songkeeper guildid
        let songkeeper = select instance path
        songkeeper <? ask

module Utils =

  let inline answerEmbed title desc = 
    EmbedBuilder()
      .WithTitle(title)
      .WithColor(Color.DarkPurple)
      .WithDescription(desc)
      .Build()

  let inline sendMessage (sm: SocketMessage) message =
    sm.Channel.SendMessageAsync(message) |> ignore

  let inline sendEmbed (sm: SocketMessage) embed =
    sm.Channel.SendMessageAsync(embed = embed) |> ignore
  
  let memoize (f: 'a -> 'b) =
    let dict = Dictionary<'a, 'b>()

    let memoizedFunc (input: 'a) =

      match dict.TryGetValue(input) with
      | true, x -> x
      | false, _ ->
          let answer = f input

          dict.TryAdd(input, answer)
          |> ignore

          answer

    memoizedFunc