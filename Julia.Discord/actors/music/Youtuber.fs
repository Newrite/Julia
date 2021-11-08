namespace Julia.Discord

open System.Threading
open System.Collections.Generic

open Discord.WebSocket

open Akkling

open YoutubeExplode
open YoutubeExplode.Common


open FSharp.UMX

open Julia.Core

module Youtuber =

  [<NoComparison>]
  [<NoEquality>]
  type private YoutuberContext<'a> = {
    State:   YoutuberState
    Mailbox: Actor<'a>
    Guild: SocketGuild
    Proxy:   Sys.ProxyDiscord
  }

  let [<Literal>] private Parser = "Parser"

  module private RequestParser =

    let private songFromYoutubeAsyncMem (youtube: YoutubeClient) (url: string<url>) = 
    
      task {

        let urlUnwrap: string = %url
    
        let! video = youtube.Videos.GetAsync(urlUnwrap)

        return
          { Song.Url       = %video.Url
            Song.Title     = %video.Title
            Song.Time      = %video.Duration.Value
            Song.Thumbnail = %video.Thumbnails.[0].Url}
      }
    
      //Utils.memoize songFromYoutubeAsync
    
    let private videoFromYoutubeSearchAsyncMem (youtube: YoutubeClient) (message: string<url>) =
    
      task {

        let messageUnwrap: string = %message
    
        return! youtube.Search.GetVideosAsync(messageUnwrap).CollectAsync(1)
      }
    
      //Utils.memoize videoFromYoutubeSearchAsync
    
    
    let private videosFromYoutubePlaylistAsyncMem (youtube: YoutubeClient) (url: string<url>) =
    
      task {

        let urlUnwrap: string = %url
        
        return!  youtube.Playlists.GetVideosAsync(urlUnwrap).CollectAsync()
    
      }
    
      //Utils.memoize videosFromYoutubePlaylistAsync

    let private parseRequest (youtube: YoutubeClient) (guild: SocketGuild) (ym: YoutuberMessages) = async {

      let matchSocketMessage
        ( YoutuberMessages.Video (_, gmc)
        | YoutuberMessages.Search (_, gmc)
        | YoutuberMessages.Playlist (_, gmc) ) =
          gmc

      let sendSong (url: string<url>) = async {
        let song = songFromYoutubeAsyncMem youtube url
        song.Wait()
        BardMessages.Play (matchSocketMessage ym, song.Result)
        |> Sys.Proxy.Discord.Message.bard guild.Id
      }

      match ym with
      | YoutuberMessages.Playlist (req, _) ->
        let videos = videosFromYoutubePlaylistAsyncMem youtube req
        videos.Wait()
        for video in videos.Result do
        do! sendSong %video.Url
      | YoutuberMessages.Video (req, _) ->
        do! sendSong req 
      | YoutuberMessages.Search (req, gmc) ->
        let video = videoFromYoutubeSearchAsyncMem youtube req
        video.Wait()
        if video.Result.Count > 0 then
          Utils.sendEmbed gmc <| Utils.answerEmbed Parser $"Найдено:\n{video.Result.[0].Title}"
          do! sendSong %video.Result.[0].Url
        else
          Utils.sendEmbed gmc <| Utils.answerEmbed Parser "Ничего не удалось найти"
    }

    let initYoutuberState (guild: SocketGuild) =

      let cts = new CancellationTokenSource()

      let queueList = new List<YoutuberMessages>()

      let youtube = new YoutubeClient()

      let computation = async {

        while not cts.IsCancellationRequested do
          try
            match queueList.Count with
            | 0 ->
              do! Async.Sleep(1000)
            | _ ->
              do! parseRequest youtube guild queueList.[0]
              queueList.RemoveAt(0)
          with exn ->
            printfn "Exception: %s" exn.Message

      }

      Async.Start(computation, cts.Token)

      { Youtube      = youtube
        CancelToken  = cts
        RequestQueue = queueList }

  module private LifecycleEvent =

    let inline preStart (ctx: YoutuberContext<_>) cycle =
      printfn "Actor youtuber for guild %s start" ctx.Guild.Name
      cycle ctx.State

    let inline postStop (ctx: YoutuberContext<_>) cycle =
      printfn "PostStop"
      ignored()

    let inline preRestart (ctx: YoutuberContext<_>) cause message cycle =
      printfn "PreRestart cause: %A message: %A" cause message
      ignored()

    let inline postRestart (ctx: YoutuberContext<_>) cause cycle =
      printfn "PostRestart cause: %A" cause
      ignored()

  module private YoutuberMessages =
   
    let inline videoAndSearchAndPlaylist (ctx: YoutuberContext<_>) (ym: YoutuberMessages) cycle =
      ctx.State.RequestQueue.Add(ym)
      cycle ctx.State

  module private GuildSystemMessage =
    
    let inline restart (ctx: YoutuberContext<_>) gmc cycle =
      ctx.State.CancelToken.Cancel()
      ctx.State.RequestQueue.Clear()
      failwith "restart"

  module private GuildSystemAsk =
    
    let inline status (ctx: YoutuberContext<_>) gmc cycle =
      ctx.Mailbox.Sender() <! $"В очереди на обработку находится {ctx.State.RequestQueue.Count} запросов."
      cycle ctx.State

  let youtuberActor (guildProxy: Sys.ProxyDiscord) (guild: SocketGuild) (mb: Actor<_>) =
    let rec cycle youtuberState = actor {

      let! (msg: obj) = mb.Receive()

      let (ctx: YoutuberContext<_>) = { State = youtuberState; Mailbox = mb; Proxy = guildProxy; Guild = guild }

      match msg with
      | LifecycleEvent le -> 
        match le with
        | PreStart                    -> return! LifecycleEvent.preStart    ctx cycle
        | PostStop                    -> return! LifecycleEvent.postStop    ctx cycle
        | PreRestart (cause, message) -> return! LifecycleEvent.preRestart  ctx cause message cycle
        | PostRestart cause           -> return! LifecycleEvent.postRestart ctx cause cycle
      | :? YoutuberMessages as ym ->
        match ym with
        | YoutuberMessages.Video _ | YoutuberMessages.Search _ | YoutuberMessages.Playlist _
          -> return! YoutuberMessages.videoAndSearchAndPlaylist ctx ym cycle
      | :? GuildSystemMessage as gsm ->
        match gsm with
        | GuildSystemMessage.Restart gmc -> return! GuildSystemMessage.restart ctx gmc cycle
      | :? GuildSystemAsk as gsa ->
        match gsa with
        | GuildSystemAsk.Status gmc -> return! GuildSystemAsk.status ctx gmc cycle
      | _ -> return! Ignore

    }

    cycle <| RequestParser.initYoutuberState guild