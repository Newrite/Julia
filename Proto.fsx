#r "nuget: FSharp.Data"

open FSharp.Data

let rand = new System.Random()
rand.Next(0, 1095)

let results: HtmlDocument = HtmlDocument.Load($"https://copypastas.ru/copypasta/{rand.Next(0, 1095)}/")
let node = results.Elements().Head.CssSelect("div[class*='_2UxTN']").Head
let pasta = node.ToString().Split("\n").[1].Replace("<div>", "").Replace("</div>", "")

printfn "Pasts: %s" pasta