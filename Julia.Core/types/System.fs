namespace Julia.Core

open Akkling
open Akka.Actor

open FSharp.UMX

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

[<RequireQualifiedAccess>]
type SystemErrros =
  | Bard of BardErrros
  with

  override self.ToString() =
    match self with
    | Bard be ->
      be.ToString() |> sprintf "%s"