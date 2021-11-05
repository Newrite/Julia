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

module Bard =

  let [<Literal>] private Play    = "Play"
  let [<Literal>] private Stop    = "Stop"
  let [<Literal>] private Join    = "Join"
  let [<Literal>] private Loop    = "Loop"
  let [<Literal>] private Leave   = "Leave"
  let [<Literal>] private Resume  = "Resume"
  let [<Literal>] private Queue   = "Queue"
  let [<Literal>] private Clear   = "Clear"
  let [<Literal>] private Nowplay = "Nowplay"
  let [<Literal>] private Skip    = "Skip"

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
    {| command = Queue
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

  let private ffmpegpath =
    @"C:\Users\nirn2\Downloads\ffmpeg-N-104348-gbb10f8d802-win64-gpl\bin\ffmpeg.exe"

  let private youtubedl =
    @"C:\Users\nirn2\Downloads\ffmpeg-N-104348-gbb10f8d802-win64-gpl\bin\youtube-dl.exe"

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

  let private createYoutubeDLProcess url =

    let info =
      ProcessStartInfo(
        FileName = youtubedl,
        Arguments = $"--cookies \"youtube.com_cookies.txt\" --output \"-\" --no-cache-dir --no-color --prefer-ffmpeg --format \"bestaudio/best\" {url}",
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardInput = true
      )

    Process.Start(info)

  let private validateChannel (gmc: GuildMessageContext) =
    match gmc.Message.Author with
    | :? SocketGuildUser as user ->
      if user.VoiceChannel <> null then
        Ok user
      else
        Result.Error BardErrros.UserNoInVoiceChannel
    | _ -> Result.Error BardErrros.IsNoGuildMessage
    
  let private connectChannel (user: SocketGuildUser) (botGuild: SocketGuild) =
    
    let connect() =
      user.VoiceChannel.ConnectAsync()
      |> Async.AwaitTask
      |> ignore

    match botGuild.CurrentUser.VoiceChannel with
    | null -> connect()
    | channel when channel = user.VoiceChannel -> ()
    | _ -> connect()

  let private translateStream (youtubeDL: Process) (ffmpeg: Process) (ctx: BardContext<_, _>) (gmc: GuildMessageContext) = task {
    

    use discord =
      ctx.Proxy.Guild.AudioClient
        .CreatePCMStream(AudioApplication.Music)

    use input = ffmpeg.StandardInput.BaseStream
    use output = ffmpeg.StandardOutput.BaseStream

    output.CopyToAsync(discord) |> ignore
    let streamDiscord = youtubeDL.StandardOutput.BaseStream.CopyToAsync(input)

    while not streamDiscord.IsCompleted && not ffmpeg.HasExited do
      do! Async.Sleep(1000)

    printfn "StreamState: %A" streamDiscord.Status
    printfn "FFmpeg exited: %b" ffmpeg.HasExited

    BardMessages.Final gmc
    |> ctx.Proxy.Message.Bard
  }

  let private startPlaySong song (ctx: BardContext<_, _>) (gmc: GuildMessageContext) =
    match validateChannel gmc with
    | Ok user ->
      connectChannel user ctx.Proxy.Guild
      let ffmpeg = createFFmpegProcess()
      let ytdl = createYoutubeDLProcess song.Url
      Utils.sendEmbed gmc <| Utils.answerWithThumbnailEmbed Play $"Начинается воспроизведение:\n{song.Title}" %song.Thumbnail
      let bp =
        let s = { FFmpeg = ffmpeg; YoutubeDL = ytdl; Song = song }
        match ctx.State.PlayerState with
        | PlayerState.Loop b -> PlayerState.Loop s
        | _ -> PlayerState.Playing s
      let newState = { ctx.State with PlayerState = bp }
      translateStream ytdl ffmpeg ctx gmc |> ignore
      newState
    | Error err ->
      Utils.sendEmbed gmc <| Utils.answerEmbed Play $"Ошибка воспроизведения: {err.ToString()}"
      ctx.State


  module private LifecycleEvent =

    let inline preStart (ctx: BardContext<_, _>) cycle =
      printfn "Actor bard for guild %s start" ctx.Proxy.Guild.Name
      cycle ctx.State

    let inline postStop (ctx: BardContext<_, _>) cycle =
      printfn "PostStop"
      ignored()

    let inline preRestart (ctx: BardContext<_, _>) cause message cycle =
      printfn "PreRestart cause: %A message: %A" cause message
      ignored()

    let inline postRestart (ctx: BardContext<_, _>) cause cycle =
      printfn "PostRestart cause: %A" cause
      ignored()

  module private BardMessages =
    
    let inline parseRequest (ctx: BardContext<_, _>) (gmc: GuildMessageContext) = task {

      let (|Video|Playlist|Message|NoArg|) (gmc: GuildMessageContext) =

        let msgArray = gmc.Message.Content.Split(' ')
        
        if msgArray.Length < 2 then
          NoArg
        else
          printfn "msgArray: %s" msgArray.[1]
          if msgArray.[1].Contains("https://")
            && msgArray.[1].Contains("youtu")
            && msgArray.[1].Contains("playlist") then
            Playlist msgArray.[1]
          elif msgArray.[1].Contains("https://")
            && msgArray.[1].Contains("youtu") then
            Video msgArray.[1]
          else
            let allExceptFirst =
              msgArray
              |> Seq.mapi (fun i s -> if i = 0 then "" else s)
              |> Seq.reduce (fun acc elem -> acc + elem)
            Message allExceptFirst

      match gmc with
      | NoArg        ->
        Utils.sendEmbed gmc <| Utils.answerEmbed Play "Недостаточно аргументов, вы забыли ссылку?"
      | Playlist msg ->
        YoutuberMessages.Playlist(%msg, gmc)
        |> ctx.Proxy.Message.Youtuber
      | Video    msg ->
        YoutuberMessages.Video(%msg, gmc)
        |> ctx.Proxy.Message.Youtuber
      | Message  msg ->
        YoutuberMessages.Search(%msg, gmc)
        |> ctx.Proxy.Message.Youtuber
    }

    let inline join (ctx: BardContext<_, _>) gmc cycle =
      match validateChannel gmc with
      | Ok user ->
        connectChannel user ctx.Proxy.Guild
        Utils.sendEmbed gmc <| Utils.answerEmbed Join $"Запрыгнула в канал {user.VoiceChannel.Name}"
      | Error err ->
        Utils.sendEmbed gmc <| Utils.answerEmbed Join $"Не удалось зайти в канал {err.ToString()}"
      cycle ctx.State

    let inline leave (ctx: BardContext<_, _>) gmc cycle =
      match ctx.State.PlayerState with
      | PlayerState.Playing _ | PlayerState.Loop _ ->
        Utils.sendEmbed gmc <| Utils.answerEmbed Leave "Сейчас идет воспроизведение, я не могу покинуть канал"
      | PlayerState.StopPlaying _ | PlayerState.WaitSong ->
        match ctx.Proxy.Guild.CurrentUser.VoiceChannel with
        | null ->
          Utils.sendEmbed gmc <| Utils.answerEmbed Leave "Я и так не в голосовом канале"
          //audioClients.TryRemove(ctx.Proxy.Guild.Id) |> ignore
        | channel ->
          channel.DisconnectAsync() |> ignore
          //audioClients.TryRemove(ctx.Proxy.Guild.Id) |> ignore
          Utils.sendEmbed gmc <| Utils.answerEmbed Leave "Покинула голосовой канал"
      cycle ctx.State

    let inline play (ctx: BardContext<_, _>) gmc (song: Song) cycle =
      
      let inline addToQuery songToQuery =
        Utils.sendEmbed gmc <| Utils.answerWithThumbnailEmbed Play $"Добавлено в очередь:\n{songToQuery.Title}" songToQuery.Thumbnail
        SongkeeperMessages.AddSong songToQuery
        |> ctx.Proxy.Message.Songkeeper

      match ctx.State.PlayerState with
      | PlayerState.Playing _ | PlayerState.Loop _ ->
        addToQuery song
        cycle ctx.State
      | PlayerState.WaitSong ->
        cycle <| startPlaySong song ctx gmc
      | PlayerState.StopPlaying s ->
        let newState = startPlaySong song ctx gmc
        addToQuery song
        cycle newState

    let inline resume (ctx: BardContext<_, _>) gmc cycle =

      match ctx.State.PlayerState with
      | PlayerState.Playing _ | PlayerState.Loop _ ->
        Utils.sendEmbed gmc <| Utils.answerEmbed Resume "Воспроизведение уже идет"
        cycle ctx.State
      | PlayerState.WaitSong ->
        Utils.sendEmbed gmc <| Utils.answerEmbed Resume "Нет остановленных песен"
        cycle ctx.State
      | PlayerState.StopPlaying s ->
        Utils.sendEmbed gmc <| Utils.answerWithThumbnailEmbed Resume $"Возобновляется воспроизведение остановленной песни:\n{s.Title}" s.Thumbnail
        let newState = startPlaySong s ctx gmc
        cycle newState

    let inline skip (ctx: BardContext<_, _>) gmc cycle =

      match ctx.State.PlayerState with
      | PlayerState.Playing bp ->
        bp.FFmpeg.Kill()
        bp.YoutubeDL.Kill()
        Utils.sendEmbed gmc <| Utils.answerEmbed Skip $"Скипнута песня:\n{bp.Song.Title}"
      | PlayerState.Loop bp ->
        bp.FFmpeg.Kill()
        bp.YoutubeDL.Kill()
        Utils.sendEmbed gmc <| Utils.answerEmbed Skip $"Скипнута песня, но ее воспроизведенеи продолжится потому что она зациклена:\n{bp.Song.Title}"
      | PlayerState.StopPlaying _ | PlayerState.WaitSong ->
        Utils.sendEmbed gmc <| Utils.answerEmbed Skip "Сейчас ничего не играет"

      cycle ctx.State

    let inline query (ctx: BardContext<_, _>) gmc cycle =

      let songsTitle: string list =
        ctx.Proxy.Ask.Songkeeper SongkeeperAsk.AvaibleSongsTitle
        |> Async.RunSynchronously
        |> box :?> string list

      let test = query {
        for title in songsTitle do
          select title
          last
      }
      if songsTitle.Length = 0 then
        Utils.sendEmbed gmc <| Utils.answerEmbed Queue "Очередь пуста"
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
          Utils.sendEmbed gmc <| Utils.answerEmbed Queue query)

      cycle ctx.State

    let inline stop (ctx: BardContext<_, _>) gmc cycle =

      match ctx.State.PlayerState with
      | PlayerState.Playing bp ->
        bp.FFmpeg.Kill()
        bp.YoutubeDL.Kill()
        Utils.sendEmbed gmc <| Utils.answerEmbed Stop $"Песня остановлена:\n{bp.Song.Title}"
        cycle { ctx.State with PlayerState = PlayerState.StopPlaying bp.Song }
      | PlayerState.Loop bp ->
        bp.FFmpeg.Kill()
        bp.YoutubeDL.Kill()
        Utils.sendEmbed gmc <| Utils.answerEmbed Stop $"Песня остановлена:\n{bp.Song.Title}"
        cycle { ctx.State with PlayerState = PlayerState.StopPlaying bp.Song }
      | PlayerState.StopPlaying song ->
        Utils.sendEmbed gmc <| Utils.answerEmbed Stop $"Воспроизведение уже остановлено, песня:\n{song.Title}"
        cycle ctx.State
      | PlayerState.WaitSong ->
        Utils.sendEmbed gmc <| Utils.answerEmbed Stop "Ожидаются песни для воспроизведения"
        cycle ctx.State

    let inline loop (ctx: BardContext<_, _>) gmc cycle =

      match ctx.State.PlayerState with
      | PlayerState.Playing bp ->
        Utils.sendEmbed gmc <| Utils.answerEmbed Loop $"Песня зациклена:\n{bp.Song.Title}"
        cycle { ctx.State with PlayerState = PlayerState.Loop bp }
      | PlayerState.Loop bp ->
        Utils.sendEmbed gmc <| Utils.answerEmbed Loop "Песня уже зациклена"
        cycle ctx.State
      | PlayerState.StopPlaying song ->
        Utils.sendEmbed gmc <| Utils.answerEmbed Loop $"Воспроизведение остановлено, песня:\n{song.Title}"
        cycle ctx.State
      | PlayerState.WaitSong ->
        Utils.sendEmbed gmc <| Utils.answerEmbed Loop "Ожидаются песни для воспроизведения"
        cycle ctx.State

    let inline clear (ctx: BardContext<_, _>) gmc cycle =

      ctx.Proxy.Message.Songkeeper SongkeeperMessages.ClearSongs

      Utils.sendEmbed gmc <| Utils.answerEmbed Clear "Очередь очищена"

      cycle ctx.State

    let inline nowPlay (ctx: BardContext<_, _>) (gmc: GuildMessageContext) cycle =

      match ctx.State.PlayerState with
      | PlayerState.Playing bp ->
        Utils.sendEmbed gmc <| Utils.answerWithThumbnailEmbed Nowplay $"Сейчас играет:\n{bp.Song.Title}" bp.Song.Thumbnail
      | PlayerState.Loop bp ->
        Utils.sendEmbed gmc <| Utils.answerWithThumbnailEmbed Nowplay $"Сейчас играет и зациклена:\n{bp.Song.Title}" bp.Song.Thumbnail
      | PlayerState.StopPlaying song ->
        Utils.sendEmbed gmc <| Utils.answerWithThumbnailEmbed Nowplay $"Остановленная песня:\n{song.Title}" song.Thumbnail
      | PlayerState.WaitSong ->
        Utils.sendEmbed gmc <| Utils.answerEmbed Nowplay "Нет текущих песен"

      cycle ctx.State

    let inline final (ctx: BardContext<_, _>) gmc cycle =

      match ctx.State.PlayerState with
      | PlayerState.Playing bp ->
        bp.FFmpeg.Kill()
        bp.YoutubeDL.Kill()
        let song: Song list =
          ctx.Proxy.Ask.Songkeeper SongkeeperAsk.NextSong
          |> Async.RunSynchronously
          |> box :?> Song list
        match song with
        | head::_ ->
            cycle <| startPlaySong head ctx gmc
        | _ ->
          Utils.sendEmbed gmc <| Utils.answerEmbed Play "Очередь закончилась"
          cycle { ctx.State with PlayerState = PlayerState.WaitSong }
      | PlayerState.Loop bp ->
        bp.FFmpeg.Kill()
        bp.YoutubeDL.Kill()
        cycle <| startPlaySong bp.Song ctx gmc
      | PlayerState.StopPlaying _ | PlayerState.WaitSong ->
        cycle ctx.State

  module private GuildSystemMessage =

    let inline restart (ctx: BardContext<_, _>) gmc cycle =
      
      //audioClients.TryRemove(ctx.Proxy.Guild.Id) |> ignore

      match ctx.State.PlayerState with
      | PlayerState.Playing bp | PlayerState.Loop bp ->
        bp.FFmpeg.Kill()
        failwith "restart"
      | PlayerState.StopPlaying _ | PlayerState.WaitSong ->
        failwith "restart"

  module private GuildSystemAsk =

    let inline status (ctx: BardContext<_, _>) sm cycle =

      match ctx.State.PlayerState with
      | PlayerState.Playing bp | PlayerState.Loop bp ->
        ctx.Mailbox.Sender() <! $"Проигрываю песню: {bp.Song.Title}"
      | PlayerState.StopPlaying sp ->
        ctx.Mailbox.Sender() <! $"Остановлено воспроизведение: {sp.Title}"
      | PlayerState.WaitSong ->
        ctx.Mailbox.Sender() <! "Ожидаются песни"

      cycle ctx.State
        


  let bardActor (guildProxy: GuildProxy<_>) (mb: Actor<_>) =
    let rec cycle bardState = actor {

      let! (msg: obj) = mb.Receive()

      let (ctx: BardContext<_, _>) = { State = bardState; Mailbox = mb; Proxy = guildProxy }
      match msg with
      | LifecycleEvent le -> 
        match le with
        | PreStart                    -> return! LifecycleEvent.preStart    ctx cycle
        | PostStop                    -> return! LifecycleEvent.postStop    ctx cycle
        | PreRestart (cause, message) -> return! LifecycleEvent.preRestart  ctx cause message cycle
        | PostRestart cause           -> return! LifecycleEvent.postRestart ctx cause cycle
      | :? BardMessages as bm ->
        match bm with
        | BardMessages.ParseRequest gmc ->
          BardMessages.parseRequest ctx gmc |> ignore
          return! cycle bardState
        | BardMessages.Join gmc         -> return! BardMessages.join    ctx gmc cycle
        | BardMessages.Leave gmc        -> return! BardMessages.leave   ctx gmc cycle
        | BardMessages.Resume gmc       -> return! BardMessages.resume  ctx gmc cycle
        | BardMessages.Loop gmc         -> return! BardMessages.loop    ctx gmc cycle
        | BardMessages.Play (gmc, song) -> return! BardMessages.play    ctx gmc song cycle
        | BardMessages.Skip gmc         -> return! BardMessages.skip    ctx gmc cycle
        | BardMessages.Query gmc        -> return! BardMessages.query   ctx gmc cycle
        | BardMessages.Stop gmc         -> return! BardMessages.stop    ctx gmc cycle
        | BardMessages.Clear gmc        -> return! BardMessages.clear   ctx gmc cycle
        | BardMessages.NowPlay gmc      -> return! BardMessages.nowPlay ctx gmc cycle
        | BardMessages.Final gmc        -> return! BardMessages.final   ctx gmc cycle
      | :? GuildSystemMessage as gsm ->
        match gsm with
        | GuildSystemMessage.Restart gmc -> return! GuildSystemMessage.restart ctx gmc cycle
      | :? GuildSystemAsk as gsa ->
        match gsa with
        | GuildSystemAsk.Status gmc -> return! GuildSystemAsk.status ctx gmc cycle
      | _ -> return! Ignore

    }

    cycle { PlayerState = PlayerState.WaitSong }