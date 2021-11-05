namespace JuliaDiscord.Core

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

open System
open System.Threading
open System.Diagnostics
open System.Threading.Tasks
open System.Collections.Generic

open FSharp.UMX
open Discord.WebSocket

[<AutoOpen>]
module Operators =
  
  let inline (>=>) twoTrackInput switchFunction =
    match twoTrackInput with
    | Ok s -> switchFunction s
    | Error f -> Error f
  
  let inline (>>=) twoTrackInput switchFunction =
    match twoTrackInput with
    | Ok s -> Ok(switchFunction s)
    | Error f -> Error f

[<Measure>] type actor_name
[<Measure>] type system_name
[<Measure>] type url
[<Measure>] type title
[<Measure>] type thumbnail

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
module ActorMessages =

  [<NoEquality>]
  [<NoComparison>]
  [<RequireQualifiedAccess>]
  type SupervisorMessages<'a> =
    | CreateActor
      of (Actor<'a> -> Effect<'a>) * string<actor_name>
  
    | CreateSystemActor
      of (Actor<'a> -> Effect<'a>) * string<actor_name>
  
    | CreateSupervisorActor
      of (Actor<'a> -> Effect<'a>) * string<actor_name> * (unit -> SupervisorStrategy)
  
    | CreateSystemSupervisorActor
      of (Actor<'a> -> Effect<'a>) * string<actor_name> * (unit -> SupervisorStrategy)
  
    | ActorMessage
      of string<actor_name> * obj
  
    | GetActor
      of string<actor_name>

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
module ActorStates =
  
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
module ActorAsks =

  [<NoComparison>]
  [<RequireQualifiedAccess>]
  type SongkeeperAsk =
    | AvaibleSongsTitle
    | NextSong

  [<NoComparison>]
  [<RequireQualifiedAccess>]
  type GuildSystemAsk =
    | Status of GuildMessageContext


[<NoEquality>]
[<NoComparison>]
type GuildProxy<'a> = private {
  guild: SocketGuild
  guildSystem: {| 
    message: {|
    Songkeeper: GuildSystemMessage -> unit
    GuildActor: GuildSystemMessage -> unit
    Bard      : GuildSystemMessage -> unit
    Youtuber  : GuildSystemMessage -> unit
    Julia     : GuildSystemMessage -> unit
    |}
    ask:     {|
      Songkeeper: GuildSystemAsk -> Async<'a>
      GuildActor: GuildSystemAsk -> Async<'a>
      Bard      : GuildSystemAsk -> Async<'a>
      Youtuber  : GuildSystemAsk -> Async<'a>
      Julia     : GuildSystemAsk -> Async<'a>
    |}
  |}
  message: {|
    Songkeeper: SongkeeperMessages -> unit
    GuildActor: GuildActorMessages -> unit
    Bard      : BardMessages       -> unit
    Youtuber  : YoutuberMessages   -> unit
    Julia     : JuliaMessages      -> unit
  |}
  ask:     {|
    Songkeeper: SongkeeperAsk -> Async<'a>
  |}
}
with

  member self.Message     = self.message
  member self.Ask         = self.ask
  member self.Guild       = self.guild

  member self.GuildSystemMessage = self.guildSystem.message
  member self.GuildSystemAsk     = self.guildSystem.ask

[<AutoOpen>]
module ActorContexts =

  [<NoComparison>]
  type SupervisorContext<'a> = {
    Mailbox: Actor<'a>
  }

  [<NoComparison>]
  type JuliaContext<'a> = {
    State:   DiscordSocketClient
    Mailbox: Actor<'a>
  }

  [<NoComparison>]
  [<NoEquality>]
  type GuildActorContext<'a, 'b> = {
    Mailbox: Actor<'a>
    Proxy:   GuildProxy<'b>
  }
  
  [<NoComparison>]
  [<NoEquality>]
  type SongkeeperContext<'a, 'b> = {
    State:   Song list
    Mailbox: Actor<'a>
    Proxy:   GuildProxy<'b>
  }
  
  [<NoComparison>]
  [<NoEquality>]
  type BardContext<'a, 'b> = {
    State:       BardState
    Mailbox:     Actor<'a>
    Proxy:       GuildProxy<'b>
  }
  
  [<NoComparison>]
  [<NoEquality>]
  type YoutuberContext<'a, 'b> = {
    State:   YoutuberState
    Mailbox: Actor<'a>
    Proxy:   GuildProxy<'b>
  }

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

[<RequireQualifiedAccess>]
type SystemErrros =
  | Bard of BardErrros
  with

  override self.ToString() =
    match self with
    | Bard be ->
      be.ToString() |> sprintf "%s"