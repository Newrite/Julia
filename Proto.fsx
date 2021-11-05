#r "nuget: YoutubeDLSharp"
open YoutubeDLSharp
open YoutubeDLSharp.Options

let ffmpegpath =
   @"C:\Users\nirn2\Downloads\ffmpeg-N-104348-gbb10f8d802-win64-gpl\bin\ffmpeg.exe"

let youtubedl =
  @"C:\Users\nirn2\Downloads\ffmpeg-N-104348-gbb10f8d802-win64-gpl\bin\youtube-dl.exe"

let YTDLClient = new YoutubeDL()

//YTDLClient.FFmpegPath <- ffmpegpath
YTDLClient.YoutubeDLPath <- youtubedl

let printChannel url =
  let task = YTDLClient.RunVideoDataFetch($"https://www.youtube.com/watch?v={url}")
  task.Wait()
  task.Result.Data

let task = YTDLClient.RunVideoDataFetch("https://www.youtube.com/playlist?list=PLvlw_ICcAI4dM4_nz3mqlONRq9vNFAm0u")
task.Wait()
//task.Result.Data.Entries
//|> Seq.iter ( fun v ->
//  let data = printChannel v.Url
//  printfn "Channel: %s" data.Channel
//  printfn "Title: %s" data.Title
//)

let opt = new OptionSet(
  NoProgress = true,
  PreferFfmpeg = true,
  NoColor = true,
  NoCacheDir = true,
  Format = "bestaudio/best",
  Output = "-"
)

let ytdlProc = new YoutubeDLProcess()
ytdlProc.ExecutablePath <- youtubedl
ytdlProc.OutputReceived.AddHandler(fun o e -> printfn "%s" e.Data)
let task2 = ytdlProc.RunAsync([| "https://www.youtube.com/watch?v=jboDF5D1eMU" |], opt)
task2.Wait()

(*_youtube_process = subprocess.Popen(('youtube-dl','-f','','--prefer-ffmpeg', '--no-color', '--no-cache-dir', '--no-progress','-o', '-', '-f', '22/18', url, '--reject-title', stream_id),stdout=subprocess.PIPE)*)