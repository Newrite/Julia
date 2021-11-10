namespace Julia.Discord

open Discord
open Discord.WebSocket

open Akkling

open Julia.Core

open FSharp.UMX
open Discord


module GuildWriter =

  [<NoEquality>]
  [<NoComparison>]
  type WriteProxy = private {
    writeMessage     : GuildMessageContext * MessageContent -> unit
    writePlay        : GuildMessageContext * Embed * Song -> unit
    writeParsed      : GuildMessageContext * Embed * URL -> unit
    writeStatus      : GuildMessageContext * Embed -> unit
    writeEmbedMessage: GuildMessageContext * Embed -> unit
  }
  with
    static member Create (guild: SocketGuild) =
      { writeMessage      = GuildWriterMessages.WriteMessage      >> Sys.Proxy.Discord.Message.guildWriter guild.Id
        writePlay         = GuildWriterMessages.WritePlay         >> Sys.Proxy.Discord.Message.guildWriter guild.Id
        writeParsed       = GuildWriterMessages.WriteParsed       >> Sys.Proxy.Discord.Message.guildWriter guild.Id
        writeStatus       = GuildWriterMessages.WriteStatus       >> Sys.Proxy.Discord.Message.guildWriter guild.Id
        writeEmbedMessage = GuildWriterMessages.WriteEmbedMessage >> Sys.Proxy.Discord.Message.guildWriter guild.Id }

    member self.WriteMessage      = self.writeMessage
    member self.WritePlay         = self.writePlay
    member self.WriteParsed       = self.writeParsed
    member self.WriteStatus       = self.writeStatus
    member self.WriteEmbedMessage = self.writeEmbedMessage

  let [<Literal>] private ParseToPlay = "Parser -> Play"

  [<NoEquality>]
  [<NoComparison>]
  type private GuildWriterContext<'a> = {
    State:      GuildWriterState
    Mailbox:    Actor<'a>
    Guild:      SocketGuild
    Proxy:      Sys.ProxyDiscord
    WriteProxy: WriteProxy
  }

  module private LifecycleEvent =

    let inline preStart ctx cycle =
      printfn "Guild %s actor GuildWriter start" ctx.Guild.Name
      cycle ctx.State

    let inline postStop ctx cycle =
      unhandled()

    let inline preRestart ctx cause message cycle =
      unhandled()

    let inline postRestart ctx cause cycle =
      unhandled()

  module private GuildWriterMessages =

    let inline writeMessage ctx gmc mc cycle =
      Utils.sendMessage gmc mc |> ignore
      cycle ctx.State

    let inline writePlay ctx gmc (embed: Embed) song cycle =

      let findSong =
        ctx.State.ParsingSongState
        |> List.tryFind (fun songState ->
          songState.ParsingSongUrl = song.Url
        )

      match findSong with
      | Some pst ->
        if pst.ParsingSongUrl = song.Url then
          pst.ParsingMessage.ModifyAsync(fun mp ->
            let newDesc: EmbedDescription = %embed.Description
            let newTitle: EmbedTitle = %ParseToPlay
            mp.Embed <- Utils.answerWithThumbnailEmbed newTitle newDesc song.Thumbnail
          ) |> ignore
          cycle { ctx.State with ParsingSongState = List.except [ pst ] ctx.State.ParsingSongState }
        else
          cycle ctx.State
      | None -> cycle ctx.State

    let inline writeParsed ctx gmc (embed: Embed) urlSong cycle =
      let parsingSongMessage = Utils.sendEmbed gmc embed

      let parsingSongState = { ParsingMessage = parsingSongMessage.Result; ParsingSongUrl = urlSong }

      cycle { ctx.State with ParsingSongState = parsingSongState :: ctx.State.ParsingSongState }

    let inline writeStatus ctx gmc (embed: Embed) cycle =
      match ctx.State.StatusMessageState with
      | Some sms ->
        sms.ModifyAsync(fun mp ->
          mp.Embed <- embed
        ) |> ignore
        cycle { ctx.State with StatusMessageState = None }
      | None ->
        let statusMessage = Utils.sendEmbed gmc embed
        cycle { ctx.State with StatusMessageState = Some statusMessage.Result }

    let inline writeEmbedMessage ctx gmc embed cycle =
      Utils.sendEmbed gmc embed |> ignore
      cycle ctx.State

  module private GuildSystemMessages =
    
    let inline restart ctx gmc cycle =
      failwith "restart"

  module private GuildSystemAsk =
    
    let inline status ctx gmc cycle =
      ctx.Mailbox.Sender() <! $"ParsingSongState: {ctx.State.ParsingSongState} ParsingMessageState: {ctx.State.StatusMessageState}"
      cycle ctx.State
    
  let guildWriterActor guildProxy guild (mb: Actor<_>) =

    let writeProxy = WriteProxy.Create guild

    let rec cycle state = actor {

      let! (msg: obj) = mb.Receive()

      let ctx =
        { State = state; Mailbox = mb; Proxy = guildProxy; Guild = guild; WriteProxy = writeProxy }

      match msg with
      | LifecycleEvent le -> 
        match le with
        | PreStart                    -> return! LifecycleEvent.preStart    ctx cycle
        | PostStop                    -> return! LifecycleEvent.postStop    ctx cycle
        | PreRestart (cause, message) -> return! LifecycleEvent.preRestart  ctx cause message cycle
        | PostRestart cause           -> return! LifecycleEvent.postRestart ctx cause cycle
      | :? GuildWriterMessages as gwm ->
        match gwm with
        | GuildWriterMessages.WriteMessage      (gmc, mc)          -> return! GuildWriterMessages.writeMessage      ctx gmc mc cycle
        | GuildWriterMessages.WritePlay         (gmc, embed, song) -> return! GuildWriterMessages.writePlay         ctx gmc embed song cycle
        | GuildWriterMessages.WriteParsed       (gmc, embed, url)  -> return! GuildWriterMessages.writeParsed       ctx gmc embed url cycle
        | GuildWriterMessages.WriteStatus       (gmc, embed)       -> return! GuildWriterMessages.writeStatus       ctx gmc embed cycle
        | GuildWriterMessages.WriteEmbedMessage (gmc, embed)       -> return! GuildWriterMessages.writeEmbedMessage ctx gmc embed cycle
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

    cycle { ParsingSongState = []; StatusMessageState = None }