namespace Julia.Core

open System

open Discord

open Discord.WebSocket

open Akka
open Akkling
open Akka.Actor

open System.Collections.Generic

open FSharp.UMX

[<NoComparison>]
type private SupervisorContext<'a> = {
  Mailbox: Actor<'a>
}

[<AutoOpen>]
module Operators =
  
  let inline (>=>) twoTrackInput switchFunction =
    match twoTrackInput with
    | Ok s -> switchFunction s
    | Error f -> Error f
  
  let inline (>>=) twoTrackInput switchFunction =
    match twoTrackInput with
    | Ok s -> Ok(switchFunction s)
    | Error f -> Error f

  //Unsafe operator return casted async 
  let inline (<??) (askFunc: 'b -> Async<obj>) (message: 'b): Async<'a> = async {
    let! result = askFunc message
    return unbox<'a> result
  }

  //Unsafe operator sync run async and return casted result
  let inline (<!?) (askFunc: 'b -> Async<obj>) (message: 'b): 'a = 
    message
    |> askFunc
    |> Async.RunSynchronously
    |> unbox<'a>

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

    let youtuber: string<actor_name> = % "youtuber"

    let getSystemPath (name: string<actor_name>) = sprintf "akka://%s/user/%s" %system %name

    let getSystemGuildPath (name: string<actor_name>) (id: uint64) = sprintf "akka://%s/user/%s%d" %system %name id

  let instance =

    let conf2 = Configuration.load()
    System.create %Names.system <| conf2

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

    module Discord =

      module GuildSystem =
        
        module Message =
        
          let inline songkeeper guildid (msg: GuildSystemMessage) =
            let path = Names.getSystemGuildPath Names.songkeeper guildid
            let songkeeper = select instance path
            songkeeper <! msg

          let inline guildActor guildid (msg: GuildSystemMessage) =
            let path = Names.getSystemGuildPath Names.guildActor guildid
            let guildActor = select instance path
            guildActor <! msg

          let inline bard guildid (msg: GuildSystemMessage) =
            let path = Names.getSystemGuildPath Names.bard guildid
            let bardActor = select instance path
            bardActor <! msg

          let inline youtuber guildid (msg: GuildSystemMessage) =
            let path = Names.getSystemGuildPath Names.youtuber guildid
            let youtuberActor = select instance path
            youtuberActor <! msg

          let inline julia (msg: GuildSystemMessage) =
            let path = Names.getSystemPath Names.client
            let julia = select instance path
            julia <! msg

        module Ask =
        
          let inline songkeeper guildid (ask: GuildSystemAsk) =
            let path = Names.getSystemGuildPath Names.songkeeper guildid
            let songkeeper = select instance path
            songkeeper <? ask

          let inline guildActor guildid (ask: GuildSystemAsk) =
            let path = Names.getSystemGuildPath Names.guildActor guildid
            let guildActor = select instance path
            guildActor <? ask

          let inline bard guildid (ask: GuildSystemAsk) =
            let path = Names.getSystemGuildPath Names.bard guildid
            let bardActor = select instance path
            bardActor  <? ask

          let inline youtuber guildid (ask: GuildSystemAsk) =
            let path = Names.getSystemGuildPath Names.youtuber guildid
            let youtuberActor = select instance path
            youtuberActor <? ask

          let inline julia (ask: GuildSystemAsk) =
            let path = Names.getSystemPath Names.client
            let julia = select instance path
            julia <? ask

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

        let inline youtuber guildid (msg: YoutuberMessages) =
          let path = Names.getSystemGuildPath Names.youtuber guildid
          let youtuberActor = select instance path
          youtuberActor <! msg

        let inline julia (msg: JuliaMessages) =
          let path = Names.getSystemPath Names.client
          let julia = select instance path
          julia <! msg

      module Ask =

        let inline songkeeper guildid (ask: SongkeeperAsk) =
          let path = Names.getSystemGuildPath Names.songkeeper guildid
          let songkeeper = select instance path
          songkeeper <? ask

  [<NoEquality>]
  [<NoComparison>]
  type GuildSystemMessageProxy = private {
    message: {|
      Songkeeper: GuildSystemMessage -> unit
      GuildActor: GuildSystemMessage -> unit
      Bard      : GuildSystemMessage -> unit
      Youtuber  : GuildSystemMessage -> unit
      Julia     : GuildSystemMessage -> unit
    |}
    ask:     {|
      Songkeeper: GuildSystemAsk -> Async<obj>
      GuildActor: GuildSystemAsk -> Async<obj>
      Bard      : GuildSystemAsk -> Async<obj>
      Youtuber  : GuildSystemAsk -> Async<obj>
      Julia     : GuildSystemAsk -> Async<obj>
    |}
  }
  with

    member self.Message = self.message
    member self.Ask     = self.ask

    static member internal Create (guild: SocketGuild) = {
      message = {|
        Songkeeper = Proxy.Discord.GuildSystem.Message.songkeeper guild.Id
        GuildActor = Proxy.Discord.GuildSystem.Message.guildActor guild.Id
        Bard       = Proxy.Discord.GuildSystem.Message.bard       guild.Id
        Youtuber   = Proxy.Discord.GuildSystem.Message.youtuber   guild.Id
        Julia      = Proxy.Discord.GuildSystem.Message.julia
        |}

      ask     = {|
        Songkeeper = Proxy.Discord.GuildSystem.Ask.songkeeper guild.Id
        GuildActor = Proxy.Discord.GuildSystem.Ask.guildActor guild.Id
        Bard       = Proxy.Discord.GuildSystem.Ask.bard       guild.Id
        Youtuber   = Proxy.Discord.GuildSystem.Ask.youtuber   guild.Id
        Julia      = Proxy.Discord.GuildSystem.Ask.julia
        |}
    }

  [<NoEquality>]
  [<NoComparison>]
  type ProxyDiscord = private {
    guildSystem: GuildSystemMessageProxy
    message: {|
      Songkeeper: SongkeeperMessages -> unit
      GuildActor: GuildActorMessages -> unit
      Bard      : BardMessages       -> unit
      Youtuber  : YoutuberMessages   -> unit
      Julia     : JuliaMessages      -> unit
    |}
    ask:     {|
      Songkeeper: SongkeeperAsk -> Async<obj>
    |}
  }
  with
  
    member self.Message     = self.message
    member self.Ask         = self.ask
  
    member self.GuildSystem = self.guildSystem

    static member Create (guild: SocketGuild) = {


      guildSystem = GuildSystemMessageProxy.Create guild

      message = {|
        Songkeeper = Proxy.Discord.Message.songkeeper guild.Id
        GuildActor = Proxy.Discord.Message.guildActor guild.Id
        Bard       = Proxy.Discord.Message.bard       guild.Id
        Youtuber   = Proxy.Discord.Message.youtuber   guild.Id
        Julia      = Proxy.Discord.Message.julia
      |}

      ask     = {|
        Songkeeper = Proxy.Discord.Ask.songkeeper guild.Id
      |}

    }

module Utils =

  let inline answerEmbed title desc = 
    EmbedBuilder()
      .WithTitle(title)
      .WithColor(Color.DarkPurple)
      .WithDescription(desc)
      .Build()

  let inline answerWithThumbnailEmbed title desc (url: string<thumbnail>) = 
    EmbedBuilder()
      .WithTitle(title)
      .WithColor(Color.DarkPurple)
      .WithDescription(desc)
      .WithThumbnailUrl(%url)
      .Build()

  let inline sendMessage (gmc: GuildMessageContext) message =
    gmc.Message.Channel.SendMessageAsync(message) |> ignore

  let inline sendEmbed (gmc: GuildMessageContext) embed =
    gmc.Message.Channel.SendMessageAsync(embed = embed) |> ignore
  
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