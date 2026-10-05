// Port of rust/crates/richtext/src/dom.rs
//
// A small arena DOM that behaves like the Nokogiri HTML5 (Gumbo) trees Action Text works on.
//
// Parsing goes through the port of html5ever (Html5ever/), which implements the same WHATWG tree
// construction algorithm as Gumbo. Serialization reproduces Nokogiri's `html_standard_serialize`
// (`Nokogiri::HTML5::Node#write_to`), which is what `ActionText::HtmlConversion.node_to_html`
// and Loofah's `to_html` produce for HTML5 documents.
namespace Campfire.RichText

open System
open System.Text
open Campfire.RichText.Html5ever

type NodeId = int

/// Gumbo's defaults, which Nokogiri raises `ArgumentError` for exceeding
/// (`Nokogiri::Gumbo::DEFAULT_MAX_TREE_DEPTH`, `DEFAULT_MAX_ATTRIBUTES`).
module DomLimits =
    [<Literal>]
    let MaxTreeDepth = 400

    [<Literal>]
    let MaxAttributes = 400

type ParseError =
    | TreeDepthExceeded
    | TooManyAttributes

    member this.Message =
        match this with
        | TreeDepthExceeded -> "Document tree depth limit exceeded"
        | TooManyAttributes -> "Attributes per element limit exceeded"

/// An element's attribute; `Name` is as html5ever made it (an SVG attribute can have a prefix).
[<Sealed>]
type Attr(name: QualName, value: string) =
    member _.Name = name
    member val Value = value with get, set

    /// The attribute's name as Nokogiri reports and serializes it ("xlink:href", "href").
    member _.QualifiedName = QualName.qualified name

    /// Whether `QualifiedName = name`, without building the qualified name.
    member _.HasName(other: string) : bool =
        match name.Prefix with
        | null -> String.Equals(other, name.Local, StringComparison.Ordinal)
        | prefix ->
            other.Length = prefix.Length + 1 + name.Local.Length
            && other.StartsWith(prefix, StringComparison.Ordinal)
            && other[prefix.Length] = ':'
            && other.EndsWith(name.Local, StringComparison.Ordinal)

[<Sealed>]
type ElementData(name: QualName, attrs: ResizeArray<Attr>) =
    member _.Name = name
    member _.Attrs = attrs

/// The text of a text node, which parsing appends to as adjacent text merges.
[<Sealed>]
type TextData(initial: string) =
    let mutable value = initial
    let mutable pending: StringBuilder | null = null

    member _.Value =
        match pending with
        | null -> value
        | sb ->
            value <- sb.ToString()
            pending <- null
            value

    member this.Append(s: string) =
        match pending with
        | null ->
            let sb = StringBuilder(value, Math.Max(16, value.Length * 2))
            sb.Append s |> ignore
            pending <- sb
        | sb -> sb.Append s |> ignore

type NodeData =
    | Document
    | Fragment
    | Element of ElementData
    | Text of TextData
    | Comment of string
    | Doctype of string

[<Sealed>]
type Node(data: NodeData) =
    member _.Data = data
    member val Parent = -1 with get, set
    member val Children = ResizeArray<int>() with get, set

module internal Serialize =
    let isVoidElement (local: string) =
        match local with
        | "area" | "base" | "basefont" | "bgsound" | "br" | "col" | "embed" | "frame" | "hr" | "img" | "input" | "keygen" | "link"
        | "meta" | "param" | "source" | "track" | "wbr" -> true
        | _ -> false

    let isRawTextElement (local: string) =
        match local with
        | "style" | "script" | "xmp" | "iframe" | "noembed" | "noframes" | "plaintext" | "noscript" -> true
        | _ -> false

    let escapeAttribute (value: string) (brackets: bool) (out: StringBuilder) =
        for c in value do
            match c with
            | '&' -> out.Append "&amp;" |> ignore
            | ' ' -> out.Append "&nbsp;" |> ignore
            | '"' -> out.Append "&quot;" |> ignore
            | '<' when brackets -> out.Append "&lt;" |> ignore
            | '>' when brackets -> out.Append "&gt;" |> ignore
            | c -> out.Append c |> ignore

    let escapeText (value: string) (out: StringBuilder) =
        for c in value do
            match c with
            | '&' -> out.Append "&amp;" |> ignore
            | ' ' -> out.Append "&nbsp;" |> ignore
            | '<' -> out.Append "&lt;" |> ignore
            | '>' -> out.Append "&gt;" |> ignore
            | c -> out.Append c |> ignore

    let pushQualifiedName (name: QualName) (out: StringBuilder) =
        match name.Prefix with
        | null -> ()
        | prefix -> out.Append(prefix).Append ':' |> ignore
        out.Append name.Local |> ignore

    let pushTagName (name: QualName) (out: StringBuilder) =
        match name.Ns with
        | Ns.Html
        | Ns.Svg
        | Ns.MathMl -> out.Append name.Local |> ignore
        | _ -> pushQualifiedName name out

/// The arena the parsed trees live in, and Nokogiri's node operations over it.
[<Sealed>]
type Dom() =
    let mutable nodes = ResizeArray<Node>()

    member internal _.Nodes = nodes

    member internal _.Push(data: NodeData) : NodeId =
        nodes.Add(Node data)
        nodes.Count - 1

    member _.Node(id: NodeId) : Node = nodes[id]

    member this.NewFragment() : NodeId = this.Push Fragment

    member this.CreateElement(local: string, attrs: (string * string) list) : NodeId =
        let attrs =
            ResizeArray(attrs |> List.map (fun (k, v) -> Attr(QualName.unprefixed Ns.Empty k, v)))
        this.Push(Element(ElementData(QualName.unprefixed Ns.Html local, attrs)))

    member this.CreateText(text: string) : NodeId = this.Push(Text(TextData text))

    member _.Children(id: NodeId) : ResizeArray<NodeId> = nodes[id].Children

    member _.Parent(id: NodeId) : NodeId voption =
        let p = nodes[id].Parent
        if p < 0 then ValueNone else ValueSome p

    member _.Element(id: NodeId) : ElementData voption =
        match nodes[id].Data with
        | Element e -> ValueSome e
        | _ -> ValueNone

    member _.IsElement(id: NodeId) : bool =
        match nodes[id].Data with
        | Element _ -> true
        | _ -> false

    member _.IsText(id: NodeId) : bool =
        match nodes[id].Data with
        | Text _ -> true
        | _ -> false

    /// Nokogiri's `Node#name`: the local name for elements, "text", "comment" and so on otherwise.
    member _.Name(id: NodeId) : string =
        match nodes[id].Data with
        | Element e -> e.Name.Local
        | Text _ -> "text"
        | Comment _ -> "comment"
        | Fragment -> "#document-fragment"
        | Document -> "document"
        | Doctype n -> n

    /// The element's local name, if `id` is an element.
    member this.LocalName(id: NodeId) : string voption =
        match nodes[id].Data with
        | Element e -> ValueSome e.Name.Local
        | _ -> ValueNone

    member this.IsNamed(id: NodeId, local: string) : bool =
        match nodes[id].Data with
        | Element e -> String.Equals(e.Name.Local, local, StringComparison.Ordinal)
        | _ -> false

    member this.IsHtmlElement(id: NodeId) : bool =
        match nodes[id].Data with
        | Element e -> e.Name.Ns = Ns.Html
        | _ -> false

    member this.Attr(id: NodeId, name: string) : string voption =
        match nodes[id].Data with
        | Element e ->
            let mutable result = ValueNone
            let mutable i = 0
            while result.IsNone && i < e.Attrs.Count do
                if e.Attrs[i].HasName name then result <- ValueSome e.Attrs[i].Value
                i <- i + 1
            result
        | _ -> ValueNone

    member this.HasAttr(id: NodeId, name: string) : bool = (this.Attr(id, name)).IsSome

    /// Nokogiri's `node[name] = value`: updates in place, or appends a new attribute.
    member this.SetAttr(id: NodeId, name: string, value: string) =
        match nodes[id].Data with
        | Element e ->
            match e.Attrs |> Seq.tryFind (fun a -> a.HasName name) with
            | Some attr -> attr.Value <- value
            | None -> e.Attrs.Add(Attr(QualName.unprefixed Ns.Empty name, value))
        | _ -> ()

    member this.RemoveAttr(id: NodeId, name: string) : string voption =
        match nodes[id].Data with
        | Element e ->
            let index = e.Attrs.FindIndex(fun a -> a.HasName name)
            if index < 0 then
                ValueNone
            else
                let value = e.Attrs[index].Value
                e.Attrs.RemoveAt index
                ValueSome value
        | _ -> ValueNone

    member this.Attrs(id: NodeId) : (string * string) list =
        match nodes[id].Data with
        | Element e -> e.Attrs |> Seq.map (fun a -> a.QualifiedName, a.Value) |> List.ofSeq
        | _ -> []

    /// Element children only (Nokogiri's `Node#elements`).
    member this.ElementChildren(id: NodeId) : NodeId list =
        nodes[id].Children |> Seq.filter this.IsElement |> List.ofSeq

    /// All descendants in document order, excluding `id` itself.
    member this.Descendants(id: NodeId) : ResizeArray<NodeId> =
        let out = ResizeArray<NodeId>()
        let stack = Collections.Generic.Stack<NodeId>()
        let children = nodes[id].Children
        for i in children.Count - 1 .. -1 .. 0 do
            stack.Push children[i]
        while stack.Count > 0 do
            let n = stack.Pop()
            out.Add n
            let children = nodes[n].Children
            for i in children.Count - 1 .. -1 .. 0 do
                stack.Push children[i]
        out

    member this.Ancestors(id: NodeId) : ResizeArray<NodeId> =
        let out = ResizeArray<NodeId>()
        let mutable current = nodes[id].Parent
        while current >= 0 do
            out.Add current
            current <- nodes[current].Parent
        out

    member this.Detach(id: NodeId) =
        let parent = nodes[id].Parent
        if parent >= 0 then
            nodes[id].Parent <- -1
            nodes[parent].Children.RemoveAll(fun c -> c = id) |> ignore

    member this.Append(parent: NodeId, child: NodeId) =
        this.Detach child
        nodes[child].Parent <- parent
        nodes[parent].Children.Add child

    member this.InsertBefore(reference: NodeId, newNode: NodeId) =
        this.Detach newNode
        let parent = nodes[reference].Parent
        if parent < 0 then failwith "reference node has a parent"
        let index = nodes[parent].Children.IndexOf reference
        nodes[newNode].Parent <- parent
        nodes[parent].Children.Insert(index, newNode)

    /// The text content of a node, as libxml2's `xmlNodeGetContent` computes it.
    member this.TextContent(id: NodeId) : string =
        match nodes[id].Data with
        | Text t -> t.Value
        | Comment t -> t
        | _ ->
            let out = StringBuilder()
            for d in this.Descendants id do
                match nodes[d].Data with
                | Text t -> out.Append t.Value |> ignore
                | _ -> ()
            out.ToString()

    member this.Text(id: NodeId) : string voption =
        match nodes[id].Data with
        | Text t -> ValueSome t.Value
        | _ -> ValueNone

    member this.DeepClone(id: NodeId) : NodeId =
        let data =
            match nodes[id].Data with
            | Element e -> Element(ElementData(e.Name, ResizeArray(e.Attrs |> Seq.map (fun a -> Attr(a.Name, a.Value)))))
            | Text t -> Text(TextData t.Value)
            | other -> other
        let copy = this.Push data
        for child in nodes[id].Children |> Seq.toArray do
            let childCopy = this.DeepClone child
            nodes[childCopy].Parent <- copy
            nodes[copy].Children.Add childCopy
        copy

    /// `Dom::clone`: the whole arena, copied.
    member this.Clone() : Dom =
        let copy = Dom()
        for node in nodes do
            let data =
                match node.Data with
                | Element e -> Element(ElementData(e.Name, ResizeArray(e.Attrs |> Seq.map (fun a -> Attr(a.Name, a.Value)))))
                | Text t -> Text(TextData t.Value)
                | other -> other
            let n = Node data
            n.Parent <- node.Parent
            n.Children <- ResizeArray(node.Children)
            copy.Nodes.Add n
        copy

    // --- Parsing -----------------------------------------------------------------------------

    /// `Nokogiri::HTML5::Document#fragment(html)`: a new fragment parsed in a `body` context.
    member this.ParseFragment(html: string) : Result<NodeId, ParseError> =
        let fragment = this.NewFragment()
        match this.ParseNodes(html, DomContext.body) with
        | Error e -> Error e
        | Ok nodes ->
            for node in nodes do
                this.Append(fragment, node)
            Ok fragment

    /// The context Nokogiri uses when parsing markup for a node (`Node#fragment`).
    member this.ContextFor(id: NodeId) : QualName =
        match nodes[id].Data with
        | Element e -> e.Name
        // Gumbo falls back to a body context for anything that isn't an element
        | _ -> DomContext.body

    /// Parses `html` in the given context and returns the resulting top-level nodes, detached.
    member this.ParseNodes(html: string, context: QualName) : Result<ResizeArray<NodeId>, ParseError> =
        let sink = DomSink this
        let contextElement = (sink :> ITreeSink).CreateElement(context, [||])
        let treeBuilder = TreeBuilder(sink, contextElement)
        treeBuilder.StartFragment()
        let tokenizerState = treeBuilder.TokenizerStateForContextElem()
        // Gumbo drops a byte order mark only at the start. html5ever drops one at the start of
        // every feed, and input is fed again after each </script>.
        let text = if html.Length > 0 && html[0] = '﻿' then html.Substring 1 else html
        let input = Input text
        let depthLimit = DepthLimit(treeBuilder, input, DomLimits.MaxTreeDepth + 1)
        let tokenizer = Tokenizer(depthLimit, tokenizerState, DomLimits.MaxAttributes)
        tokenizer.Feed input
        tokenizer.End()
        if depthLimit.Exceeded then
            Error TreeDepthExceeded
        elif tokenizer.TooManyAttributes then
            Error TooManyAttributes
        else
            // html5ever puts the fragment's nodes under an `html` element beneath the document.
            let document = sink.Document
            let root =
                nodes[document].Children
                |> Seq.tryFind (fun c ->
                    match nodes[c].Data with
                    | Element e -> e.Name.Local = "html"
                    | _ -> false)
                |> Option.defaultValue document
            let top = ResizeArray(nodes[root].Children)
            for n in top do
                nodes[n].Parent <- -1
            Ok top

    /// Nokogiri's `node.inner_html = html` (via `children=`), parsed in the node's context.
    member this.SetInnerHtml(id: NodeId, html: string) : Result<unit, ParseError> =
        let context = this.ContextFor id
        match this.ParseNodes(html, context) with
        | Error e -> Error e
        | Ok newChildren ->
            for child in nodes[id].Children do
                nodes[child].Parent <- -1
            nodes[id].Children.Clear()
            for child in newChildren do
                this.Append(id, child)
            Ok()

    /// Nokogiri's `node.replace(html)`: the markup is parsed in the context of the node's parent.
    member this.ReplaceWithHtml(id: NodeId, html: string) : Result<unit, ParseError> =
        match this.Parent id with
        | ValueNone -> Ok()
        | ValueSome parent ->
            let context = this.ContextFor parent
            match this.ParseNodes(html, context) with
            | Error e -> Error e
            | Ok replacements ->
                for n in replacements do
                    this.InsertBefore(id, n)
                this.Detach id
                Ok()

    /// Replaces a node with other (detached) nodes.
    member this.ReplaceWithNodes(id: NodeId, replacements: NodeId seq) =
        if nodes[id].Parent >= 0 then
            for n in replacements do
                this.InsertBefore(id, n)
            this.Detach id

    // --- Serialization -----------------------------------------------------------------------

    /// `node.to_html` for an HTML5 document: the node itself (children only for a fragment).
    member this.ToHtml(id: NodeId) : string = this.Serialize(id, false)

    /// `to_html`, but with `<` and `>` escaped in attribute values as well, as the current HTML
    /// serialization algorithm does. Nokogiri leaves them raw, which lets auto_link's regular
    /// expressions mistake an attribute value for text.
    member this.ToHtmlWithEscapedAttributeBrackets(id: NodeId) : string = this.Serialize(id, true)

    member private this.Serialize(id: NodeId, brackets: bool) : string =
        let out = StringBuilder()
        match nodes[id].Data with
        | Fragment
        | Document -> this.SerializeChildren(id, brackets, out)
        | _ -> this.SerializeNode(id, brackets, out)
        out.ToString()

    member this.InnerHtml(id: NodeId) : string =
        let out = StringBuilder()
        this.SerializeChildren(id, false, out)
        out.ToString()

    member private this.SerializeChildren(id: NodeId, brackets: bool, out: StringBuilder) =
        for child in nodes[id].Children do
            this.SerializeNode(child, brackets, out)

    member private this.SerializeNode(id: NodeId, brackets: bool, out: StringBuilder) =
        match nodes[id].Data with
        | Element e ->
            out.Append '<' |> ignore
            Serialize.pushTagName e.Name out
            for attr in e.Attrs do
                out.Append ' ' |> ignore
                Serialize.pushQualifiedName attr.Name out
                out.Append "=\"" |> ignore
                Serialize.escapeAttribute attr.Value brackets out
                out.Append '"' |> ignore
            out.Append '>' |> ignore
            if not (e.Name.Ns = Ns.Html && Serialize.isVoidElement e.Name.Local) then
                this.SerializeChildren(id, brackets, out)
                out.Append "</" |> ignore
                Serialize.pushTagName e.Name out
                out.Append '>' |> ignore
        | Text text ->
            let parent = nodes[id].Parent
            let raw =
                parent >= 0
                && (match nodes[parent].Data with
                    | Element p -> p.Name.Ns = Ns.Html && Serialize.isRawTextElement p.Name.Local
                    | _ -> false)
            if raw then out.Append text.Value |> ignore else Serialize.escapeText text.Value out
        | Comment text -> out.Append("<!--").Append(text).Append "-->" |> ignore
        | Doctype name -> out.Append("<!DOCTYPE ").Append(name).Append '>' |> ignore
        | Fragment
        | Document -> this.SerializeChildren(id, brackets, out)

/// The context a fragment is parsed in: Nokogiri parses `Document#fragment` in a `body`
/// context and `Node#fragment` (used by `replace`, `inner_html=`) in the node's own context.
and DomContext =
    static member body: QualName = QualName.unprefixed Ns.Html "body"

/// html5ever's `TreeSink` over a `Dom`: the tree the parser builds goes straight into the arena.
and [<Sealed>] private DomSink(dom: Dom) =
    let nodes = dom.Nodes
    let document = dom.Push Document

    member private _.Detach(id: int) =
        let parent = nodes[id].Parent
        if parent >= 0 then
            nodes[id].Parent <- -1
            nodes[parent].Children.RemoveAll(fun c -> c = id) |> ignore

    member private this.InsertAt(parent: int, index: int, child: NodeOrText) =
        match child with
        | AppendNode node ->
            this.Detach node
            nodes[node].Parent <- parent
            nodes[parent].Children.Insert(index, node)
        | AppendText text ->
            // Adjacent text merges into the preceding text node, as in the DOM
            let merged =
                index > 0
                && (match nodes[nodes[parent].Children[index - 1]].Data with
                    | Text existing ->
                        existing.Append text
                        true
                    | _ -> false)
            if not merged then
                let node = dom.Push(Text(TextData text))
                nodes[node].Parent <- parent
                nodes[parent].Children.Insert(index, node)

    /// The `html` element html5ever puts the fragment's nodes under.
    member private _.IsFragmentRoot(id: int) = nodes[id].Parent = document

    member _.Document = document

    interface ITreeSink with
        member _.Document = document

        member _.ElemName(target) =
            match nodes[target].Data with
            | Element e -> e.Name
            | _ -> failwith "not an element"

        member _.CreateElement(name, attrs) =
            // The tokenizer has already dropped duplicate attributes
            let attrs = ResizeArray(attrs |> Array.map (fun a -> Attr(a.Name, a.Value)))
            dom.Push(Element(ElementData(name, attrs)))

        member _.CreateComment(text) = dom.Push(Comment text)

        member this.Append(parent, child) = this.InsertAt(parent, nodes[parent].Children.Count, child)

        member this.AppendBasedOnParentNode(element, prevElement, child) =
            if nodes[element].Parent >= 0 then
                (this :> ITreeSink).AppendBeforeSibling(element, child)
            else
                (this :> ITreeSink).Append(prevElement, child)

        member _.GetTemplateContents(target) =
            // libxml2 has no template contents: Nokogiri keeps them as ordinary children
            target

        member this.AppendBeforeSibling(sibling, newNode) =
            let parent = nodes[sibling].Parent
            if parent < 0 then failwith "sibling has a parent"
            // From the end: foster parenting inserts before an open table, which is its parent's last
            // child, so a body of thousands of misplaced elements in a table stays linear.
            let index = nodes[parent].Children.LastIndexOf sibling
            this.InsertAt(parent, index, newNode)

        member this.AddAttrsIfMissing(target, attrs) =
            // An <html> tag in the body gives its attributes to the fragment's root <html> element,
            // which parse_nodes never reads. Merging them there would compare each one with all the
            // root had collected, so a body of <html> tags would take quadratic time.
            if not (this.IsFragmentRoot target) then
                match nodes[target].Data with
                | Element e ->
                    for a in attrs do
                        if not (e.Attrs |> Seq.exists (fun existing -> existing.Name = a.Name)) then
                            e.Attrs.Add(Attr(a.Name, a.Value))
                | _ -> ()

        member this.RemoveFromParent(target) = this.Detach target

        member this.ReparentChildren(node, newParent) =
            let children = nodes[node].Children
            nodes[node].Children <- ResizeArray<int>()
            for child in children do
                nodes[child].Parent <- -1
                (this :> ITreeSink).Append(newParent, AppendNode child)
