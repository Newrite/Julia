namespace JuliaDiscord.Client

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open System.Diagnostics
open System.Collections.Generic
open System.Collections.Concurrent

open Discord
open Discord.Net
open Discord.Audio
open Discord.WebSocket

open Akka
open Akkling
open Akka.Actor
open Akkling.Actors

open YoutubeExplode
open YoutubeExplode.Common
open YoutubeExplode.Videos
open YoutubeExplode.Videos.Streams

open FSharp.UMX

open JuliaDiscord.Core


module Songkeeper =

  module private LifecycleEvent =

    let inline preStart (ctx: SongkeeperContext<_,_>) cycle =
      printfn "Actor songkeeper: %s start" ctx.Mailbox.Self.Path.Name
      cycle ctx.State

    let inline postStop (ctx: SongkeeperContext<_,_>) cycle =
      printfn "PostStop"
      ignored()

    let inline preRestart (ctx: SongkeeperContext<_,_>) cause message cycle =
      printfn "PreRestart cause: %A message: %A" cause message
      ignored()

    let inline postRestart (ctx: SongkeeperContext<_,_>) cause cycle =
      printfn "PostRestart cause: %A" cause
      ignored()

  module private SongkeeperMessages =

    let inline clearSongs (ctx: SongkeeperContext<_,_>) cycle =
      cycle []

    let inline addSong (ctx: SongkeeperContext<_,_>) song cycle =
      cycle <| ctx.State @ song

  module private SongkeeperAsk =

    let inline nextSong (ctx: SongkeeperContext<_,_>) cycle =
      match ctx.State with
      | [] as voidSong ->
        ctx.Mailbox.Sender() <! voidSong
        cycle ctx.State
      | head :: tail ->
        ctx.Mailbox.Sender() <! [ head ]
        cycle tail

    let inline availbeSongsTitle (ctx: SongkeeperContext<_,_>) cycle =
      ctx.Mailbox.Sender() <! [ for song in ctx.State do song.Title ]
      cycle ctx.State

  module private GuildSystemMessage =
    
    let inline restart (ctx: SongkeeperContext<_,_>) gmc cycle =
      failwith "restart"

  module private GuildSystemAsk =
    
    let inline status (ctx: SongkeeperContext<_,_>) gmc cycle =
      ctx.Mailbox.Sender() <! $"В хранилище содержится {ctx.State.Length} песен."
      cycle ctx.State
    
  let songkeeperActor (guildProxy: GuildProxy<_>) (mb: Actor<_>) =
    let rec cycle songs = actor {

      let! (msg: obj) = mb.Receive()

      let ctx: SongkeeperContext<_,_> = { State = songs; Mailbox = mb; Proxy = guildProxy }

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