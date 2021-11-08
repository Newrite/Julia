namespace Julia.Core

open Discord
open Discord.Audio
open Discord.WebSocket

open Akkling

open YoutubeExplode

open System
open System.Threading
open System.Diagnostics
open System.Collections.Generic

open FSharp.UMX

[<NoComparison>]
type GuildMessageContext = {
  Client:  DiscordSocketClient
  Guild:   SocketGuild
  Message: SocketMessage
}

[<NoComparison>]
type Song = {
  Url:       string<url>
  Title:     string<title>
  Time:      TimeSpan
  Thumbnail: string<thumbnail>
}

[<NoComparison>]
type BardPlaying = {
  FFmpeg:      Process
  YoutubeDL:   Process
  Song:        Song
}

[<NoComparison>]
[<RequireQualifiedAccess>]
type PlayerState =
  | WaitSong    
  | Playing     of BardPlaying
  | Loop        of BardPlaying
  | StopPlaying of Song

[<AutoOpen>]
module ActorMessagesDiscord =

  [<NoComparison>]
  [<RequireQualifiedAccess>]
  type JuliaMessages =
    | InitFinish
    | ReciveMessage of SocketMessage
    | Log           of LogMessage
    | JoinedGuild   of SocketGuild

  [<NoComparison>]
  [<RequireQualifiedAccess>]
  type GuildActorMessages =
    | GuildMessage  of GuildMessageContext
    | ActorsRestart of GuildMessageContext
    | ActorsStatus  of GuildMessageContext

  [<NoComparison>]
  [<RequireQualifiedAccess>]
  type BardMessages =
    | ParseRequest of GuildMessageContext
    | Join         of GuildMessageContext
    | Leave        of GuildMessageContext
    | Resume       of GuildMessageContext
    | Loop         of GuildMessageContext
    | Skip         of GuildMessageContext
    | Query        of GuildMessageContext
    | Stop         of GuildMessageContext
    | Clear        of GuildMessageContext
    | NowPlay      of GuildMessageContext
    | Final        of GuildMessageContext
    | Play         of GuildMessageContext * Song

  [<NoComparison>]
  [<RequireQualifiedAccess>]
  type SongkeeperMessages =
    | AddSong    of Song
    | ClearSongs

  [<NoComparison>]
  [<RequireQualifiedAccess>]
  type YoutuberMessages =
    | Video    of string<url> * GuildMessageContext
    | Search   of string<url> * GuildMessageContext
    | Playlist of string<url> * GuildMessageContext

  [<NoComparison>]
  [<RequireQualifiedAccess>]
  type LoggerMessages = 
    | Error   of string
    | Info    of string
    | Debug   of string
    | Warning of string

  [<NoComparison>]
  [<RequireQualifiedAccess>]
  type GuildSystemMessage =
    | Restart of GuildMessageContext

[<AutoOpen>]
module ActorStatesDiscord =
  
  [<NoComparison>]
  type BardState = {
    PlayerState: PlayerState
    AudioClient: IAudioClient option
  }

  [<NoComparison>]
  type YoutuberState = {
    Youtube:      YoutubeClient
    CancelToken:  CancellationTokenSource
    RequestQueue: List<YoutuberMessages>
  }

[<AutoOpen>]
module ActorAsksDiscord =

  [<NoComparison>]
  [<RequireQualifiedAccess>]
  type SongkeeperAsk =
    | AvaibleSongsTitle
    | NextSong

  [<NoComparison>]
  [<RequireQualifiedAccess>]
  type GuildSystemAsk =
    | Status of GuildMessageContext

[<RequireQualifiedAccess>]
type BardErrros =
  | UserNoInVoiceChannel
  | IsNoGuildMessage
  | CantGetAudioClientWhenTryConnect
  with
  
  override self.ToString() =
    match self with
    | UserNoInVoiceChannel ->
      "Currently user no connect to voice channel"
    | IsNoGuildMessage ->
      "Message from this user is not from guild"
    | CantGetAudioClientWhenTryConnect ->
      "Can't get AudioClient when try to connect voice channel"