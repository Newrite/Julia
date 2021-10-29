namespace JuliaDiscord.Core

open Discord
open Discord.Net
open Discord.WebSocket

open Akka
open Akkling
open Akka.Actor
open Akkling.Actors

open YoutubeExplode.Videos.Streams

open System.Diagnostics

open FSharp.UMX

[<Measure>] type actor_name
[<Measure>] type system_name

[<NoComparison>]
type SupervisorContext<'a> = {
  Mailbox: Actor<'a>
}

[<NoEquality>]
[<NoComparison>]
[<RequireQualifiedAccess>]
type SupervisorMessages<'a> =
  | CreateActor of (Actor<'a> -> Effect<'a>) * string<actor_name>
  | CreateSystemActor of (Actor<'a> -> Effect<'a>) * string<actor_name>
  | CreateSupervisorActor of (Actor<'a> -> Effect<'a>) * string<actor_name> * (unit -> SupervisorStrategy)
  | CreateSystemSupervisorActor of (Actor<'a> -> Effect<'a>) * string<actor_name> * (unit -> SupervisorStrategy)
  | ActorMessage of string<actor_name> * obj
  | GetActor of string<actor_name>

[<NoComparison>]
type JuliaContext<'a> = {
  State: DiscordSocketClient
  Mailbox: Actor<'a>
}

[<NoComparison>]
[<RequireQualifiedAccess>]
type JuliaMessages =
  | ReciveMessage of SocketMessage
  | Log of LogMessage
  | InitFinish
  | JoinedGuild of SocketGuild


[<NoComparison>]
type GuildActorContext<'a> = {
  State: SocketGuild
  Mailbox: Actor<'a>
}

[<NoComparison>]
[<RequireQualifiedAccess>]
type GuildActorMessages =
  | GuildMessage of SocketMessage
  | RestartActors of SocketMessage
  | Restart of SocketMessage

[<NoComparison>]
type Song = {
  StreamInfo: IStreamInfo
  Title: string
}

[<NoComparison>]
type BardPlaying = {
  FFmpeg: Process
  Song: Song
}

[<NoComparison>]
[<RequireQualifiedAccess>]
type PlayerState =
  | WaitSong
  | Playing of BardPlaying
  | Loop of BardPlaying
  | StopPlaying of Song

[<NoComparison>]
type BardState = {
  Guild: SocketGuild
  PlayerState: PlayerState
}

[<NoComparison>]
type BardContext<'a> = {
  State: BardState
  Mailbox: Actor<'a>
}

[<NoComparison>]
[<RequireQualifiedAccess>]
type BardMessages =
  | ParseRequest of SocketMessage
  | Join of SocketMessage
  | Leave of SocketMessage
  | Resume of SocketMessage
  | Loop of SocketMessage
  | Play of SocketMessage * Song
  | Skip of SocketMessage
  | Query of SocketMessage
  | Stop of SocketMessage
  | Clear of SocketMessage
  | NowPlay of SocketMessage
  | Final of SocketMessage
  | Restart of SocketMessage

[<NoComparison>]
type SongkeeperContext<'a> = {
  State: Song list
  Mailbox: Actor<'a>
}

[<NoComparison>]
[<RequireQualifiedAccess>]
type SongkeeperMessages =
  | AddSong of Song
  | ClearSongs
  | Restart

[<NoComparison>]
[<RequireQualifiedAccess>]
type SongkeeperAsk =
  | AvaibleSongsTitle
  | NextSong


[<RequireQualifiedAccess>]
type BardErrros =
  | UserNoInVoiceChannel
  | IsNoGuildMessage
  with
  
  override self.ToString() =
    match self with
    |UserNoInVoiceChannel ->
      "Currently user no connect to voice channel"
    |IsNoGuildMessage ->
      "Message from this user is not from guild"

[<RequireQualifiedAccess>]
type SystemErrros =
  | Bard of BardErrros
  with

  override self.ToString() =
    match self with
    | Bard be ->
      be.ToString() |> sprintf "%s"