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

    let inline preStart ctx cycle =
      printfn "Guild %s actor Songkeeper start" ctx.Guild.Name
      cycle ctx.State

    let inline postStop ctx cycle =
      unhandled()

    let inline preRestart ctx cause message cycle =
      unhandled()

    let inline postRestart ctx cause cycle =
      unhandled()

  module private SongkeeperMessages =

    let inline clearSongs ctx cycle =
      cycle []

    let inline addSong ctx song cycle =
      cycle <| ctx.State @ song

  module private SongkeeperAsk =

    let inline nextSong ctx cycle =
      match ctx.State with
      | [] as voidSong ->
        ctx.Mailbox.Sender() <! voidSong
        cycle ctx.State
      | head :: tail ->
        ctx.Mailbox.Sender() <! [ head ]
        cycle tail

    let inline availbeSongsTitle ctx cycle =
      ctx.Mailbox.Sender() <! [ for song in ctx.State do song.Title ]
      cycle ctx.State

  module private GuildSystemMessages =
    
    let inline restart ctx gmc cycle =
      failwith "restart"

  module private GuildSystemAsk =
    
    let inline status ctx gmc cycle =
      ctx.Mailbox.Sender() <! $"В хранилище содержится {ctx.State.Length} песен."
      cycle ctx.State
    
  let songkeeperActor guildProxy guild (mb: Actor<_>) =

    let rec cycle songs = actor {

      let! (msg: obj) = mb.Receive()

      let ctx = { State = songs; Mailbox = mb; Proxy = guildProxy; Guild = guild }

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
      | :? GuildSystemMessages as gsm ->
        match gsm with
        | GuildSystemMessages.Restart gmc -> return! GuildSystemMessages.restart ctx gmc cycle
      | :? GuildSystemAsk as gsa ->
        match gsa with
        | GuildSystemAsk.Status gmc -> return! GuildSystemAsk.status ctx gmc cycle
      | some ->
        printfn "Ignored MSG: %A" some
        return! Unhandled

    }

    cycle []