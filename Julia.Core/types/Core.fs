namespace Julia.Core

open FSharp.UMX

[<AutoOpen>]
module UMX =
  [<Measure>] type private actorname
  [<Measure>] type private systemname
  [<Measure>] type private url
  [<Measure>] type private songtitle
  [<Measure>] type private thumbnail
  [<Measure>] type private messagecontet
  [<Measure>] type private embedtitle
  [<Measure>] type private embeddescription
  
  type ActorName         = string<actorname>
  type SystemName        = string<systemname>
  type URL               = string<url>
  type SongTitle         = string<songtitle>
  type Thumbnail         = string<thumbnail>
  type MessageContent    = string<messagecontet>
  type EmbedTitle        = string<embedtitle>
  type EmbedDescription  = string<embeddescription>