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
    WriteProxy: GuildWriter.WriteProxy
  }

  let [<Literal>] private Parser = "Parser"

  module private RequestParser =

    let private songFromYoutubeAsyncMem (youtube: YoutubeClient) url = 
    
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
    
    let private videoFromYoutubeSearchAsyncMem (youtube: YoutubeClient) message =
    
      task {

        let messageUnwrap: string = %message
    
        return! youtube.Search.GetVideosAsync(messageUnwrap).CollectAsync(1)
      }
    
      //Utils.memoize videoFromYoutubeSearchAsync
    
    
    let private videosFromYoutubePlaylistAsyncMem (youtube: YoutubeClient) url =
    
      task {

        let urlUnwrap: string = %url
        
        return!  youtube.Playlists.GetVideosAsync(urlUnwrap).CollectAsync()
    
      }
    
      //Utils.memoize videosFromYoutubePlaylistAsync

    let private parseRequest youtube (guild: SocketGuild) ym (wp: GuildWriter.WriteProxy) = async {

      let matchSocketMessage
        ( YoutuberMessages.Video (_, gmc)
        | YoutuberMessages.Search (_, gmc)
        | YoutuberMessages.Playlist (_, gmc) ) =
          gmc

      let sendSong (url: URL) = async {
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
          wp.WriteParsed(gmc, Utils.answerEmbed %Parser %($"Найдено:\n{video.Result.[0].Title}"), %video.Result.[0].Url)
          do! sendSong %video.Result.[0].Url
        else
          wp.WriteEmbedMessage(gmc, Utils.answerEmbed %Parser %"Ничего не удалось найти")
    }

    let initYoutuberState guild wp =

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
              do! parseRequest youtube guild queueList.[0] wp
              queueList.RemoveAt(0)
          with exn ->
            printfn "Exception: %s" exn.Message

      }

      Async.Start(computation, cts.Token)

      { Youtube      = youtube
        CancelToken  = cts
        RequestQueue = queueList }

  module private LifecycleEvent =

    let inline preStart ctx cycle =
      printfn "Guild %s actor Youtuber start" ctx.Guild.Name
      cycle ctx.State

    let inline postStop ctx cycle =
      unhandled()

    let inline preRestart ctx cause message cycle =
      unhandled()

    let inline postRestart ctx cause cycle =
      unhandled()

  module private YoutuberMessages =
   
    let inline videoAndSearchAndPlaylist ctx ym cycle =
      ctx.State.RequestQueue.Add(ym)
      cycle ctx.State

  module private GuildSystemMessages =
    
    let inline restart ctx gmc cycle =
      ctx.State.CancelToken.Cancel()
      ctx.State.RequestQueue.Clear()
      failwith "restart"

  module private GuildSystemAsk =
    
    let inline status ctx gmc cycle =
      ctx.Mailbox.Sender() <! $"В очереди на обработку находится {ctx.State.RequestQueue.Count} запросов."
      cycle ctx.State

  let youtuberActor guildProxy guild (mb: Actor<_>) =
    
    let writeProxy = GuildWriter.WriteProxy.Create guild

    let rec cycle youtuberState = actor {

      let! (msg: obj) = mb.Receive()

      let ctx =
        { State = youtuberState; Mailbox = mb; Proxy = guildProxy; Guild = guild; WriteProxy = writeProxy }

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

    cycle <| RequestParser.initYoutuberState guild writeProxy