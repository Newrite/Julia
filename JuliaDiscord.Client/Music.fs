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

module SongKeeper =

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
    
  let songKeeperActor (mb: Actor<_>) =
    let rec cycle songs = actor {

      let! (msg: obj) = mb.Receive()

      let ctx: SongkeeperContext<_> = { State = songs; Mailbox = mb }

      match msg with
      | LifecycleEvent le -> 
        match le with
        | PreStart                    -> return! LifecycleEvent.preStart ctx cycle
        | PostStop                    -> return! LifecycleEvent.postStop ctx cycle
        | PreRestart (cause, message) -> return! LifecycleEvent.preRestart ctx cause message cycle
        | PostRestart cause           -> return! LifecycleEvent.postRestart ctx cause cycle
      | :? SongkeeperMessages as skm ->
        match skm with
        | SongkeeperMessages.ClearSongs   -> return! SongkeeperMessages.clearSongs ctx cycle
        | SongkeeperMessages.AddSong song -> return! SongkeeperMessages.addSong ctx [ song ] cycle
        | SongkeeperMessages.Restart      -> failwith "Restart songkeeper"
      | :? SongkeeperAsk as ska ->
        match ska with
        | SongkeeperAsk.NextSong          -> return! SongkeeperAsk.nextSong ctx cycle
        | SongkeeperAsk.AvaibleSongsTitle -> return! SongkeeperAsk.availbeSongsTitle ctx cycle
      | _ -> return! Ignore

    }

    cycle []

module Bard =

  let [<Literal>] private Play = "Play"
  let [<Literal>] private Stop = "Stop"
  let [<Literal>] private Join = "Join"
  let [<Literal>] private Loop = "Loop"
  let [<Literal>] private Leave = "Leave"
  let [<Literal>] private Resume = "Resume"
  let [<Literal>] private Query = "Query"
  let [<Literal>] private Clear = "Clear"
  let [<Literal>] private Nowplay = "Nowplay"
  let [<Literal>] private Skip = "Skip"

  let commands = [ 
    {| command = Play
       message = BardMessages.ParseRequest
       desc    = "Принимает ссылку на плейлист с ютуба или на видео с ютуба для воспроизведения аудио"|}
    {| command = Stop
       message = BardMessages.Stop
       desc    = "Останавливает воспроизведение текущей песни" + 
                 " сохраняя песню которая играла что бы продолжить с нее после play или resume" |}
    {| command = Join
       message = BardMessages.Join
       desc    = "Подключается к голосовому каналу в котором находится пользователь"|}
    {| command = Leave
       message = BardMessages.Leave
       desc    = "Покидает голосовой канал в котором находится пользователь"|}
    {| command = Loop
       message = BardMessages.Loop
       desc    = "Зацикливает текущий трек"|}
    {| command = Resume
       message = BardMessages.Resume
       desc    = "Возобновляет воспроизведение остановленной песни, воспроизведение начинается с начала"|}
    {| command = Query
       message = BardMessages.Query
       desc    = "Показывает очередь песен"|}
    {| command = Clear
       message = BardMessages.Clear
       desc    = "Очищает очередь песен"|}
    {| command = Nowplay
       message = BardMessages.NowPlay
       desc    = "Показывает название текущей песни"|}
    {| command = Skip
       message = BardMessages.Skip
       desc    = "Скипает текущую песню"|}
  ]

  let private audioClients = ConcurrentDictionary<uint64, IAudioClient>()

  let private ffmpegpath =
    @"C:\Users\nirn2\Downloads\ffmpeg-N-104348-gbb10f8d802-win64-gpl\bin\ffmpeg.exe"

  let private createFFmpegProcess() =

    let info =
      ProcessStartInfo(
        FileName = ffmpegpath,
        Arguments = $"-hide_banner -loglevel verbose -i - -vn -ac 2 -vn -f s16le -ar 48000 -",
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardInput = true
      )

    Process.Start(info)

  let private songFromYoutubeAsyncMem =

    let songFromYoutubeAsync (url: string) = task {

      let youtube = new YoutubeClient()

      let! streamManifest = youtube.Videos.Streams.GetManifestAsync(url)

      let! video = youtube.Videos.GetAsync(url)

      let streamInfo =
        let a =
          (streamManifest.GetAudioOnlyStreams() :?> IEnumerable<IStreamInfo>)

        a.GetWithHighestBitrate()

      return
        { Song.StreamInfo = streamInfo
          Song.Title = video.Title }
    }

    Utils.memoize songFromYoutubeAsync

  let private videoFromYoutubeSearchAsyncMem =

    let videoFromYoutubeSearchAsync (message: string) = task {

      let youtube = new YoutubeClient()

      return! youtube.Search.GetVideosAsync(message).CollectAsync(1)
    }

    Utils.memoize videoFromYoutubeSearchAsync


  let private videosFromYoutubePlaylistAsyncMem =

    let videosFromYoutubePlaylistAsync (url: string) = task {
      
        let youtube = new YoutubeClient()
        
        return!  youtube.Playlists.GetVideosAsync(url).CollectAsync()

      }

    Utils.memoize videosFromYoutubePlaylistAsync

  let private validateChannel (sm: SocketMessage) =
    match sm.Author with
    | :? SocketGuildUser as user ->
      if user.VoiceChannel <> null then
        Ok user
      else
        Result.Error BardErrros.UserNoInVoiceChannel
    | _ -> Result.Error BardErrros.IsNoGuildMessage
    
  let private connectChannel (user: SocketGuildUser) (sm: SocketMessage) (guild: SocketGuild) =
    match guild.CurrentUser.VoiceChannel with
    | null ->
      let client = user.VoiceChannel.ConnectAsync().Result
      audioClients.TryAdd(guild.Id, client) |> ignore
    | channel when channel = user.VoiceChannel ->
      if not (audioClients.ContainsKey(guild.Id)) then
        let client = user.VoiceChannel.ConnectAsync().Result
        audioClients.TryAdd(guild.Id, client) |> ignore
    | _ ->
      let client = user.VoiceChannel.ConnectAsync().Result
      audioClients.TryAdd(guild.Id, client) |> ignore

  let private translateStream (song: Song) (ffmpeg: Process) (ctx: BardContext<_>) (sm: SocketMessage) = task {
    use discord =
      audioClients[ctx.State.Guild.Id]
        .CreatePCMStream(AudioApplication.Music)

    let youtube = YoutubeClient()
    let stream = youtube.Videos.Streams.GetAsync(song.StreamInfo)

    use input = ffmpeg.StandardInput.BaseStream
    use output = ffmpeg.StandardOutput.BaseStream

    output.CopyToAsync(discord) |> ignore
    let streamDiscord = stream.Result.CopyToAsync(input)

    while not streamDiscord.IsCompleted && not ffmpeg.HasExited do
      do! Async.Sleep(3000)

    printfn "StreamState: %b" streamDiscord.IsCompleted
    printfn "FFmpeg exited: %b" ffmpeg.HasExited

    stream.Result.Dispose()

    BardMessages.Final sm
    |> Sys.Proxy.Message.bard ctx.State.Guild.Id
  }

  let private startPlaySong song (ctx: BardContext<_>) (sm: SocketMessage) =
    match validateChannel sm with
    | Ok user ->
      connectChannel user sm ctx.State.Guild
      let ffmpeg = createFFmpegProcess()
      Utils.sendEmbed sm <| Utils.answerEmbed Play $"Начинается воспроизведение:\n{song.Title}"
      let bp =
        let s = { FFmpeg = ffmpeg; Song = song }
        match ctx.State.PlayerState with
        | PlayerState.Loop b -> PlayerState.Loop s
        | _ -> PlayerState.Playing s
      let newState = { ctx.State with PlayerState = bp }
      translateStream song ffmpeg ctx sm |> ignore
      newState
    | Error err ->
      Utils.sendEmbed sm <| Utils.answerEmbed Play $"Ошибка воспроизведения: {err.ToString()}"
      ctx.State


  module private LifecycleEvent =

    let inline preStart (ctx: BardContext<_>) cycle =
      printfn "Actor bard for guild %s start" ctx.State.Guild.Name
      cycle ctx.State

    let inline postStop (ctx: BardContext<_>) cycle =
      printfn "PostStop"
      ignored()

    let inline preRestart (ctx: BardContext<_>) cause message cycle =
      printfn "PreRestart cause: %A message: %A" cause message
      ignored()

    let inline postRestart (ctx: BardContext<_>) cause cycle =
      printfn "PostRestart cause: %A" cause
      ignored()

  module private BardMessages =
    
    let inline parseRequest (ctx: BardContext<_>) (sm: SocketMessage) = task {

      let (|Video|Playlist|Message|NoArg|) (sm: SocketMessage) =
        
        let msgArray = sm.Content.Split(' ')
        if msgArray.Length < 2 then
          NoArg
        else
          if msgArray.[1].Contains("https://youtu") && msgArray.[1].Contains("playlist") then
            Playlist
          elif msgArray.[1].Contains("https://youtu") then
            Video
          else
            Message

      let sendSong url = task {
        let! song = songFromYoutubeAsyncMem url
        ctx.Mailbox.Self <! box (BardMessages.Play (sm, song))
      }

      let msgArray = sm.Content.Split(' ')

      match sm with
      | NoArg ->
        Utils.sendEmbed sm <| Utils.answerEmbed Play "Недостаточно аргументов, вы забыли ссылку?"
      | Playlist ->
        let! videos = videosFromYoutubePlaylistAsyncMem msgArray.[1]
        for video in videos do
        do! sendSong video.Url
      | Video ->
        do! sendSong msgArray.[1]
      | Message ->
        let allExceptFirst =
          msgArray
          |> Seq.mapi (fun i s -> if i = 0 then "" else s)
          |> Seq.reduce (fun acc elem -> acc + elem)
        let! video = videoFromYoutubeSearchAsyncMem allExceptFirst
        if video.Count > 0 then
          Utils.sendEmbed sm <| Utils.answerEmbed Play $"Найдено:\n{video.[0].Title}"
          do! sendSong video.[0].Url
        else
          Utils.sendEmbed sm <| Utils.answerEmbed Play "Ничего не удалось найти"
    }

    let inline join (ctx: BardContext<_>) sm cycle =
      match validateChannel sm with
      | Ok user ->
        connectChannel user sm ctx.State.Guild
        Utils.sendEmbed sm <| Utils.answerEmbed Join $"Запрыгнула в канал {user.VoiceChannel.Name}"
      | Error err ->
        Utils.sendEmbed sm <| Utils.answerEmbed Join $"Не удалось зайти в канал {err.ToString()}"
      cycle ctx.State

    let inline leave (ctx: BardContext<_>) sm cycle =
      match ctx.State.PlayerState with
      | PlayerState.Playing _ | PlayerState.Loop _ ->
        Utils.sendEmbed sm <| Utils.answerEmbed Leave "Сейчас идет воспроизведение, я не могу покинуть канал"
      | PlayerState.StopPlaying _ | PlayerState.WaitSong ->
        match ctx.State.Guild.CurrentUser.VoiceChannel with
        | null ->
          Utils.sendEmbed sm <| Utils.answerEmbed Leave "Я и так не в голосовом канале"
        | channel ->
          channel.DisconnectAsync() |> ignore
          Utils.sendEmbed sm <| Utils.answerEmbed Leave "Покинула голосовой канал"
      cycle ctx.State

    let inline play (ctx: BardContext<_>) sm (song: Song) cycle =
      
      let inline addToQuery songToQuery =
        Utils.sendEmbed sm <| Utils.answerEmbed Play $"Добавлено в очередь:\n{songToQuery.Title}"
        SongkeeperMessages.AddSong songToQuery
        |> Sys.Proxy.Message.songkeeper ctx.State.Guild.Id

      match ctx.State.PlayerState with
      | PlayerState.Playing _ | PlayerState.Loop _ ->
        addToQuery song
        cycle ctx.State
      | PlayerState.WaitSong ->
        cycle <| startPlaySong song ctx sm
      | PlayerState.StopPlaying s ->
        let newState = startPlaySong song ctx sm
        addToQuery song
        cycle newState

    let inline resume (ctx: BardContext<_>) sm cycle =

      match ctx.State.PlayerState with
      | PlayerState.Playing _ | PlayerState.Loop _ ->
        Utils.sendEmbed sm <| Utils.answerEmbed Resume "Воспроизведение уже идет"
        cycle ctx.State
      | PlayerState.WaitSong ->
        Utils.sendEmbed sm <| Utils.answerEmbed Resume "Нет остановленных песен"
        cycle ctx.State
      | PlayerState.StopPlaying s ->
        Utils.sendEmbed sm <| Utils.answerEmbed Resume $"Возобновляется воспроизведение остановленной песни:\n{s.Title}"
        let newState = startPlaySong s ctx sm
        cycle newState

    let inline skip (ctx: BardContext<_>) sm cycle =

      match ctx.State.PlayerState with
      | PlayerState.Playing bp ->
        bp.FFmpeg.Kill()
        Utils.sendEmbed sm <| Utils.answerEmbed Skip $"Скипнута песня:\n{bp.Song.Title}"
      | PlayerState.Loop bp ->
        bp.FFmpeg.Kill()
        Utils.sendEmbed sm <| Utils.answerEmbed Skip $"Скипнута песня, но ее воспроизведенеи продолжится потому что она зациклена:\n{bp.Song.Title}"
      | PlayerState.StopPlaying _ | PlayerState.WaitSong ->
        Utils.sendEmbed sm <| Utils.answerEmbed Skip "Сейчас ничего не играет"

      cycle ctx.State

    let inline query (ctx: BardContext<_>) sm cycle =

      let songsTitle: string list =
        Sys.Proxy.Ask.songkeeper ctx.State.Guild.Id SongkeeperAsk.AvaibleSongsTitle
        |> Async.RunSynchronously

      if songsTitle.Length = 0 then
        Utils.sendEmbed sm <| Utils.answerEmbed Query "Очередь пуста"
      else
        let mutable index = 1
        songsTitle
        |> List.chunkBySize 10
        |> List.iter (fun slist ->
          let mutable query = ""
          for title in slist do
            let str = sprintf "%d. %s\n----------\n" index title
            index <- index + 1
            query <- query + str
          Thread.Sleep(1000)
          Utils.sendEmbed sm <| Utils.answerEmbed Query query)

      cycle ctx.State

    let inline stop (ctx: BardContext<_>) sm cycle =

      match ctx.State.PlayerState with
      | PlayerState.Playing bp ->
        bp.FFmpeg.Kill()
        Utils.sendEmbed sm <| Utils.answerEmbed Stop $"Песня остановлена:\n{bp.Song.Title}"
        cycle { ctx.State with PlayerState = PlayerState.StopPlaying bp.Song }
      | PlayerState.Loop bp ->
        bp.FFmpeg.Kill()
        Utils.sendEmbed sm <| Utils.answerEmbed Stop $"Песня остановлена:\n{bp.Song.Title}"
        cycle { ctx.State with PlayerState = PlayerState.StopPlaying bp.Song }
      | PlayerState.StopPlaying song ->
        Utils.sendEmbed sm <| Utils.answerEmbed Stop $"Воспроизведение уже остановлено, песня:\n{song.Title}"
        cycle ctx.State
      | PlayerState.WaitSong ->
        Utils.sendEmbed sm <| Utils.answerEmbed Stop "Ожидаются песни для воспроизведения"
        cycle ctx.State

    let inline loop (ctx: BardContext<_>) sm cycle =

      match ctx.State.PlayerState with
      | PlayerState.Playing bp ->
        Utils.sendEmbed sm <| Utils.answerEmbed Loop $"Песня зациклена:\n{bp.Song.Title}"
        cycle { ctx.State with PlayerState = PlayerState.Loop bp }
      | PlayerState.Loop bp ->
        Utils.sendEmbed sm <| Utils.answerEmbed Loop "Песня уже зациклена"
        cycle ctx.State
      | PlayerState.StopPlaying song ->
        Utils.sendEmbed sm <| Utils.answerEmbed Loop $"Воспроизведение остановлено, песня:\n{song.Title}"
        cycle ctx.State
      | PlayerState.WaitSong ->
        Utils.sendEmbed sm <| Utils.answerEmbed Loop "Ожидаются песни для воспроизведения"
        cycle ctx.State

    let inline clear (ctx: BardContext<_>) sm cycle =

      Sys.Proxy.Message.songkeeper ctx.State.Guild.Id SongkeeperMessages.ClearSongs

      Utils.sendEmbed sm <| Utils.answerEmbed Clear "Очередь очищена"

      cycle ctx.State

    let inline nowPlay (ctx: BardContext<_>) (sm: SocketMessage) cycle =

      match ctx.State.PlayerState with
      | PlayerState.Playing bp ->
        Utils.sendEmbed sm <| Utils.answerEmbed Nowplay $"Сейчас играет:\n{bp.Song.Title}"
      | PlayerState.Loop bp ->
        Utils.sendEmbed sm <| Utils.answerEmbed Nowplay $"Сейчас играет и зациклена:\n{bp.Song.Title}"
      | PlayerState.StopPlaying song ->
        Utils.sendEmbed sm <| Utils.answerEmbed Nowplay $"Остановленная песня:\n{song.Title}"
      | PlayerState.WaitSong ->
        Utils.sendEmbed sm <| Utils.answerEmbed Nowplay "Нет текущих песен"

      cycle ctx.State

    let inline final (ctx: BardContext<_>) sm cycle =

      match ctx.State.PlayerState with
      | PlayerState.Playing bp ->
        bp.FFmpeg.Kill()
        let song: Song list =
          Sys.Proxy.Ask.songkeeper ctx.State.Guild.Id SongkeeperAsk.NextSong
          |> Async.RunSynchronously
        match song with
        | head::_ ->
            cycle <| startPlaySong head ctx sm
        | _ ->
          Utils.sendEmbed sm <| Utils.answerEmbed Play "Очередь закончилась"
          cycle { ctx.State with PlayerState = PlayerState.WaitSong }
      | PlayerState.Loop bp ->
        bp.FFmpeg.Kill()
        cycle <| startPlaySong bp.Song ctx sm
      | PlayerState.StopPlaying _ | PlayerState.WaitSong ->
        cycle ctx.State

    let inline restart (ctx: BardContext<_>) sm cycle =
      
      audioClients.TryRemove(ctx.State.Guild.Id) |> ignore

      match ctx.State.PlayerState with
      | PlayerState.Playing bp | PlayerState.Loop bp ->
        bp.FFmpeg.Kill()
        failwith "restart"
      | PlayerState.StopPlaying _ | PlayerState.WaitSong ->
        failwith "restart"
        

  let bardActor guild (mb: Actor<_>) =
    let rec cycle bardState = actor {

      let! (msg: obj) = mb.Receive()

      let (ctx: BardContext<_>) = { State = bardState; Mailbox = mb }

      match msg with
      | LifecycleEvent le -> 
        match le with
        | PreStart                    -> return! LifecycleEvent.preStart ctx cycle
        | PostStop                    -> return! LifecycleEvent.postStop ctx cycle
        | PreRestart (cause, message) -> return! LifecycleEvent.preRestart ctx cause message cycle
        | PostRestart cause           -> return! LifecycleEvent.postRestart ctx cause cycle
      | :? BardMessages as bm ->
        match bm with
        | BardMessages.ParseRequest sm ->
          do BardMessages.parseRequest ctx sm |> ignore
          return! cycle bardState
        | BardMessages.Join sm         -> return! BardMessages.join ctx sm cycle
        | BardMessages.Leave sm        -> return! BardMessages.leave ctx sm cycle
        | BardMessages.Resume sm       -> return! BardMessages.resume ctx sm cycle
        | BardMessages.Loop sm         -> return! BardMessages.loop ctx sm cycle
        | BardMessages.Play (sm, song) -> return! BardMessages.play ctx sm song cycle
        | BardMessages.Skip sm         -> return! BardMessages.skip ctx sm cycle
        | BardMessages.Query sm        -> return! BardMessages.query ctx sm cycle
        | BardMessages.Stop sm         -> return! BardMessages.stop ctx sm cycle
        | BardMessages.Clear sm        -> return! BardMessages.clear ctx sm cycle
        | BardMessages.NowPlay sm      -> return! BardMessages.nowPlay ctx sm cycle
        | BardMessages.Final sm        -> return! BardMessages.final ctx sm cycle
        | BardMessages.Restart sm      -> return! BardMessages.restart ctx sm cycle
      | _ -> return! Ignore

    }

    cycle { Guild = guild; PlayerState = PlayerState.WaitSong }

