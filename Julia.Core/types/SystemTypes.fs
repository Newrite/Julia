namespace Julia.Core

open Akkling
open Akka.Actor

[<AutoOpen>]
module ActorMessages =

  [<NoEquality>]
  [<NoComparison>]
  [<RequireQualifiedAccess>]
  type SupervisorMessages<'a> =
    | CreateActor
      of (Actor<'a> -> Effect<'a>) * ActorName
  
    | CreateSystemActor
      of (Actor<'a> -> Effect<'a>) * ActorName
  
    | CreateSupervisorActor
      of (Actor<'a> -> Effect<'a>) * ActorName * (unit -> SupervisorStrategy)
  
    | CreateSystemSupervisorActor
      of (Actor<'a> -> Effect<'a>) * ActorName * (unit -> SupervisorStrategy)
  
    | ActorMessage
      of ActorName * obj
  
    | GetActor
      of ActorName

[<RequireQualifiedAccess>]
type SystemErrros =
  | Bard of BardErrros
  with

  override self.ToString() =
    match self with
    | Bard be ->
      be.ToString() |> sprintf "%s"