// The F# half of bin/richtext-differential: what the Rust tools in ../src/bin print, from the F# port.
//
//   Differential parse                  one JSON string per line on stdin, parsed in 20 contexts
//   Differential pipeline CORPUS        each body presented, as plain text, as an editor value and
//                                       for its mentions, with the corpus's users and SGIDs
open System
open System.IO
open System.Text
open System.Text.Json
open Campfire.RichText
open Campfire.RichText.Html5ever
open Campfire.RichText.Tests.CorpusResolver

let private json = JsonSerializerOptions(Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

/// The contexts `parse.rs` parses in, in its order.
let private contexts =
    [ "body"; "table"; "tr"; "td"; "select"; "template"; "head"; "html"; "colgroup"; "tbody"; "caption"; "frameset"; "title"; "textarea"; "script"
      "style"; "plaintext"; "noscript"; "p"; "a" ]

let private quoted (s: string) : string = JsonSerializer.Serialize(s, json)

let private ok (value: string) : string = "{\"ok\":" + quoted value + "}"

let private err (message: string) : string = "{\"err\":" + quoted message + "}"

/// Calls `handle` with each input line, and writes the JSON array of results it returns.
let private forEachInput (handle: string -> string list) =
    use reader = new StreamReader(Console.OpenStandardInput(), UTF8Encoding false)
    use writer = new StreamWriter(Console.OpenStandardOutput(), UTF8Encoding false)
    let mutable line = reader.ReadLine()
    while not (isNull line) do
        match line with
        | null
        | "" -> ()
        | line ->
            match JsonSerializer.Deserialize<string> line with
            | null -> failwith "not a JSON string"
            | input -> writer.WriteLine("[" + String.Join(",", handle input) + "]")
        line <- reader.ReadLine()

let private parse () =
    forEachInput (fun html ->
        contexts
        |> List.map (fun context ->
            let dom = Dom()
            match dom.ParseNodes(html, QualName.unprefixed Ns.Html context) with
            | Ok nodes -> ok (String.Join("", nodes |> Seq.map dom.ToHtml))
            | Error e -> err e.Message))

let private pipeline (corpusPath: string) =
    use corpus = JsonDocument.Parse(File.ReadAllText corpusPath)
    let ctx = { Resolver = resolverOf corpus.RootElement; RequestHost = Some "once.campfire.test" }
    let outcome (r: Result<string, RenderError>) : string =
        match r with
        | Ok value -> ok value
        | Error e -> err e.Message
    forEachInput (fun body ->
        let editable =
            match ActionText.editableValue body ctx with
            | Ok(Some value) -> ok value
            | Ok None -> "{\"none\":true}"
            | Error e -> err e.Message
        let mentioned =
            match ActionText.mentionedUsers body ctx with
            | Ok users -> "{\"ok\":[" + String.Join(",", users |> List.map (fun u -> string u.Id)) + "]}"
            | Error e -> err e.Message
        [ outcome (ActionText.messagePresentation body ctx); outcome (ActionText.toPlainText body ctx); editable; mentioned ])

[<EntryPoint>]
let main argv =
    match argv with
    | [| "parse" |] ->
        parse ()
        0
    | [| "pipeline"; corpus |] ->
        pipeline corpus
        0
    | _ ->
        eprintfn "usage: Differential parse | Differential pipeline CORPUS"
        2
