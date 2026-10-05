// Port of rust/crates/richtext/src/plain_text.rs
//
// `ActionText::PlainTextConversion`
module Campfire.RichText.PlainText

open System
open System.Text
open Campfire.RichText.RubyExt

let private isList (name: string) = name = "ul" || name = "ol"

let private listDepth (dom: Dom) (node: NodeId) : int =
    dom.Ancestors node |> Seq.filter (fun a -> isList (dom.Name a)) |> Seq.length

let private bulletForLi (dom: Dom) (node: NodeId) : string =
    let list = dom.Ancestors node |> Seq.map dom.Name |> Seq.tryFind isList
    if list = Some "ol" then
        let index =
            match dom.Parent node with
            | ValueSome p ->
                match dom.ElementChildren p |> List.tryFindIndex (fun c -> c = node) with
                | Some i -> i
                | None -> 0
            | ValueNone -> 0
        string (index + 1) + "."
    else
        "•"

let private plainTextForBlock (childValues: string list) : string = chompNewlines (String.Concat childValues) + "\n\n"

let private isSpace (c: char) = c = ' ' || c = '\t' || c = '\n' || c = '\u000b' || c = '\u000c' || c = '\r'

let rec private plainTextFor (dom: Dom) (node: NodeId) : string =
    let childValues () = dom.Children node |> Seq.toArray |> Array.map (plainTextFor dom) |> List.ofArray
    match dom.Name node with
    | "script"
    | "style"
    | "unsupported" -> ""
    | "h1"
    | "p" -> plainTextForBlock (childValues ())
    | "ul"
    | "ol" ->
        let text = plainTextForBlock (childValues ())
        if listDepth dom node > 0 then "\n" + text else text
    | "br" -> "\n"
    // Text nodes, and elements that happen to be named "text" (SVG's), use `node.text`
    | "text" -> chompNewlines (dom.TextContent node)
    | "div" -> chompNewlines (String.Concat(childValues ())) + "\n"
    | "figcaption" -> "[" + chompNewlines (String.Concat(childValues ())) + "]"
    | "blockquote" ->
        let text = plainTextForBlock (childValues ())
        if isBlank text then
            "“”"
        else
            // `text.insert(text.rindex(/\S/) + 1, "”")`, then `text.index(/\S/)` for "“"
            let mutable last = text.Length - 1
            while last >= 0 && isSpace text[last] do
                last <- last - 1
            let mutable first = 0
            while first < text.Length && isSpace text[first] do
                first <- first + 1
            let closed = if last >= 0 then text.Insert(last + 1, "”") else text
            if first < closed.Length then closed.Insert(first, "“") else closed
    | "li" ->
        let bullet = bulletForLi dom node
        let text = chompNewlines (String.Concat(childValues ()))
        let depth = listDepth dom node
        let indentation = if depth > 1 then String.replicate (depth - 1) "  " else ""
        indentation + bullet + " " + text + "\n"
    | _ -> String.Concat(childValues ())

/// `PlainTextConversion.node_to_plain_text`: a bottom-up reduction keyed on each node's name.
let nodeToPlainText (dom: Dom) (node: NodeId) : string = chompNewlines (plainTextFor dom node)
