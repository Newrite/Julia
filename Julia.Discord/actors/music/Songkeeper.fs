namespace Julia.Discord

open Discord.WebSocket

open Akkling

open Julia.Core


module Songkeeper =

  [<NoComparison>]
  [<NoEquality>]
  type private SongkeeperContext<'a> = {
    State:   Song list
    Mailbox: Actor<'a>
    Guild: SocketGuild
    Proxy:   Sys.ProxyDiscord
  }

  module private LifecycleEvent =

    let inline preStart (ctx: SongkeeperContext<_>) cycle =
      printfn "Actor songkeeper: %s start" ctx.Mailbox.Self.Path.Name
      cycle ctx.State

    let inline postStop (ctx: SongkeeperContext<_>) cycle =
      printfn "PostStop"
      ignored()

    let inline preRestart (ctx: SongkeeperContext<_>) cause message cycle =
      printfn "PreRestart cause: %A message: %A" cause message
      ignored()

    let inline postRestart (ctx: SongkeeperContext<_>) cause cycle =
      printfn "PostRestart cause: %A" cause
      ignored()

  module private SongkeeperMessages =

    let inline clearSongs (ctx: SongkeeperContext<_>) cycle =
      cycle []

    let inline addSong (ctx: SongkeeperContext<_>) song cycle =
      cycle <| ctx.State @ song

  module private SongkeeperAsk =

    let inline nextSong (ctx: SongkeeperContext<_>) cycle =
      match ctx.State with
      | [] as voidSong ->
        ctx.Mailbox.Sender() <! voidSong
        cycle ctx.State
      | head :: tail ->
        ctx.Mailbox.Sender() <! [ head ]
        cycle tail

    let inline availbeSongsTitle (ctx: SongkeeperContext<_>) cycle =
      ctx.Mailbox.Sender() <! [ for song in ctx.State do song.Title ]
      cycle ctx.State

  module private GuildSystemMessage =
    
    let inline restart (ctx: SongkeeperContext<_>) gmc cycle =
      failwith "restart"

  module private GuildSystemAsk =
    
    let inline status (ctx: SongkeeperContext<_>) gmc cycle =
      ctx.Mailbox.Sender() <! $"В хранилище содержится {ctx.State.Length} песен."
      cycle ctx.State
    
  let songkeeperActor (guildProxy: Sys.ProxyDiscord) (guild: SocketGuild) (mb: Actor<_>) =
    let rec cycle songs = actor {

      let! (msg: obj) = mb.Receive()

      let ctx: SongkeeperContext<_> = { State = songs; Mailbox = mb; Proxy = guildProxy; Guild = guild }

      match msg with
      | LifecycleEvent le -> 
        match le with
        | PreStart                    -> return! LifecycleEvent.preStart    ctx cycle
        | PostStop                    -> return! LifecycleEvent.postStop    ctx cycle
        | PreRestart (cause, message) -> return! LifecycleEvent.preRestart  ctx cause message cycle
        | PostRestart cause           -> return! LifecycleEvent.postRestart ctx cause cycle
      | :? SongkeeperMessages as skm ->
        match skm with
        | SongkeeperMessages.ClearSongs   -> return! SongkeeperMessages.clearSongs ctx cycle
        | SongkeeperMessages.AddSong song -> return! SongkeeperMessages.addSong    ctx [ song ] cycle
      | :? SongkeeperAsk as ska ->
        match ska with
        | SongkeeperAsk.NextSong          -> return! SongkeeperAsk.nextSong          ctx cycle
        | SongkeeperAsk.AvaibleSongsTitle -> return! SongkeeperAsk.availbeSongsTitle ctx cycle
      | :? GuildSystemMessage as gsm ->
        match gsm with
        | GuildSystemMessage.Restart gmc -> return! GuildSystemMessage.restart ctx gmc cycle
      | :? GuildSystemAsk as gsa ->
        match gsa with
        | GuildSystemAsk.Status gmc -> return! GuildSystemAsk.status ctx gmc cycle
      | _ -> return! Ignore

    }

    cycle []