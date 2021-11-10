namespace Julia.Discord

open Discord.WebSocket

open Akkling

open Julia.Core

open FSharp.UMX

module GuildActor =

  [<NoComparison>]
  [<NoEquality>]
  type private GuildActorContext<'a> = {
    Mailbox: Actor<'a>
    Guild:   SocketGuild
    Proxy:   Sys.ProxyDiscord
    WriteProxy: GuildWriter.WriteProxy
  }

  let [<Literal>] private Restart = "Restart"
  let [<Literal>] private Status = "Status"

  let commandsSystem = [ 
    {| command = Restart
       message = GuildActorMessages.ActorsRestart
       desc    = "Перезапускает акторов гильдии"|}
    {| command = Status
       message = GuildActorMessages.ActorsStatus
       desc    = "Выдает статус акторов гильдии"|}
  ]

  module private LifecycleEvent =

    let inline preStart ctx cycle =
      printfn "Actor for guild %s start" ctx.Guild.Name
      cycle()

    let inline postStop ctx cycle =
      printfn "PostStop"
      ignored()

    let inline preRestart ctx cause message cycle =
      printfn "PreRestart cause: %A message: %A" cause message
      ignored()

    let inline postRestart ctx cause cycle =
      printfn "PostRestart cause: %A" cause
      ignored()

  module private GuildActorMessages =

    let private parseCommands ctx gmc =

      let msgArray: string[] = gmc.Message.Content.ToLower().Split(' ')

      let prefix = ">"

      let helpCommands() =
        if msgArray.[0].ToLower().StartsWith(prefix + "help") then
          let mutable commandsString = ""
          commandsString <- commandsString + "**Комманды барда**\n"
          for command in Bard.commands do
            commandsString <- commandsString + sprintf "**%s**::\t\t %s\n---\n" command.command command.desc
          commandsString <- commandsString + "---\n**Комманды системы**\n"
          for command in commandsSystem do
            commandsString <- commandsString + sprintf "**%s**::\t\t %s\n---\n" command.command command.desc
          (gmc, Utils.answerEmbed %"Help" %(commandsString.Substring(0, commandsString.Length - 4)))
          |> GuildWriterMessages.WriteEmbedMessage
          |> ctx.Proxy.Message.GuildWriter

      let bardCommands() =
        Bard.commands
        |> List.iter (fun commands ->
          if msgArray.[0].ToLower().StartsWith(prefix + commands.command.ToLower()) then
            commands.message gmc
            |> ctx.Proxy.Message.Bard
        )

      let guildCommandsSystem() =
        if gmc.Message.Author.Id = 161572135301677057UL then
          commandsSystem
          |> List.iter (fun commands ->
            if msgArray.[0].ToLower().StartsWith(prefix + commands.command.ToLower()) then
              commands.message gmc
              |> ctx.Proxy.Message.GuildActor
          )

      if msgArray.Length > 0 then
        bardCommands()
        guildCommandsSystem()
        helpCommands()
    
    let inline guildMessage ctx gmc cycle =
      parseCommands ctx gmc
      cycle()

    let inline actorsRestart ctx gmc cycle =

      (gmc,  Utils.answerEmbed %Restart %"Перезапускаются акторы гильдии")
      |> GuildWriterMessages.WriteEmbedMessage
      |> ctx.Proxy.Message.GuildWriter

      GuildSystemMessages.Restart gmc
      |> ctx.Proxy.GuildSystem.Message.Bard

      GuildSystemMessages.Restart gmc
      |> ctx.Proxy.GuildSystem.Message.Songkeeper

      GuildSystemMessages.Restart gmc
      |> ctx.Proxy.GuildSystem.Message.Youtuber

      GuildSystemMessages.Restart gmc
      |> ctx.Proxy.GuildSystem.Message.GuildActor

      GuildSystemMessages.Restart gmc
      |> ctx.Proxy.GuildSystem.Message.GuildWriter

      cycle()

    let inline actorsStatus ctx gmc cycle =

      async {
      
        ctx.WriteProxy.WriteStatus(gmc, Utils.answerEmbed %Status %"Опрашиваются акторы гильдии")

        let bardAnswer:        string =
          ctx.Proxy.GuildSystem.Ask.Bard        <!? GuildSystemAsk.Status gmc
                                                
        let songkeeperAnswer:  string =          
          ctx.Proxy.GuildSystem.Ask.Songkeeper  <!? GuildSystemAsk.Status gmc
                                                
        let youtuberAnswer:    string =          
          ctx.Proxy.GuildSystem.Ask.Youtuber    <!? GuildSystemAsk.Status gmc
                                                
        let guildActorAnswer:  string =          
          ctx.Proxy.GuildSystem.Ask.GuildActor  <!? GuildSystemAsk.Status gmc

        let guildWriterAnswer: string =
          ctx.Proxy.GuildSystem.Ask.GuildWriter <!? GuildSystemAsk.Status gmc

        ctx.WriteProxy.WriteStatus(gmc, Utils.answerEmbed %Status %($"**Bard**\n{bardAnswer}\n**Songkeeper**\n{songkeeperAnswer}\n" +
            $"**Youtuber**\n{youtuberAnswer}\n**GuildActor**\n{guildActorAnswer}\n**GuildWriter**\n{guildWriterAnswer}"))
      } |> Async.Start
        
      cycle()

  module private GuildSystemMessages =
    
    let inline restart ctx gmc cycle =
      failwith "restart"

  module private GuildSystemAsk =
    
    let inline status ctx gmc cycle =
      ctx.Mailbox.Sender() <! "Жив, цел, арёл!"
      cycle()
  
  let guildActor guildProxy guild (mb: Actor<_>) =

    let writeProxy = GuildWriter.WriteProxy.Create guild

    let rec cycle() = actor {

      let! (msg: obj) = mb.Receive()

      let ctx = { Mailbox = mb; Proxy = guildProxy; Guild = guild; WriteProxy = writeProxy }

      match msg with
      | LifecycleEvent le ->
        match le with
        | PreStart                    -> return! LifecycleEvent.preStart    ctx cycle
        | PostStop                    -> return! LifecycleEvent.postStop    ctx cycle
        | PreRestart (cause, message) -> return! LifecycleEvent.preRestart  ctx cause message cycle
        | PostRestart cause           -> return! LifecycleEvent.postRestart ctx cause cycle
      | :? GuildActorMessages as gam ->
        match gam with
        | GuildActorMessages.GuildMessage gmc  -> return! GuildActorMessages.guildMessage  ctx gmc cycle
        | GuildActorMessages.ActorsRestart gmc -> return! GuildActorMessages.actorsRestart ctx gmc cycle
        | GuildActorMessages.ActorsStatus gmc  -> return! GuildActorMessages.actorsStatus  ctx gmc cycle
      | :? GuildSystemMessages as gsm ->
        match gsm with
        | GuildSystemMessages.Restart sm -> return! GuildSystemMessages.restart ctx sm cycle
      | :? GuildSystemAsk as gsa ->
        match gsa with
        | GuildSystemAsk.Status sm -> return! GuildSystemAsk.status ctx sm cycle
          
      | some ->
        printfn "Ignored MSG: %A" some
        return! Unhandled

    }

    cycle()