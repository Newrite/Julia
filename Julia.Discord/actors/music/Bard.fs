namespace Julia.Discord

open System.Threading
open System.Diagnostics

open Discord.Audio
open Discord.WebSocket

open Akkling

open YoutubeExplode

open FSharp.UMX

open Julia.Core

module Bard =
  
  [<NoComparison>]
  [<NoEquality>]
  type private BardContext<'a> = {
    State:   BardState
    Mailbox: Actor<'a>
    Guild:   SocketGuild
    Proxy:   Sys.ProxyDiscord
    WriteProxy: GuildWriter.WriteProxy
  }

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
        Arguments = $"--cookies \"youtube.com_cookies.txt\" --output \"-\" --no-cache-dir --no-color --format \"bestaudio/best\" {url}",
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardInput = true
      )

    Process.Start(info)

  let private validateChannel gmc =
    match gmc.Message.Author with
    | :? SocketGuildUser as user ->
      if not (isNull user.VoiceChannel) then
        Ok user
      else
        Error BardErrros.UserNoInVoiceChannel
    | _ -> Error BardErrros.IsNoGuildMessage
    
  let private connectChannel
    (botGuild: SocketGuild) (ctx: BardContext<_>)
    (gmc: GuildMessageContext) (user: SocketGuildUser) =
    
    let connect() =
      Ok <| user.VoiceChannel.ConnectAsync().Result

    let rec recConnect tryCount =
      try
        match botGuild.CurrentUser.VoiceChannel with
        | null -> connect()
        | channel when channel = user.VoiceChannel ->
          match ctx.State.PlayerState with
          | PlayerState.Playing _ | PlayerState.Loop _ ->
            match ctx.State.AudioClient with
            | Some client -> Ok client
            | None        -> connect()
          | PlayerState.WaitSong | PlayerState.StopPlaying _ -> connect()
        | _ -> connect()
      with ex ->
        let err = sprintf "Failed get AudioClient: %s try counts: %d" ex.Message tryCount
        ctx.WriteProxy.WriteEmbedMessage(gmc, Utils.answerEmbed %"Connect" %err)
        if tryCount <= 3 then
          ctx.WriteProxy.WriteEmbedMessage(gmc, Utils.answerEmbed %"Connect" %"Try again...")
          recConnect (tryCount + 1)
        else
          Error BardErrros.CantGetAudioClientWhenTryConnect
        
    
    recConnect 0

  let private translateStream
    (youtubeDL: Process)    (ffmpeg: Process)
    (ctx: BardContext<_>) (gmc: GuildMessageContext)
    (client: IAudioClient) (song: Song)  = task {
    

    use discord =
      client.CreatePCMStream(AudioApplication.Music)

    use input = ffmpeg.StandardInput.BaseStream
    use output = ffmpeg.StandardOutput.BaseStream

    use _ = output.CopyToAsync(discord)
    use ytToFFStream = youtubeDL.StandardOutput.BaseStream.CopyToAsync(input)

    let mutable secondsPassed = 0.
    let songTime = song.Time
    let condition() =
      secondsPassed < songTime.TotalSeconds && not ytToFFStream.IsFaulted && not ffmpeg.HasExited
    while condition() do
      printfn "Seconds passed: %f Song duration in seconds: %f" secondsPassed songTime.TotalSeconds
      secondsPassed <- secondsPassed + 1.
      do! Async.Sleep(1000)

    printfn "StreamState: %A" ytToFFStream.Status
    printfn "FFmpeg exited: %b" ffmpeg.HasExited

    do! Async.Sleep(3000)

    BardMessages.Final gmc
    |> ctx.Proxy.Message.Bard
  }

  let private startPlaySong song ctx gmc =

    let prepareToTranslate client =

        let ffmpeg = createFFmpegProcess()

        Async.Sleep(5000) |> Async.RunSynchronously

        let ytdl = createYoutubeDLProcess song.Url

        ctx.WriteProxy.WritePlay(
          gmc,
          Utils.answerWithThumbnailEmbed %Play %($"Начинается воспроизведение:\n{song.Title}") %song.Thumbnail, 
          song
         )

        let bp =
          let s = { FFmpeg = ffmpeg; YoutubeDL = ytdl; Song = song }
          match ctx.State.PlayerState with
          | PlayerState.Loop b -> PlayerState.Loop s
          | _ -> PlayerState.Playing s

        let newState = { ctx.State with PlayerState = bp; AudioClient = Some client }
        translateStream ytdl ffmpeg ctx gmc client song |> ignore
        newState

    validateChannel gmc
    >=> connectChannel ctx.Guild ctx gmc
    >>= prepareToTranslate
    |> function
    | Ok state -> state
    | Error err ->
      ctx.WriteProxy.WritePlay(gmc, Utils.answerEmbed %Play %($"Ошибка воспроизведения: {err.ToString()}"), song)
      ctx.State


  module private LifecycleEvent =

    let inline preStart ctx cycle =
      printfn "Guild %s actor Bard start" ctx.Guild.Name
      cycle ctx.State

    let inline postStop ctx cycle =
      unhandled()

    let inline preRestart ctx cause message cycle =
      unhandled()

    let inline postRestart ctx cause cycle =
      unhandled()

  module private BardMessages =
    
    let inline parseRequest ctx gmc cycle = 
    
      async {

        let (|Video|Playlist|Message|NoArg|) (gmc: GuildMessageContext) =

          let msgArray = gmc.Message.Content.Split(' ')
          
          if msgArray.Length < 2 then
            NoArg
          else
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
                |> Seq.reduce (+)
              Message allExceptFirst

        match gmc with
        | NoArg        ->
          ctx.WriteProxy.WriteEmbedMessage(gmc, Utils.answerEmbed %Play %"Недостаточно аргументов, вы забыли ссылку?")
        | Playlist msg ->
          YoutuberMessages.Playlist(%msg, gmc)
          |> ctx.Proxy.Message.Youtuber
        | Video    msg ->
          YoutuberMessages.Video(%msg, gmc)
          |> ctx.Proxy.Message.Youtuber
        | Message  msg ->
          YoutuberMessages.Search(%msg, gmc)
          |> ctx.Proxy.Message.Youtuber } |> Async.Start

      cycle ctx.State

    let inline join ctx gmc cycle =
      match ctx.State.PlayerState with
      | PlayerState.Loop _ | PlayerState.Playing _ ->
        ctx.WriteProxy.WriteEmbedMessage(gmc, Utils.answerEmbed %Join %"Не могу сменить канал во время воспроизведения")
        cycle ctx.State
      | PlayerState.StopPlaying _ | PlayerState.WaitSong ->
        validateChannel gmc
        >=> connectChannel ctx.Guild ctx gmc
        |> function
        | Ok client ->
          let s = { ctx.State with AudioClient = Some client }
          ctx.WriteProxy.WriteEmbedMessage(gmc, Utils.answerEmbed %Join %"Запрыгнула в канал")
          cycle s
        | Error err ->
          ctx.WriteProxy.WriteEmbedMessage(gmc, Utils.answerEmbed %Join %($"Не удалось зайти в канал {err.ToString()}"))
          cycle ctx.State

    let inline leave ctx gmc cycle =
      match ctx.State.PlayerState with
      | PlayerState.Playing _ | PlayerState.Loop _ ->
        ctx.WriteProxy.WriteEmbedMessage(gmc, Utils.answerEmbed %Leave %"Сейчас идет воспроизведение, я не могу покинуть канал")
      | PlayerState.StopPlaying _ | PlayerState.WaitSong ->
        match ctx.Guild.CurrentUser.VoiceChannel with
        | null ->
          ctx.WriteProxy.WriteEmbedMessage(gmc, Utils.answerEmbed %Leave %"Я и так не в голосовом канале")
        | channel ->
          channel.DisconnectAsync() |> ignore
          ctx.WriteProxy.WriteEmbedMessage(gmc, Utils.answerEmbed %Leave %"Покинула голосовой канал")
      cycle ctx.State

    let inline play ctx gmc (song: Song) cycle =
      
      let inline addToQuery songToQuery =
        ctx.WriteProxy.WritePlay(
          gmc,
          Utils.answerWithThumbnailEmbed %Play %($"Добавлено в очередь:\n{songToQuery.Title}") songToQuery.Thumbnail,
          songToQuery
        )
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

    let inline resume ctx gmc cycle =

      match ctx.State.PlayerState with
      | PlayerState.Playing _ | PlayerState.Loop _ ->
        ctx.WriteProxy.WriteEmbedMessage(gmc, Utils.answerEmbed %Resume %"Воспроизведение уже идет")
        cycle ctx.State
      | PlayerState.WaitSong ->
        ctx.WriteProxy.WriteEmbedMessage(gmc, Utils.answerEmbed %Resume %"Нет остановленных песен")
        cycle ctx.State
      | PlayerState.StopPlaying s ->
        ctx.WriteProxy.WriteEmbedMessage(
          gmc,
          Utils.answerWithThumbnailEmbed %Resume %($"Возобновляется воспроизведение остановленной песни:\n{s.Title}") s.Thumbnail
        )
        let newState = startPlaySong s ctx gmc
        cycle newState

    let inline skip ctx gmc cycle =

      match ctx.State.PlayerState with
      | PlayerState.Playing bp ->
        bp.FFmpeg.Kill()
        bp.YoutubeDL.Kill()
        ctx.WriteProxy.WriteEmbedMessage(gmc, Utils.answerEmbed %Skip %($"Скипнута песня:\n{bp.Song.Title}"))
      | PlayerState.Loop bp ->
        bp.FFmpeg.Kill()
        bp.YoutubeDL.Kill()
        ctx.WriteProxy.WriteEmbedMessage(
          gmc,
          Utils.answerEmbed %Skip %($"Скипнута песня, но ее воспроизведенеи продолжится потому что она зациклена:\n{bp.Song.Title}")
        )
      | PlayerState.StopPlaying _ | PlayerState.WaitSong ->
        ctx.WriteProxy.WriteEmbedMessage(gmc, Utils.answerEmbed %Skip %"Сейчас ничего не играет")

      cycle ctx.State

    let inline queue ctx gmc cycle =

      let songsTitle: string list =
        ctx.Proxy.Ask.Songkeeper 
        <!? SongkeeperAsk.AvaibleSongsTitle

      if songsTitle.Length = 0 then
        ctx.WriteProxy.WriteEmbedMessage(gmc, Utils.answerEmbed %Queue %"Очередь пуста")
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
          ctx.WriteProxy.WriteEmbedMessage(gmc, Utils.answerEmbed %Queue %query))

      cycle ctx.State

    let inline stop ctx gmc cycle =

      match ctx.State.PlayerState with
      | PlayerState.Playing bp ->
        bp.FFmpeg.Kill()
        bp.YoutubeDL.Kill()
        ctx.State.AudioClient.Value.Dispose()
        ctx.WriteProxy.WriteEmbedMessage(gmc, Utils.answerEmbed %Stop %($"Песня остановлена:\n{bp.Song.Title}"))
        cycle { ctx.State with PlayerState = PlayerState.StopPlaying bp.Song; AudioClient = None }
      | PlayerState.Loop bp ->
        bp.FFmpeg.Kill()
        bp.YoutubeDL.Kill()
        ctx.State.AudioClient.Value.Dispose()
        ctx.WriteProxy.WriteEmbedMessage(gmc, Utils.answerEmbed %Stop %($"Песня остановлена:\n{bp.Song.Title}"))
        cycle { ctx.State with PlayerState = PlayerState.StopPlaying bp.Song; AudioClient = None }
      | PlayerState.StopPlaying song ->
        ctx.WriteProxy.WriteEmbedMessage(gmc, Utils.answerEmbed %Stop %($"Воспроизведение уже остановлено, песня:\n{song.Title}"))
        cycle ctx.State
      | PlayerState.WaitSong ->
        ctx.WriteProxy.WriteEmbedMessage(gmc, Utils.answerEmbed %Stop %"Ожидаются песни для воспроизведения")
        cycle ctx.State

    let inline loop ctx gmc cycle =

      match ctx.State.PlayerState with
      | PlayerState.Playing bp ->
        ctx.WriteProxy.WriteEmbedMessage(gmc, Utils.answerEmbed %Loop %($"Песня зациклена:\n{bp.Song.Title}"))
        cycle { ctx.State with PlayerState = PlayerState.Loop bp }
      | PlayerState.Loop bp ->
        ctx.WriteProxy.WriteEmbedMessage(gmc, Utils.answerEmbed %Loop %"Песня уже зациклена")
        cycle ctx.State
      | PlayerState.StopPlaying song ->
        ctx.WriteProxy.WriteEmbedMessage(gmc, Utils.answerEmbed %Loop %($"Воспроизведение остановлено, песня:\n{song.Title}"))
        cycle ctx.State
      | PlayerState.WaitSong ->
        ctx.WriteProxy.WriteEmbedMessage(gmc, Utils.answerEmbed %Loop %"Ожидаются песни для воспроизведения")
        cycle ctx.State

    let inline clear ctx gmc cycle =

      ctx.Proxy.Message.Songkeeper SongkeeperMessages.ClearSongs

      ctx.WriteProxy.WriteEmbedMessage(gmc, Utils.answerEmbed %Clear %"Очередь очищена")

      cycle ctx.State

    let inline nowPlay ctx (gmc: GuildMessageContext) cycle =

      match ctx.State.PlayerState with
      | PlayerState.Playing bp ->
        ctx.WriteProxy.WriteEmbedMessage(
          gmc,
          Utils.answerWithThumbnailEmbed %Nowplay %($"Сейчас играет:\n{bp.Song.Title}") bp.Song.Thumbnail
        )
      | PlayerState.Loop bp ->
        ctx.WriteProxy.WriteEmbedMessage(
          gmc,
          Utils.answerWithThumbnailEmbed %Nowplay %($"Сейчас играет и зациклена:\n{bp.Song.Title}") bp.Song.Thumbnail
        )
      | PlayerState.StopPlaying song ->
        ctx.WriteProxy.WriteEmbedMessage(
          gmc,
          Utils.answerWithThumbnailEmbed %Nowplay %($"Остановленная песня:\n{song.Title}") song.Thumbnail
        )
      | PlayerState.WaitSong ->
        ctx.WriteProxy.WriteEmbedMessage(gmc, Utils.answerEmbed %Nowplay %"Нет текущих песен")

      cycle ctx.State

    let inline final ctx gmc cycle =

      match ctx.State.PlayerState with
      | PlayerState.Playing bp ->
        bp.FFmpeg.Kill()
        bp.YoutubeDL.Kill()
        let song: Song list =
          ctx.Proxy.Ask.Songkeeper <!? SongkeeperAsk.NextSong
        match song with
        | head::_ ->
            cycle <| startPlaySong head ctx gmc
        | _ ->
          ctx.WriteProxy.WriteEmbedMessage(gmc, Utils.answerEmbed %Play %"Очередь закончилась")
          ctx.State.AudioClient.Value.Dispose()
          cycle { ctx.State with PlayerState = PlayerState.WaitSong; AudioClient = None }
      | PlayerState.Loop bp ->
        bp.FFmpeg.Kill()
        bp.YoutubeDL.Kill()
        cycle <| startPlaySong bp.Song ctx gmc
      | PlayerState.StopPlaying _ | PlayerState.WaitSong ->
        cycle ctx.State

  module private GuildSystemMessages =

    let inline restart ctx gmc cycle =

      match ctx.State.PlayerState with
      | PlayerState.Playing bp | PlayerState.Loop bp ->
        bp.FFmpeg.Kill()
        bp.YoutubeDL.Kill()
        if ctx.State.AudioClient.IsSome then
          ctx.State.AudioClient.Value.Dispose()
        failwith "restart"
      | PlayerState.StopPlaying _ | PlayerState.WaitSong ->
        failwith "restart"

  module private GuildSystemAsk =

    let inline status ctx sm cycle =

      match ctx.State.PlayerState with
      | PlayerState.Playing bp | PlayerState.Loop bp ->
        ctx.Mailbox.Sender() <! $"Проигрываю песню: {bp.Song.Title}"
      | PlayerState.StopPlaying sp ->
        ctx.Mailbox.Sender() <! $"Остановлено воспроизведение: {sp.Title}"
      | PlayerState.WaitSong ->
        ctx.Mailbox.Sender() <! "Ожидаются песни"

      cycle ctx.State
        


  let bardActor guildProxy (guild: SocketGuild) (mb: Actor<_>) =

    let writeProxy = GuildWriter.WriteProxy.Create guild

    let rec cycle bardState = actor {

      let! (msg: obj) = mb.Receive()

      let ctx =
        { State = bardState; Mailbox = mb; Proxy = guildProxy; Guild = guild; WriteProxy = writeProxy }
      match msg with
      | LifecycleEvent le -> 
        match le with
        | PreStart                    -> return! LifecycleEvent.preStart    ctx cycle
        | PostStop                    -> return! LifecycleEvent.postStop    ctx cycle
        | PreRestart (cause, message) -> return! LifecycleEvent.preRestart  ctx cause message cycle
        | PostRestart cause           -> return! LifecycleEvent.postRestart ctx cause cycle
      | :? BardMessages as bm ->
        match bm with
        | BardMessages.ParseRequest gmc -> return! BardMessages.parseRequest ctx gmc cycle
        | BardMessages.Join         gmc -> return! BardMessages.join         ctx gmc cycle
        | BardMessages.Leave        gmc -> return! BardMessages.leave        ctx gmc cycle
        | BardMessages.Resume       gmc -> return! BardMessages.resume       ctx gmc cycle
        | BardMessages.Loop         gmc -> return! BardMessages.loop         ctx gmc cycle
        | BardMessages.Play (gmc, song) -> return! BardMessages.play         ctx gmc song cycle
        | BardMessages.Skip         gmc -> return! BardMessages.skip         ctx gmc cycle
        | BardMessages.Query        gmc -> return! BardMessages.queue        ctx gmc cycle
        | BardMessages.Stop         gmc -> return! BardMessages.stop         ctx gmc cycle
        | BardMessages.Clear        gmc -> return! BardMessages.clear        ctx gmc cycle
        | BardMessages.NowPlay      gmc -> return! BardMessages.nowPlay      ctx gmc cycle
        | BardMessages.Final        gmc -> return! BardMessages.final        ctx gmc cycle
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

    cycle { PlayerState = PlayerState.WaitSong; AudioClient = None }