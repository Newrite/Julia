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
open UMX

[<NoComparison>]
type GuildMessageContext = {
  Client:  DiscordSocketClient
  Guild:   SocketGuild
  Message: SocketMessage
}

[<NoComparison>]
type Song = {
  Url:       URL
  Title:     SongTitle
  Time:      TimeSpan
  Thumbnail: Thumbnail
}

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
    | Video    of URL * GuildMessageContext
    | Search   of URL * GuildMessageContext
    | Playlist of URL * GuildMessageContext

  [<NoComparison>]
  [<RequireQualifiedAccess>]
  type LoggerMessages = 
    | Error   of string
    | Info    of string
    | Debug   of string
    | Warning of string

  [<NoComparison>]
  [<RequireQualifiedAccess>]
  type GuildSystemMessages =
    | Restart of GuildMessageContext

  [<NoComparison>]
  [<RequireQualifiedAccess>]
  type GuildWriterMessages =
    | WriteMessage      of GuildMessageContext * MessageContent
    | WritePlay         of GuildMessageContext * Embed * Song
    | WriteParsed       of GuildMessageContext * Embed * URL
    | WriteStatus       of GuildMessageContext * Embed
    | WriteEmbedMessage of GuildMessageContext * Embed

[<AutoOpen>]
module ActorStatesDiscord =

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

  [<NoComparison>]
  type ParsingSongState = {
    ParsingMessage: Rest.RestUserMessage
    ParsingSongUrl: URL
  }
  //
  //[<NoComparison>]
  //[<RequireQualifiedAccess>]
  //type StatusMessageState =
  //  | UnhandledStatusMessage   of Rest.RestUserMessage
  //  | Wait
  
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

  [<NoComparison>]
  type GuildWriterState = {
    ParsingSongState:   ParsingSongState list
    StatusMessageState: Rest.RestUserMessage option
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