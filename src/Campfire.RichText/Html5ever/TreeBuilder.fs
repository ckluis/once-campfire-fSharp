// Port of rust/crates/richtext/vendor/html5ever/src/tree_builder/{mod,rules,tag_sets,types}.rs
// (html5ever 0.35 as vendored: scripting off, so <noscript> content is markup), with the tree
// depth limit of rust/crates/richtext/src/dom.rs (`DepthLimit`) beside it.
//
// What a fragment parse never reaches is left out: the initial insertion mode and doctype
// handling (a fragment starts in the mode its context element resets to, so quirks mode stays
// off), form association, and the parse errors the DOM sink drops.
namespace Campfire.RichText.Html5ever

open System.Collections.Generic

type InsertionMode =
    | Initial
    | BeforeHtml
    | BeforeHead
    | InHead
    | InHeadNoscript
    | AfterHead
    | InBody
    | Text
    | InTable
    | InTableText
    | InCaption
    | InColumnGroup
    | InTableBody
    | InRow
    | InCell
    | InSelect
    | InSelectInTable
    | InTemplate
    | AfterBody
    | InFrameset
    | AfterFrameset
    | AfterAfterBody
    | AfterAfterFrameset

type SplitStatus =
    | NotSplit
    | Whitespace
    | NotWhitespace

/// The tokens the tree builder's rules see; everything else is handled before them.
type Token =
    | TagToken of Tag
    | CommentToken of string
    | Characters of SplitStatus * string
    | NullCharacter
    | Eof

type ProcessResult =
    | Done
    | DoneAckSelfClosing
    | SplitWhitespace of string
    | Reprocess of InsertionMode * Token
    | ScriptEnd of int
    | ToPlaintext
    | ToRawData of RawKind

type FormatEntry =
    | Marker
    | FormatElement of handle: int * tag: Tag

type InsertionPoint =
    /// Insert as last child in this parent.
    | LastChild of int
    /// Insertion point is decided based on existence of element's parent node.
    | TableFosterParenting of element: int * prevElement: int

type Bookmark =
    | Replace of int
    | InsertAfter of int

/// `tag_sets.rs`: sets of element names, tested on an element's name and namespace.
module internal TagSets =
    let private ofList (names: string list) = HashSet<string>(names)

    let private htmlDefaultScopeNames =
        ofList [ "applet"; "caption"; "html"; "table"; "td"; "th"; "marquee"; "object"; "template" ]

    let mathmlTextIntegrationPoint (q: QualName) =
        q.Ns = Ns.MathMl && (q.Local = "mi" || q.Local = "mo" || q.Local = "mn" || q.Local = "ms" || q.Local = "mtext")

    /// https://html.spec.whatwg.org/multipage/#html-integration-point
    let svgHtmlIntegrationPoint (q: QualName) =
        q.Ns = Ns.Svg && (q.Local = "foreignObject" || q.Local = "desc" || q.Local = "title")

    let defaultScope (q: QualName) =
        (q.Ns = Ns.Html && htmlDefaultScopeNames.Contains q.Local) || mathmlTextIntegrationPoint q || svgHtmlIntegrationPoint q

    let listItemScope (q: QualName) = defaultScope q || (q.Ns = Ns.Html && (q.Local = "ol" || q.Local = "ul"))
    let buttonScope (q: QualName) = defaultScope q || (q.Ns = Ns.Html && q.Local = "button")

    let private tableScopeNames = ofList [ "html"; "table"; "template" ]
    let tableScope (q: QualName) = q.Ns = Ns.Html && tableScopeNames.Contains q.Local

    /// Every element but an `optgroup` or `option`.
    let selectScope (q: QualName) = not (q.Ns = Ns.Html && (q.Local = "optgroup" || q.Local = "option"))

    let private tableBodyContextNames = ofList [ "tbody"; "tfoot"; "thead"; "template"; "html" ]
    let tableBodyContext (q: QualName) = q.Ns = Ns.Html && tableBodyContextNames.Contains q.Local

    let private tableRowContextNames = ofList [ "tr"; "template"; "html" ]
    let tableRowContext (q: QualName) = q.Ns = Ns.Html && tableRowContextNames.Contains q.Local

    let tdTh (q: QualName) = q.Ns = Ns.Html && (q.Local = "td" || q.Local = "th")

    let private cursoryImpliedEndNames = ofList [ "dd"; "dt"; "li"; "option"; "optgroup"; "p"; "rb"; "rp"; "rt"; "rtc" ]
    let cursoryImpliedEnd (q: QualName) = q.Ns = Ns.Html && cursoryImpliedEndNames.Contains q.Local

    let private thoroughImpliedEndNames =
        ofList [ "dd"; "dt"; "li"; "option"; "optgroup"; "p"; "rb"; "rp"; "rt"; "rtc"; "caption"; "colgroup"; "tbody"; "td"; "tfoot"; "th"; "thead"; "tr" ]

    let thoroughImpliedEnd (q: QualName) = q.Ns = Ns.Html && thoroughImpliedEndNames.Contains q.Local

    let headingTag (q: QualName) =
        q.Ns = Ns.Html && (q.Local = "h1" || q.Local = "h2" || q.Local = "h3" || q.Local = "h4" || q.Local = "h5" || q.Local = "h6")

    let private specialTagNames =
        ofList
            [ "address"; "applet"; "area"; "article"; "aside"; "base"; "basefont"; "bgsound"; "blockquote"; "body"
              "br"; "button"; "caption"; "center"; "col"; "colgroup"; "dd"; "details"; "dir"; "div"; "dl"; "dt"; "embed"
              "fieldset"; "figcaption"; "figure"; "footer"; "form"; "frame"; "frameset"; "h1"; "h2"; "h3"; "h4"; "h5"
              "h6"; "head"; "header"; "hgroup"; "hr"; "html"; "iframe"; "img"; "input"; "isindex"; "li"; "link"
              "listing"; "main"; "marquee"; "menu"; "meta"; "nav"; "noembed"; "noframes"; "noscript"
              "object"; "ol"; "p"; "param"; "plaintext"; "pre"; "script"; "section"; "select"; "source"; "style"
              "summary"; "table"; "tbody"; "td"; "template"; "textarea"; "tfoot"; "th"; "thead"; "title"; "tr"; "track"
              "ul"; "wbr"; "xmp" ]

    let specialTag (q: QualName) = q.Ns = Ns.Html && specialTagNames.Contains q.Local

    let fosterTarget (q: QualName) =
        q.Ns = Ns.Html && (q.Local = "table" || q.Local = "tbody" || q.Local = "tfoot" || q.Local = "thead" || q.Local = "tr")

    let tableOuter (q: QualName) = q.Ns = Ns.Html && (q.Local = "table" || q.Local = "tbody" || q.Local = "tfoot")

    let tableOuterOrRow (q: QualName) = fosterTarget q

/// The names html5ever's tree builder rewrites for SVG, MathML and foreign attributes.
module internal ForeignNames =
    let svgTagNames =
        dict
            [ "altglyph", "altGlyph"; "altglyphdef", "altGlyphDef"; "altglyphitem", "altGlyphItem"
              "animatecolor", "animateColor"; "animatemotion", "animateMotion"; "animatetransform", "animateTransform"
              "clippath", "clipPath"; "feblend", "feBlend"; "fecolormatrix", "feColorMatrix"
              "fecomponenttransfer", "feComponentTransfer"; "fecomposite", "feComposite"
              "feconvolvematrix", "feConvolveMatrix"; "fediffuselighting", "feDiffuseLighting"
              "fedisplacementmap", "feDisplacementMap"; "fedistantlight", "feDistantLight"; "fedropshadow", "feDropShadow"
              "feflood", "feFlood"; "fefunca", "feFuncA"; "fefuncb", "feFuncB"; "fefuncg", "feFuncG"; "fefuncr", "feFuncR"
              "fegaussianblur", "feGaussianBlur"; "feimage", "feImage"; "femerge", "feMerge"; "femergenode", "feMergeNode"
              "femorphology", "feMorphology"; "feoffset", "feOffset"; "fepointlight", "fePointLight"
              "fespecularlighting", "feSpecularLighting"; "fespotlight", "feSpotLight"; "fetile", "feTile"
              "feturbulence", "feTurbulence"; "foreignobject", "foreignObject"; "glyphref", "glyphRef"
              "lineargradient", "linearGradient"; "radialgradient", "radialGradient"; "textpath", "textPath" ]

    let svgAttributeNames =
        dict
            [ "attributename", "attributeName"; "attributetype", "attributeType"; "basefrequency", "baseFrequency"
              "baseprofile", "baseProfile"; "calcmode", "calcMode"; "clippathunits", "clipPathUnits"
              "diffuseconstant", "diffuseConstant"; "edgemode", "edgeMode"; "filterunits", "filterUnits"
              "glyphref", "glyphRef"; "gradienttransform", "gradientTransform"; "gradientunits", "gradientUnits"
              "kernelmatrix", "kernelMatrix"; "kernelunitlength", "kernelUnitLength"; "keypoints", "keyPoints"
              "keysplines", "keySplines"; "keytimes", "keyTimes"; "lengthadjust", "lengthAdjust"
              "limitingconeangle", "limitingConeAngle"; "markerheight", "markerHeight"; "markerunits", "markerUnits"
              "markerwidth", "markerWidth"; "maskcontentunits", "maskContentUnits"; "maskunits", "maskUnits"
              "numoctaves", "numOctaves"; "pathlength", "pathLength"; "patterncontentunits", "patternContentUnits"
              "patterntransform", "patternTransform"; "patternunits", "patternUnits"; "pointsatx", "pointsAtX"
              "pointsaty", "pointsAtY"; "pointsatz", "pointsAtZ"; "preservealpha", "preserveAlpha"
              "preserveaspectratio", "preserveAspectRatio"; "primitiveunits", "primitiveUnits"; "refx", "refX"
              "refy", "refY"; "repeatcount", "repeatCount"; "repeatdur", "repeatDur"
              "requiredextensions", "requiredExtensions"; "requiredfeatures", "requiredFeatures"
              "specularconstant", "specularConstant"; "specularexponent", "specularExponent"
              "spreadmethod", "spreadMethod"; "startoffset", "startOffset"; "stddeviation", "stdDeviation"
              "stitchtiles", "stitchTiles"; "surfacescale", "surfaceScale"; "systemlanguage", "systemLanguage"
              "tablevalues", "tableValues"; "targetx", "targetX"; "targety", "targetY"; "textlength", "textLength"
              "viewbox", "viewBox"; "viewtarget", "viewTarget"; "xchannelselector", "xChannelSelector"
              "ychannelselector", "yChannelSelector"; "zoomandpan", "zoomAndPan" ]

    /// `qualname!("xlink" xlink "href")` and friends. `xmlns` gets the empty prefix, not no prefix.
    let foreignAttributes : IDictionary<string, QualName> =
        dict
            [ "xlink:actuate", QualName.create "xlink" Ns.Xlink "actuate"
              "xlink:arcrole", QualName.create "xlink" Ns.Xlink "arcrole"
              "xlink:href", QualName.create "xlink" Ns.Xlink "href"
              "xlink:role", QualName.create "xlink" Ns.Xlink "role"
              "xlink:show", QualName.create "xlink" Ns.Xlink "show"
              "xlink:title", QualName.create "xlink" Ns.Xlink "title"
              "xlink:type", QualName.create "xlink" Ns.Xlink "type"
              "xml:lang", QualName.create "xml" Ns.Xml "lang"
              "xml:space", QualName.create "xml" Ns.Xml "space"
              "xmlns", QualName.create "" Ns.Xmlns "xmlns"
              "xmlns:xlink", QualName.create "xmlns" Ns.Xmlns "xlink" ]

open TagSets

/// html5ever's `TreeBuilder` for a fragment parse, over a `ITreeSink`.
[<Sealed; AllowNullLiteral>]
type TreeBuilder(sink: ITreeSink, contextElem: int) =
    let openElems = ResizeArray<int>()
    let activeFormatting = ResizeArray<FormatEntry>()
    let templateModes = ResizeArray<InsertionMode>()
    let pendingTableText = ResizeArray<struct (SplitStatus * string)>()
    let docHandle = sink.Document
    let mutable mode = InsertionMode.Initial
    let mutable origMode = InsertionMode.Initial
    let mutable headElem = -1
    let mutable formElem = -1
    let mutable framesetOk = true
    let mutable ignoreLf = false
    let mutable fosterParenting = false

    // --- Small helpers -----------------------------------------------------------------------

    member private this.ElemName(h: int) : QualName = sink.ElemName h

    member private this.CurrentNode: int = openElems[openElems.Count - 1]

    member private this.AdjustedCurrentNode: int =
        if openElems.Count = 1 && contextElem >= 0 then contextElem else this.CurrentNode

    member private this.HtmlElemNamed(elem: int, name: string) : bool =
        let q = this.ElemName elem
        q.Ns = Ns.Html && q.Local = name

    member private this.CurrentNodeNamed(name: string) = this.HtmlElemNamed(this.CurrentNode, name)

    member private this.InHtmlElemNamed(name: string) : bool =
        let mutable found = false
        for e in openElems do
            if not found && this.HtmlElemNamed(e, name) then found <- true
        found

    member private this.ElemIn(elem: int, set: QualName -> bool) : bool = set (this.ElemName elem)

    member private this.CurrentNodeIn(set: QualName -> bool) : bool = set (this.ElemName this.CurrentNode)

    member private this.InScope(scope: QualName -> bool, pred: int -> bool) : bool =
        let mutable i = openElems.Count - 1
        let mutable result = false
        let mutable fin = false
        while not fin && i >= 0 do
            let node = openElems[i]
            if pred node then
                result <- true
                fin <- true
            elif scope (this.ElemName node) then
                fin <- true
            else
                i <- i - 1
        result

    member private this.InScopeNamed(scope: QualName -> bool, name: string) : bool =
        this.InScope(scope, (fun elem -> this.HtmlElemNamed(elem, name)))

    member private this.Push(elem: int) = openElems.Add elem

    member private this.Pop() : int =
        let elem = openElems[openElems.Count - 1]
        openElems.RemoveAt(openElems.Count - 1)
        elem

    member private this.RemoveFromStack(elem: int) =
        let position = openElems.LastIndexOf elem
        if position >= 0 then openElems.RemoveAt position

    member private this.PositionInActiveFormatting(element: int) : int =
        let mutable result = -1
        let mutable i = 0
        while result < 0 && i < activeFormatting.Count do
            match activeFormatting[i] with
            | FormatElement(handle, _) when handle = element -> result <- i
            | _ -> ()
            i <- i + 1
        result

    member private this.IsMarkerOrOpen(entry: FormatEntry) : bool =
        match entry with
        | Marker -> true
        | FormatElement(node, _) -> openElems.LastIndexOf node >= 0

    // --- Insertion ---------------------------------------------------------------------------

    /// https://html.spec.whatwg.org/multipage/#appropriate-place-for-inserting-a-node
    member private this.AppropriatePlaceForInsertion(overrideTarget: int) : InsertionPoint =
        let target = if overrideTarget >= 0 then overrideTarget else this.CurrentNode
        if not (fosterParenting && this.ElemIn(target, fosterTarget)) then
            // No foster parenting (inside a template, or the common case).
            if this.HtmlElemNamed(target, "template") then LastChild(sink.GetTemplateContents target) else LastChild target
        else
            // Foster parenting
            let mutable result = None
            let mutable i = openElems.Count - 1
            while result.IsNone && i >= 0 do
                let elem = openElems[i]
                if this.HtmlElemNamed(elem, "template") then
                    result <- Some(LastChild(sink.GetTemplateContents elem))
                elif this.HtmlElemNamed(elem, "table") then
                    result <- Some(TableFosterParenting(elem, openElems[i - 1]))
                i <- i - 1
            match result with
            | Some r -> r
            | None -> LastChild openElems[0]

    member private this.InsertAt(point: InsertionPoint, child: NodeOrText) =
        match point with
        | LastChild parent -> sink.Append(parent, child)
        | TableFosterParenting(element, prevElement) -> sink.AppendBasedOnParentNode(element, prevElement, child)

    member private this.InsertAppropriately(child: NodeOrText, overrideTarget: int) =
        this.InsertAt(this.AppropriatePlaceForInsertion overrideTarget, child)

    member private this.AppendText(text: string) : ProcessResult =
        this.InsertAppropriately(AppendText text, -1)
        Done

    member private this.AppendComment(text: string) : ProcessResult =
        let comment = sink.CreateComment text
        this.InsertAppropriately(AppendNode comment, -1)
        Done

    member private this.AppendCommentToDoc(text: string) : ProcessResult =
        let comment = sink.CreateComment text
        sink.Append(docHandle, AppendNode comment)
        Done

    member private this.AppendCommentToHtml(text: string) : ProcessResult =
        let comment = sink.CreateComment text
        sink.Append(openElems[0], AppendNode comment)
        Done

    member private this.CreateRoot(attrs: TagAttribute[]) =
        let elem = sink.CreateElement(QualName.unprefixed Ns.Html "html", attrs)
        this.Push elem
        sink.Append(docHandle, AppendNode elem)

    /// https://html.spec.whatwg.org/multipage/#create-an-element-for-the-token
    member private this.InsertElement(push: bool, ns: Ns, name: string, attrs: TagAttribute[]) : int =
        let elem = sink.CreateElement(QualName.unprefixed ns name, attrs)
        let point = this.AppropriatePlaceForInsertion -1
        this.InsertAt(point, AppendNode elem)
        if push then this.Push elem
        elem

    member private this.InsertElementFor(tag: Tag) : int = this.InsertElement(true, Ns.Html, tag.Name, tag.Attrs)

    member private this.InsertAndPopElementFor(tag: Tag) : int = this.InsertElement(false, Ns.Html, tag.Name, tag.Attrs)

    member private this.InsertPhantom(name: string) : int = this.InsertElement(true, Ns.Html, name, [||])

    /// https://html.spec.whatwg.org/multipage/parsing.html#insert-an-element-at-the-adjusted-insertion-location
    member private this.InsertForeignElement(tag: Tag, ns: Ns, onlyAddToElementStack: bool) : int =
        let point = this.AppropriatePlaceForInsertion -1
        let elem = sink.CreateElement(QualName.unprefixed ns tag.Name, tag.Attrs)
        if not onlyAddToElementStack then this.InsertAt(point, AppendNode elem)
        this.Push elem
        elem

    /// A start tag whose tag name is "template": would a declarative shadow root be attached?
    member private this.ShouldAttachDeclarativeShadow(tag: Tag) : bool =
        // The DOM sink allows declarative shadow roots, so it takes a `shadowrootmode` and a
        // template that isn't the topmost element of the stack.
        let isShadowRootMode =
            tag.Attrs |> Array.exists (fun a -> a.Name.Local = "shadowrootmode" && (a.Value = "open" || a.Value = "closed"))
        isShadowRootMode && openElems.Count > 1

    // --- Formatting elements -----------------------------------------------------------------

    /// <https://html.spec.whatwg.org/#reconstruct-the-active-formatting-elements>
    member private this.ReconstructActiveFormattingElements() =
        if activeFormatting.Count > 0 && not (this.IsMarkerOrOpen activeFormatting[activeFormatting.Count - 1]) then
            // Rewind to the first entry that has to be reopened.
            let mutable entryIndex = activeFormatting.Count - 1
            let mutable rewinding = true
            while rewinding do
                if entryIndex = 0 then
                    rewinding <- false
                else
                    entryIndex <- entryIndex - 1
                    if this.IsMarkerOrOpen activeFormatting[entryIndex] then
                        entryIndex <- entryIndex + 1
                        rewinding <- false
            let mutable creating = true
            while creating do
                let tag =
                    match activeFormatting[entryIndex] with
                    | FormatElement(_, t) -> t
                    | Marker -> failwith "Found marker during formatting element reconstruction"
                let newElement = this.InsertElement(true, Ns.Html, tag.Name, tag.Attrs)
                activeFormatting[entryIndex] <- FormatElement(newElement, tag)
                if entryIndex = activeFormatting.Count - 1 then creating <- false else entryIndex <- entryIndex + 1

    member private this.ClearActiveFormattingToMarker() =
        let mutable fin = false
        while not fin do
            if activeFormatting.Count = 0 then
                fin <- true
            else
                let last = activeFormatting[activeFormatting.Count - 1]
                activeFormatting.RemoveAt(activeFormatting.Count - 1)
                match last with
                | Marker -> fin <- true
                | _ -> ()

    /// Two tags are equivalent when their names and attributes are, in any order.
    member private this.EquivModuloAttrOrder(a: Tag, b: Tag) : bool =
        a.Kind = b.Kind
        && a.Name = b.Name
        && a.Attrs.Length = b.Attrs.Length
        && a.Attrs
           |> Array.forall (fun x -> b.Attrs |> Array.exists (fun y -> x.Name = y.Name && x.Value = y.Value))

    member private this.CreateFormattingElementFor(tag: Tag) : int =
        let mutable firstMatch = -1
        let mutable matches = 0
        // From the end to the last marker
        let mutable i = activeFormatting.Count - 1
        let mutable fin = false
        while not fin && i >= 0 do
            match activeFormatting[i] with
            | Marker -> fin <- true
            | FormatElement(_, oldTag) ->
                if this.EquivModuloAttrOrder(tag, oldTag) then
                    firstMatch <- i
                    matches <- matches + 1
                i <- i - 1
        if matches >= 3 then activeFormatting.RemoveAt firstMatch
        let elem = this.InsertElement(true, Ns.Html, tag.Name, tag.Attrs)
        activeFormatting.Add(FormatElement(elem, tag))
        elem

    // --- Popping -----------------------------------------------------------------------------

    member private this.GenerateImpliedEndTags(set: QualName -> bool) =
        let mutable fin = false
        while not fin do
            if openElems.Count = 0 || not (set (this.ElemName openElems[openElems.Count - 1])) then fin <- true else this.Pop() |> ignore

    member private this.GenerateImpliedEndExcept(except: string) =
        this.GenerateImpliedEndTags(fun p -> not (p.Ns = Ns.Html && p.Local = except) && cursoryImpliedEnd p)

    /// Pops elements until the current element is in the set.
    member private this.PopUntilCurrent(set: QualName -> bool) =
        while not (this.CurrentNodeIn set) do
            this.Pop() |> ignore

    /// Pops elements until an element from the set has been popped; returns how many were popped.
    member private this.PopUntil(pred: QualName -> bool) : int =
        let mutable n = 0
        let mutable fin = false
        while not fin do
            n <- n + 1
            if openElems.Count = 0 then
                fin <- true
            else
                let elem = this.Pop()
                if pred (this.ElemName elem) then fin <- true
        n

    member private this.PopUntilNamed(name: string) : int = this.PopUntil(fun p -> p.Ns = Ns.Html && p.Local = name)

    /// Pops elements until one with the specified name has been popped.
    member private this.ExpectToClose(name: string) = this.PopUntilNamed name |> ignore

    member private this.CloseP() =
        this.GenerateImpliedEndTags(fun p -> not (p.Ns = Ns.Html && p.Local = "p") && cursoryImpliedEnd p)
        this.ExpectToClose "p"

    member private this.CloseElementInButtonScope() =
        if this.InScopeNamed(buttonScope, "p") then this.CloseP()

    member private this.CloseTheCell() =
        this.GenerateImpliedEndTags cursoryImpliedEnd
        this.PopUntil tdTh |> ignore
        this.ClearActiveFormattingToMarker()

    member private this.IsTypeHidden(tag: Tag) : bool =
        match tag.Attrs |> Array.tryFind (fun a -> a.Name.Ns = Ns.Empty && a.Name.Local = "type") with
        | None -> false
        | Some a -> System.String.Equals(a.Value, "hidden", System.StringComparison.OrdinalIgnoreCase)

    // --- Modes -------------------------------------------------------------------------------

    /// https://html.spec.whatwg.org/multipage/#reset-the-insertion-mode-appropriately
    member private this.ResetInsertionMode() : InsertionMode =
        let mutable result = ValueNone
        let mutable i = openElems.Count - 1
        while result.IsNone && i >= 0 do
            let last = i = 0
            let node = if last && contextElem >= 0 then contextElem else openElems[i]
            let q = this.ElemName node
            if q.Ns = Ns.Html then
                match q.Local with
                | "select" ->
                    let mutable select = ValueNone
                    let mutable j = i - 1
                    while select.IsNone && j >= 0 do
                        if this.HtmlElemNamed(openElems[j], "template") then select <- ValueSome InsertionMode.InSelect
                        elif this.HtmlElemNamed(openElems[j], "table") then select <- ValueSome InsertionMode.InSelectInTable
                        j <- j - 1
                    result <- ValueSome(defaultValueArg select InsertionMode.InSelect)
                | "td"
                | "th" -> if not last then result <- ValueSome InsertionMode.InCell
                | "tr" -> result <- ValueSome InsertionMode.InRow
                | "tbody"
                | "thead"
                | "tfoot" -> result <- ValueSome InsertionMode.InTableBody
                | "caption" -> result <- ValueSome InsertionMode.InCaption
                | "colgroup" -> result <- ValueSome InsertionMode.InColumnGroup
                | "table" -> result <- ValueSome InsertionMode.InTable
                | "template" -> result <- ValueSome templateModes[templateModes.Count - 1]
                | "head" -> if not last then result <- ValueSome InsertionMode.InHead
                | "body" -> result <- ValueSome InsertionMode.InBody
                | "frameset" -> result <- ValueSome InsertionMode.InFrameset
                | "html" -> result <- ValueSome(if headElem < 0 then InsertionMode.BeforeHead else InsertionMode.AfterHead)
                | _ -> ()
            i <- i - 1
        defaultValueArg result InsertionMode.InBody

    /// The tokenizer state a fragment's context element asks for.
    member this.TokenizerStateForContextElem() : State =
        let q = this.ElemName contextElem
        if q.Ns <> Ns.Html then
            Data
        else
            match q.Local with
            | "title"
            | "textarea" -> RawData Rcdata
            | "style"
            | "xmp"
            | "iframe"
            | "noembed"
            | "noframes" -> RawData Rawtext
            | "script" -> RawData ScriptData
            | "plaintext" -> State.Plaintext
            | _ -> Data

    // Switch to `Text` insertion mode, save the old mode, and switch the tokenizer to a raw-data
    // state, which takes effect after the current start tag is processed.
    member private this.ToRawTextMode(k: RawKind) : ProcessResult =
        origMode <- mode
        mode <- InsertionMode.Text
        ToRawData k

    member private this.ParseRawData(tag: Tag, k: RawKind) : ProcessResult =
        this.InsertElementFor tag |> ignore
        this.ToRawTextMode k

    member private this.FosterParentInBody(token: Token) : ProcessResult =
        fosterParenting <- true
        let res = this.Step(InsertionMode.InBody, token)
        fosterParenting <- false
        res

    member private this.ProcessCharsInTable(token: Token) : ProcessResult =
        if this.CurrentNodeIn tableOuterOrRow then
            origMode <- mode
            Reprocess(InsertionMode.InTableText, token)
        else
            this.FosterParentInBody token

    member private this.ProcessEndTagInBody(tag: Tag) =
        // Look back for a matching open element.
        let mutable matchIdx = -1
        let mutable stop = false
        let mutable i = openElems.Count - 1
        while not stop && i >= 0 do
            let elem = openElems[i]
            if this.HtmlElemNamed(elem, tag.Name) then
                matchIdx <- i
                stop <- true
            elif this.ElemIn(elem, specialTag) then
                stop <- true
            else
                i <- i - 1
        if matchIdx >= 0 then
            this.GenerateImpliedEndExcept tag.Name
            openElems.RemoveRange(matchIdx, openElems.Count - matchIdx)

    member private this.AdoptionAgency(subject: string) =
        // 1.
        if this.CurrentNodeNamed subject && this.PositionInActiveFormatting this.CurrentNode < 0 then
            this.Pop() |> ignore
        else
            // 2. 3. 4.
            let mutable outer = 0
            let mutable fin = false
            while not fin && outer < 8 do
                outer <- outer + 1
                // 5.
                let mutable fmtIndex = -1
                let mutable fmtElem = -1
                let mutable fmtTag = Unchecked.defaultof<Tag>
                let mutable i = activeFormatting.Count - 1
                let mutable scanning = true
                while scanning && i >= 0 do
                    match activeFormatting[i] with
                    | Marker -> scanning <- false
                    | FormatElement(h, t) ->
                        if t.Name = subject then
                            fmtIndex <- i
                            fmtElem <- h
                            fmtTag <- t
                            scanning <- false
                        else
                            i <- i - 1
                if fmtIndex < 0 then
                    this.ProcessEndTagInBody
                        { Kind = EndTag
                          Name = subject
                          SelfClosing = false
                          Attrs = [||] }
                    fin <- true
                else
                    let fmtElemStackIndex = openElems.LastIndexOf fmtElem
                    if fmtElemStackIndex < 0 then
                        activeFormatting.RemoveAt fmtIndex
                        fin <- true
                    // 7.
                    elif not (this.InScope(defaultScope, (let fmt = fmtElem in fun n -> n = fmt))) then
                        fin <- true
                    else
                        // 9.
                        let mutable furthestBlockIndex = -1
                        let mutable j = fmtElemStackIndex
                        while furthestBlockIndex < 0 && j < openElems.Count do
                            if this.ElemIn(openElems[j], specialTag) then furthestBlockIndex <- j
                            j <- j + 1
                        if furthestBlockIndex < 0 then
                            // 10.
                            openElems.RemoveRange(fmtElemStackIndex, openElems.Count - fmtElemStackIndex)
                            activeFormatting.RemoveAt fmtIndex
                            fin <- true
                        else
                            let furthestBlock = openElems[furthestBlockIndex]
                            // 11.
                            let commonAncestor = openElems[fmtElemStackIndex - 1]
                            // 12.
                            let mutable bookmark = Replace fmtElem
                            // 13.
                            let mutable nodeIndex = furthestBlockIndex
                            let mutable lastNode = furthestBlock
                            let mutable innerCounter = 0
                            let mutable inner = true
                            while inner do
                                // 13.2.
                                innerCounter <- innerCounter + 1
                                // 13.3.
                                nodeIndex <- nodeIndex - 1
                                let node = openElems[nodeIndex]
                                // 13.4.
                                if node = fmtElem then
                                    inner <- false
                                // 13.5.
                                elif innerCounter > 3 then
                                    let position = this.PositionInActiveFormatting node
                                    if position >= 0 then activeFormatting.RemoveAt position
                                    openElems.RemoveAt nodeIndex
                                else
                                    let nodeFormattingIndex = this.PositionInActiveFormatting node
                                    if nodeFormattingIndex < 0 then
                                        // 13.6.
                                        openElems.RemoveAt nodeIndex
                                    else
                                        // 13.7.
                                        let tag =
                                            match activeFormatting[nodeFormattingIndex] with
                                            | FormatElement(_, t) -> t
                                            | Marker -> failwith "Found marker during adoption agency"
                                        let newElement = sink.CreateElement(QualName.unprefixed Ns.Html tag.Name, tag.Attrs)
                                        openElems[nodeIndex] <- newElement
                                        activeFormatting[nodeFormattingIndex] <- FormatElement(newElement, tag)
                                        // 13.8.
                                        if lastNode = furthestBlock then bookmark <- InsertAfter newElement
                                        // 13.9.
                                        sink.RemoveFromParent lastNode
                                        sink.Append(newElement, AppendNode lastNode)
                                        // 13.10.
                                        lastNode <- newElement
                            // 14.
                            sink.RemoveFromParent lastNode
                            this.InsertAppropriately(AppendNode lastNode, commonAncestor)
                            // 15.
                            let newElement = sink.CreateElement(QualName.unprefixed Ns.Html fmtTag.Name, fmtTag.Attrs)
                            let newEntry = FormatElement(newElement, fmtTag)
                            // 16.
                            sink.ReparentChildren(furthestBlock, newElement)
                            // 17.
                            sink.Append(furthestBlock, AppendNode newElement)
                            // 18.
                            match bookmark with
                            | Replace toReplace -> activeFormatting[this.PositionInActiveFormatting toReplace] <- newEntry
                            | InsertAfter previous ->
                                let index = this.PositionInActiveFormatting previous + 1
                                activeFormatting.Insert(index, newEntry)
                                let oldIndex = this.PositionInActiveFormatting fmtElem
                                activeFormatting.RemoveAt oldIndex
                            // 19.
                            this.RemoveFromStack fmtElem
                            let newFurthestBlockIndex = openElems.IndexOf furthestBlock
                            openElems.Insert(newFurthestBlockIndex + 1, newElement)
                            // 20.

    member private this.HandleMisnestedATags() =
        // The most recent "a" formatting element since the last marker
        let mutable node = -1
        let mutable i = activeFormatting.Count - 1
        let mutable scanning = true
        while scanning && i >= 0 do
            match activeFormatting[i] with
            | Marker -> scanning <- false
            | FormatElement(h, _) ->
                if this.HtmlElemNamed(h, "a") then
                    node <- h
                    scanning <- false
                else
                    i <- i - 1
        if node >= 0 then
            this.AdoptionAgency "a"
            let index = this.PositionInActiveFormatting node
            if index >= 0 then activeFormatting.RemoveAt index
            this.RemoveFromStack node

    // --- Foreign content ---------------------------------------------------------------------

    member private this.IsForeign(token: Token) : bool =
        match token with
        | Eof -> false
        | _ when openElems.Count = 0 -> false
        | _ ->
            let current = this.AdjustedCurrentNode
            let name = this.ElemName current
            if name.Ns = Ns.Html then
                false
            else
                let mutable decided = ValueNone
                if mathmlTextIntegrationPoint name then
                    match token with
                    | Characters _
                    | NullCharacter -> decided <- ValueSome false
                    | TagToken t when t.Kind = StartTag && t.Name <> "mglyph" && t.Name <> "malignmark" -> decided <- ValueSome false
                    | _ -> ()
                if decided.IsNone && svgHtmlIntegrationPoint name then
                    match token with
                    | Characters _
                    | NullCharacter -> decided <- ValueSome false
                    | TagToken t when t.Kind = StartTag -> decided <- ValueSome false
                    | _ -> ()
                if decided.IsNone && name.Ns = Ns.MathMl && name.Local = "annotation-xml" then
                    match token with
                    | TagToken t when t.Kind = StartTag && t.Name = "svg" -> decided <- ValueSome false
                    | Characters _
                    | NullCharacter
                    | TagToken { Kind = StartTag } ->
                        // The DOM sink never marks an annotation-xml as an HTML integration point.
                        decided <- ValueSome true
                    | _ -> ()
                defaultValueArg decided true

    member private this.AdjustAttributes(tag: Tag, map: string -> QualName voption) : Tag =
        let attrs =
            tag.Attrs
            |> Array.map (fun a ->
                match map a.Name.Local with
                | ValueSome replacement -> { a with Name = replacement }
                | ValueNone -> a)
        { tag with Attrs = attrs }

    member private this.AdjustSvgAttributes(tag: Tag) : Tag =
        this.AdjustAttributes(
            tag,
            fun k ->
                match ForeignNames.svgAttributeNames.TryGetValue k with
                | true, replacement -> ValueSome(QualName.unprefixed Ns.Empty replacement)
                | _ -> ValueNone
        )

    member private this.AdjustMathmlAttributes(tag: Tag) : Tag =
        this.AdjustAttributes(tag, fun k -> if k = "definitionurl" then ValueSome(QualName.unprefixed Ns.Empty "definitionURL") else ValueNone)

    member private this.AdjustForeignAttributes(tag: Tag) : Tag =
        this.AdjustAttributes(
            tag,
            fun k ->
                match ForeignNames.foreignAttributes.TryGetValue k with
                | true, replacement -> ValueSome replacement
                | _ -> ValueNone
        )

    member private this.EnterForeign(tag0: Tag, ns: Ns) : ProcessResult =
        let tag1 =
            match ns with
            | Ns.MathMl -> this.AdjustMathmlAttributes tag0
            | Ns.Svg -> this.AdjustSvgAttributes tag0
            | _ -> tag0
        let tag = this.AdjustForeignAttributes tag1
        if tag.SelfClosing then
            this.InsertElement(false, ns, tag.Name, tag.Attrs) |> ignore
            DoneAckSelfClosing
        else
            this.InsertElement(true, ns, tag.Name, tag.Attrs) |> ignore
            Done

    member private this.ForeignStartTag(tag0: Tag) : ProcessResult =
        let currentNs = (this.ElemName this.AdjustedCurrentNode).Ns
        let tag1 =
            match currentNs with
            | Ns.MathMl -> this.AdjustMathmlAttributes tag0
            | Ns.Svg ->
                let renamed =
                    match ForeignNames.svgTagNames.TryGetValue tag0.Name with
                    | true, name -> { tag0 with Name = name }
                    | _ -> tag0
                this.AdjustSvgAttributes renamed
            | _ -> tag0
        let tag = this.AdjustForeignAttributes tag1
        if tag.SelfClosing then
            this.InsertElement(false, currentNs, tag.Name, tag.Attrs) |> ignore
            DoneAckSelfClosing
        else
            this.InsertElement(true, currentNs, tag.Name, tag.Attrs) |> ignore
            Done

    member private this.UnexpectedStartTagInForeignContent(tag: Tag) : ProcessResult =
        while not (this.CurrentNodeIn(fun n -> n.Ns = Ns.Html || mathmlTextIntegrationPoint n || svgHtmlIntegrationPoint n)) do
            this.Pop() |> ignore
        this.Step(mode, TagToken tag)

    member private this.StepForeign(token: Token) : ProcessResult =
        match token with
        | NullCharacter -> this.AppendText "�"
        | Characters(_, text) ->
            if text |> Seq.exists (fun c -> not (Strings.isAsciiWhitespace c)) then framesetOk <- false
            this.AppendText text
        | CommentToken text -> this.AppendComment text
        | TagToken tag ->
            match tag.Kind, tag.Name with
            | StartTag,
              ("b" | "big" | "blockquote" | "body" | "br" | "center" | "code" | "dd" | "div" | "dl" | "dt" | "em" | "embed" | "h1"
              | "h2" | "h3" | "h4" | "h5" | "h6" | "head" | "hr" | "i" | "img" | "li" | "listing" | "menu" | "meta" | "nobr" | "ol"
              | "p" | "pre" | "ruby" | "s" | "small" | "span" | "strong" | "strike" | "sub" | "sup" | "table" | "tt" | "u" | "ul"
              | "var")
            | EndTag, ("br" | "p") -> this.UnexpectedStartTagInForeignContent tag
            | StartTag, "font" ->
                let unexpected =
                    tag.Attrs
                    |> Array.exists (fun a ->
                        a.Name.Ns = Ns.Empty && (a.Name.Local = "color" || a.Name.Local = "face" || a.Name.Local = "size"))
                if unexpected then this.UnexpectedStartTagInForeignContent tag else this.ForeignStartTag tag
            | StartTag, _ -> this.ForeignStartTag tag
            | EndTag, _ ->
                let mutable first = true
                let mutable stackIdx = openElems.Count - 1
                let mutable result = ValueNone
                while result.IsNone do
                    if stackIdx = 0 then
                        result <- ValueSome Done
                    else
                        let nodeName = this.ElemName openElems[stackIdx]
                        let html = nodeName.Ns = Ns.Html
                        let eq = System.String.Equals(nodeName.Local, tag.Name, System.StringComparison.OrdinalIgnoreCase)
                        if not first && html then
                            result <- ValueSome(this.Step(mode, TagToken tag))
                        elif eq then
                            openElems.RemoveRange(stackIdx, openElems.Count - stackIdx)
                            result <- ValueSome Done
                        else
                            first <- false
                            stackIdx <- stackIdx - 1
                result.Value
        | Eof -> failwith "impossible case in foreign content"

    // --- The rules ---------------------------------------------------------------------------

    member private this.Step(insertionMode: InsertionMode, token: Token) : ProcessResult =
        let anyNotWhitespace (text: string) = text |> Seq.exists (fun c -> not (Strings.isAsciiWhitespace c))
        match insertionMode with
        | InsertionMode.Initial ->
            match token with
            | Characters(NotSplit, text) -> SplitWhitespace text
            | Characters(Whitespace, _) -> Done
            | CommentToken text -> this.AppendCommentToDoc text
            | _ -> Reprocess(InsertionMode.BeforeHtml, token)

        | InsertionMode.BeforeHtml ->
            let other () =
                this.CreateRoot [||]
                Reprocess(InsertionMode.BeforeHead, token)
            match token with
            | Characters(NotSplit, text) -> SplitWhitespace text
            | Characters(Whitespace, _) -> Done
            | CommentToken text -> this.AppendCommentToDoc text
            | TagToken tag ->
                match tag.Kind, tag.Name with
                | StartTag, "html" ->
                    this.CreateRoot tag.Attrs
                    this.SetMode InsertionMode.BeforeHead
                    Done
                | EndTag, ("head" | "body" | "html" | "br") -> other ()
                | EndTag, _ -> Done
                | _ -> other ()
            | _ -> other ()

        | InsertionMode.BeforeHead ->
            let other () =
                headElem <- this.InsertPhantom "head"
                Reprocess(InsertionMode.InHead, token)
            match token with
            | Characters(NotSplit, text) -> SplitWhitespace text
            | Characters(Whitespace, _) -> Done
            | CommentToken text -> this.AppendComment text
            | TagToken tag ->
                match tag.Kind, tag.Name with
                | StartTag, "html" -> this.Step(InsertionMode.InBody, token)
                | StartTag, "head" ->
                    headElem <- this.InsertElementFor tag
                    this.SetMode InsertionMode.InHead
                    Done
                | EndTag, ("head" | "body" | "html" | "br") -> other ()
                | EndTag, _ -> Done
                | _ -> other ()
            | _ -> other ()

        | InsertionMode.InHead ->
            let other () =
                this.Pop() |> ignore
                Reprocess(InsertionMode.AfterHead, token)
            match token with
            | Characters(NotSplit, text) -> SplitWhitespace text
            | Characters(Whitespace, text) -> this.AppendText text
            | CommentToken text -> this.AppendComment text
            | TagToken tag ->
                match tag.Kind, tag.Name with
                | StartTag, "html" -> this.Step(InsertionMode.InBody, token)
                | StartTag, ("base" | "basefont" | "bgsound" | "link" | "meta") ->
                    this.InsertAndPopElementFor tag |> ignore
                    DoneAckSelfClosing
                | StartTag, "title" -> this.ParseRawData(tag, Rcdata)
                // Scripting is off, so <noscript> in the head is markup.
                | StartTag, "noscript" ->
                    this.InsertElementFor tag |> ignore
                    this.SetMode InsertionMode.InHeadNoscript
                    Done
                | StartTag, ("noframes" | "style") -> this.ParseRawData(tag, Rawtext)
                | StartTag, "script" ->
                    let elem = sink.CreateElement(QualName.unprefixed Ns.Html "script", tag.Attrs)
                    this.InsertAppropriately(AppendNode elem, -1)
                    openElems.Add elem
                    this.ToRawTextMode ScriptData
                | EndTag, "head" ->
                    this.Pop() |> ignore
                    this.SetMode InsertionMode.AfterHead
                    Done
                | EndTag, ("body" | "html" | "br") -> other ()
                | StartTag, "template" ->
                    activeFormatting.Add Marker
                    framesetOk <- false
                    this.SetMode InsertionMode.InTemplate
                    templateModes.Add InsertionMode.InTemplate
                    if this.ShouldAttachDeclarativeShadow tag then
                        // The sink never attaches a shadow root: the element made to host one is
                        // dropped, and the template is inserted as usual.
                        this.InsertForeignElement(tag, Ns.Html, true) |> ignore
                        this.Pop() |> ignore
                        this.InsertElementFor tag |> ignore
                    else
                        this.InsertElementFor tag |> ignore
                    Done
                | EndTag, "template" ->
                    if not (this.InHtmlElemNamed "template") then
                        ()
                    else
                        this.GenerateImpliedEndTags thoroughImpliedEnd
                        this.ExpectToClose "template"
                        this.ClearActiveFormattingToMarker()
                        templateModes.RemoveAt(templateModes.Count - 1)
                        this.SetMode(this.ResetInsertionMode())
                    Done
                | StartTag, "head" -> Done
                | EndTag, _ -> Done
                | _ -> other ()
            | _ -> other ()

        | InsertionMode.InHeadNoscript ->
            let other () =
                this.Pop() |> ignore
                Reprocess(InsertionMode.InHead, token)
            match token with
            | Characters(NotSplit, text) -> SplitWhitespace text
            | Characters(Whitespace, _) -> this.Step(InsertionMode.InHead, token)
            | CommentToken _ -> this.Step(InsertionMode.InHead, token)
            | TagToken tag ->
                match tag.Kind, tag.Name with
                | StartTag, "html" -> this.Step(InsertionMode.InBody, token)
                | EndTag, "noscript" ->
                    this.Pop() |> ignore
                    this.SetMode InsertionMode.InHead
                    Done
                | StartTag, ("basefont" | "bgsound" | "link" | "meta" | "noframes" | "style") -> this.Step(InsertionMode.InHead, token)
                | EndTag, "br" -> other ()
                | StartTag, ("head" | "noscript") -> Done
                | EndTag, _ -> Done
                | _ -> other ()
            | _ -> other ()

        | InsertionMode.AfterHead ->
            let other () =
                this.InsertPhantom "body" |> ignore
                Reprocess(InsertionMode.InBody, token)
            match token with
            | Characters(NotSplit, text) -> SplitWhitespace text
            | Characters(Whitespace, text) -> this.AppendText text
            | CommentToken text -> this.AppendComment text
            | TagToken tag ->
                match tag.Kind, tag.Name with
                | StartTag, "html" -> this.Step(InsertionMode.InBody, token)
                | StartTag, "body" ->
                    this.InsertElementFor tag |> ignore
                    framesetOk <- false
                    this.SetMode InsertionMode.InBody
                    Done
                | StartTag, "frameset" ->
                    this.InsertElementFor tag |> ignore
                    this.SetMode InsertionMode.InFrameset
                    Done
                | StartTag, ("base" | "basefont" | "bgsound" | "link" | "meta" | "noframes" | "script" | "style" | "template" | "title") ->
                    let head = headElem
                    this.Push head
                    let result = this.Step(InsertionMode.InHead, token)
                    this.RemoveFromStack head
                    result
                | EndTag, "template" -> this.Step(InsertionMode.InHead, token)
                | EndTag, ("body" | "html" | "br") -> other ()
                | StartTag, "head" -> Done
                | EndTag, _ -> Done
                | _ -> other ()
            | _ -> other ()

        | InsertionMode.InBody -> this.StepInBody token

        | InsertionMode.Text ->
            match token with
            | Characters(_, text) -> this.AppendText text
            | Eof ->
                this.Pop() |> ignore
                Reprocess(origMode, token)
            | TagToken tag when tag.Kind = EndTag ->
                let node = this.Pop()
                this.SetMode origMode
                if tag.Name = "script" then ScriptEnd node else Done
            | _ -> failwith "impossible case in Text mode"

        | InsertionMode.InTable ->
            match token with
            | NullCharacter -> this.ProcessCharsInTable token
            | Characters _ -> this.ProcessCharsInTable token
            | CommentToken text -> this.AppendComment text
            | TagToken tag ->
                match tag.Kind, tag.Name with
                | StartTag, "caption" ->
                    this.PopUntilCurrent tableScope
                    activeFormatting.Add Marker
                    this.InsertElementFor tag |> ignore
                    this.SetMode InsertionMode.InCaption
                    Done
                | StartTag, "colgroup" ->
                    this.PopUntilCurrent tableScope
                    this.InsertElementFor tag |> ignore
                    this.SetMode InsertionMode.InColumnGroup
                    Done
                | StartTag, "col" ->
                    this.PopUntilCurrent tableScope
                    this.InsertPhantom "colgroup" |> ignore
                    Reprocess(InsertionMode.InColumnGroup, token)
                | StartTag, ("tbody" | "tfoot" | "thead") ->
                    this.PopUntilCurrent tableScope
                    this.InsertElementFor tag |> ignore
                    this.SetMode InsertionMode.InTableBody
                    Done
                | StartTag, ("td" | "th" | "tr") ->
                    this.PopUntilCurrent tableScope
                    this.InsertPhantom "tbody" |> ignore
                    Reprocess(InsertionMode.InTableBody, token)
                | StartTag, "table" ->
                    if this.InScopeNamed(tableScope, "table") then
                        this.PopUntilNamed "table" |> ignore
                        Reprocess(this.ResetInsertionMode(), token)
                    else
                        Done
                | EndTag, "table" ->
                    if this.InScopeNamed(tableScope, "table") then
                        this.PopUntilNamed "table" |> ignore
                        this.SetMode(this.ResetInsertionMode())
                    Done
                | EndTag, ("body" | "caption" | "col" | "colgroup" | "html" | "tbody" | "td" | "tfoot" | "th" | "thead" | "tr") -> Done
                | StartTag, ("style" | "script" | "template")
                | EndTag, "template" -> this.Step(InsertionMode.InHead, token)
                | StartTag, "input" ->
                    if this.IsTypeHidden tag then
                        this.InsertAndPopElementFor tag |> ignore
                        DoneAckSelfClosing
                    else
                        this.FosterParentInBody(TagToken tag)
                | StartTag, "form" ->
                    if not (this.InHtmlElemNamed "template") && formElem < 0 then
                        formElem <- this.InsertAndPopElementFor tag
                    Done
                | _ -> this.FosterParentInBody token
            | Eof -> this.Step(InsertionMode.InBody, token)

        | InsertionMode.InTableText ->
            match token with
            | NullCharacter -> Done
            | Characters(split, text) ->
                pendingTableText.Add(struct (split, text))
                Done
            | _ ->
                let pending = pendingTableText.ToArray()
                pendingTableText.Clear()
                let containsNonspace =
                    pending
                    |> Array.exists (fun (struct (split, text)) ->
                        match split with
                        | Whitespace -> false
                        | NotWhitespace -> true
                        | NotSplit -> anyNotWhitespace text)
                if containsNonspace then
                    for struct (split, text) in pending do
                        match this.FosterParentInBody(Characters(split, text)) with
                        | Done -> ()
                        | _ -> failwith "not prepared to handle this!"
                else
                    for struct (_, text) in pending do
                        this.AppendText text |> ignore
                Reprocess(origMode, token)

        | InsertionMode.InCaption ->
            match token with
            | TagToken tag ->
                match tag.Kind, tag.Name with
                | StartTag, ("caption" | "col" | "colgroup" | "tbody" | "td" | "tfoot" | "th" | "thead" | "tr")
                | EndTag, ("table" | "caption") ->
                    if this.InScopeNamed(tableScope, "caption") then
                        this.GenerateImpliedEndTags cursoryImpliedEnd
                        this.ExpectToClose "caption"
                        this.ClearActiveFormattingToMarker()
                        if tag.Kind = EndTag && tag.Name = "caption" then
                            this.SetMode InsertionMode.InTable
                            Done
                        else
                            Reprocess(InsertionMode.InTable, TagToken tag)
                    else
                        Done
                | EndTag, ("body" | "col" | "colgroup" | "html" | "tbody" | "td" | "tfoot" | "th" | "thead" | "tr") -> Done
                | _ -> this.Step(InsertionMode.InBody, token)
            | _ -> this.Step(InsertionMode.InBody, token)

        | InsertionMode.InColumnGroup ->
            let other () =
                if this.CurrentNodeNamed "colgroup" then
                    this.Pop() |> ignore
                    Reprocess(InsertionMode.InTable, token)
                else
                    Done
            match token with
            | Characters(NotSplit, text) -> SplitWhitespace text
            | Characters(Whitespace, text) -> this.AppendText text
            | CommentToken text -> this.AppendComment text
            | TagToken tag ->
                match tag.Kind, tag.Name with
                | StartTag, "html" -> this.Step(InsertionMode.InBody, token)
                | StartTag, "col" ->
                    this.InsertAndPopElementFor tag |> ignore
                    DoneAckSelfClosing
                | EndTag, "colgroup" ->
                    if this.CurrentNodeNamed "colgroup" then
                        this.Pop() |> ignore
                        this.SetMode InsertionMode.InTable
                    Done
                | EndTag, "col" -> Done
                | StartTag, "template"
                | EndTag, "template" -> this.Step(InsertionMode.InHead, token)
                | _ -> other ()
            | Eof -> this.Step(InsertionMode.InBody, token)
            | _ -> other ()

        | InsertionMode.InTableBody ->
            match token with
            | TagToken tag ->
                match tag.Kind, tag.Name with
                | StartTag, "tr" ->
                    this.PopUntilCurrent tableBodyContext
                    this.InsertElementFor tag |> ignore
                    this.SetMode InsertionMode.InRow
                    Done
                | StartTag, ("th" | "td") ->
                    this.PopUntilCurrent tableBodyContext
                    this.InsertPhantom "tr" |> ignore
                    Reprocess(InsertionMode.InRow, token)
                | EndTag, ("tbody" | "tfoot" | "thead") ->
                    if this.InScopeNamed(tableScope, tag.Name) then
                        this.PopUntilCurrent tableBodyContext
                        this.Pop() |> ignore
                        this.SetMode InsertionMode.InTable
                    Done
                | StartTag, ("caption" | "col" | "colgroup" | "tbody" | "tfoot" | "thead")
                | EndTag, "table" ->
                    if this.InScope(tableScope, (fun e -> this.ElemIn(e, tableOuter))) then
                        this.PopUntilCurrent tableBodyContext
                        this.Pop() |> ignore
                        Reprocess(InsertionMode.InTable, token)
                    else
                        Done
                | EndTag, ("body" | "caption" | "col" | "colgroup" | "html" | "td" | "th" | "tr") -> Done
                | _ -> this.Step(InsertionMode.InTable, token)
            | _ -> this.Step(InsertionMode.InTable, token)

        | InsertionMode.InRow ->
            match token with
            | TagToken tag ->
                match tag.Kind, tag.Name with
                | StartTag, ("th" | "td") ->
                    this.PopUntilCurrent tableRowContext
                    this.InsertElementFor tag |> ignore
                    this.SetMode InsertionMode.InCell
                    activeFormatting.Add Marker
                    Done
                | EndTag, "tr" ->
                    if this.InScopeNamed(tableScope, "tr") then
                        this.PopUntilCurrent tableRowContext
                        this.Pop() |> ignore
                        this.SetMode InsertionMode.InTableBody
                    Done
                | StartTag, ("caption" | "col" | "colgroup" | "tbody" | "tfoot" | "thead" | "tr")
                | EndTag, "table" ->
                    if this.InScopeNamed(tableScope, "tr") then
                        this.PopUntilCurrent tableRowContext
                        this.Pop() |> ignore
                        Reprocess(InsertionMode.InTableBody, token)
                    else
                        Done
                | EndTag, ("tbody" | "tfoot" | "thead") ->
                    if this.InScopeNamed(tableScope, tag.Name) then
                        if this.InScopeNamed(tableScope, "tr") then
                            this.PopUntilCurrent tableRowContext
                            this.Pop() |> ignore
                            Reprocess(InsertionMode.InTableBody, TagToken tag)
                        else
                            Done
                    else
                        Done
                | EndTag, ("body" | "caption" | "col" | "colgroup" | "html" | "td" | "th") -> Done
                | _ -> this.Step(InsertionMode.InTable, token)
            | _ -> this.Step(InsertionMode.InTable, token)

        | InsertionMode.InCell ->
            match token with
            | TagToken tag ->
                match tag.Kind, tag.Name with
                | EndTag, ("td" | "th") ->
                    if this.InScopeNamed(tableScope, tag.Name) then
                        this.GenerateImpliedEndTags cursoryImpliedEnd
                        this.ExpectToClose tag.Name
                        this.ClearActiveFormattingToMarker()
                        this.SetMode InsertionMode.InRow
                    Done
                | StartTag, ("caption" | "col" | "colgroup" | "tbody" | "td" | "tfoot" | "th" | "thead" | "tr") ->
                    if this.InScope(tableScope, (fun n -> this.ElemIn(n, tdTh))) then
                        this.CloseTheCell()
                        Reprocess(InsertionMode.InRow, token)
                    else
                        Done
                | EndTag, ("body" | "caption" | "col" | "colgroup" | "html") -> Done
                | EndTag, ("table" | "tbody" | "tfoot" | "thead" | "tr") ->
                    if this.InScopeNamed(tableScope, tag.Name) then
                        this.CloseTheCell()
                        Reprocess(InsertionMode.InRow, TagToken tag)
                    else
                        Done
                | _ -> this.Step(InsertionMode.InBody, token)
            | _ -> this.Step(InsertionMode.InBody, token)

        | InsertionMode.InSelect ->
            match token with
            | NullCharacter -> Done
            | Characters(_, text) -> this.AppendText text
            | CommentToken text -> this.AppendComment text
            | TagToken tag ->
                match tag.Kind, tag.Name with
                | StartTag, "html" -> this.Step(InsertionMode.InBody, token)
                | StartTag, "option" ->
                    if this.CurrentNodeNamed "option" then this.Pop() |> ignore
                    this.InsertElementFor tag |> ignore
                    Done
                | StartTag, "optgroup" ->
                    if this.CurrentNodeNamed "option" then this.Pop() |> ignore
                    if this.CurrentNodeNamed "optgroup" then this.Pop() |> ignore
                    this.InsertElementFor tag |> ignore
                    Done
                | StartTag, "hr" ->
                    if this.CurrentNodeNamed "option" then this.Pop() |> ignore
                    if this.CurrentNodeNamed "optgroup" then this.Pop() |> ignore
                    this.InsertElementFor tag |> ignore
                    this.Pop() |> ignore
                    DoneAckSelfClosing
                | EndTag, "optgroup" ->
                    if openElems.Count >= 2
                       && this.CurrentNodeNamed "option"
                       && this.HtmlElemNamed(openElems[openElems.Count - 2], "optgroup") then
                        this.Pop() |> ignore
                    if this.CurrentNodeNamed "optgroup" then this.Pop() |> ignore
                    Done
                | EndTag, "option" ->
                    if this.CurrentNodeNamed "option" then this.Pop() |> ignore
                    Done
                | StartTag, "select"
                | EndTag, "select" ->
                    let inScope = this.InScopeNamed(selectScope, "select")
                    if inScope then
                        this.PopUntilNamed "select" |> ignore
                        this.SetMode(this.ResetInsertionMode())
                    Done
                | StartTag, ("input" | "keygen" | "textarea") ->
                    if this.InScopeNamed(selectScope, "select") then
                        this.PopUntilNamed "select" |> ignore
                        Reprocess(this.ResetInsertionMode(), token)
                    else
                        Done
                | StartTag, ("script" | "template")
                | EndTag, "template" -> this.Step(InsertionMode.InHead, token)
                | _ -> Done
            | Eof -> this.Step(InsertionMode.InBody, token)

        | InsertionMode.InSelectInTable ->
            match token with
            | TagToken tag ->
                match tag.Kind, tag.Name with
                | StartTag, ("caption" | "table" | "tbody" | "tfoot" | "thead" | "tr" | "td" | "th") ->
                    this.PopUntilNamed "select" |> ignore
                    Reprocess(this.ResetInsertionMode(), token)
                | EndTag, ("caption" | "table" | "tbody" | "tfoot" | "thead" | "tr" | "td" | "th") ->
                    if this.InScopeNamed(tableScope, tag.Name) then
                        this.PopUntilNamed "select" |> ignore
                        Reprocess(this.ResetInsertionMode(), TagToken tag)
                    else
                        Done
                | _ -> this.Step(InsertionMode.InSelect, token)
            | _ -> this.Step(InsertionMode.InSelect, token)

        | InsertionMode.InTemplate ->
            match token with
            | Characters _ -> this.Step(InsertionMode.InBody, token)
            | CommentToken _ -> this.Step(InsertionMode.InBody, token)
            | Eof ->
                if not (this.InHtmlElemNamed "template") then
                    Done
                else
                    this.PopUntilNamed "template" |> ignore
                    this.ClearActiveFormattingToMarker()
                    templateModes.RemoveAt(templateModes.Count - 1)
                    this.SetMode(this.ResetInsertionMode())
                    Reprocess(this.ResetInsertionMode(), token)
            | TagToken tag ->
                match tag.Kind, tag.Name with
                | StartTag, ("base" | "basefont" | "bgsound" | "link" | "meta" | "noframes" | "script" | "style" | "template" | "title")
                | EndTag, "template" -> this.Step(InsertionMode.InHead, token)
                | StartTag, ("caption" | "colgroup" | "tbody" | "tfoot" | "thead") ->
                    templateModes.RemoveAt(templateModes.Count - 1)
                    templateModes.Add InsertionMode.InTable
                    Reprocess(InsertionMode.InTable, token)
                | StartTag, "col" ->
                    templateModes.RemoveAt(templateModes.Count - 1)
                    templateModes.Add InsertionMode.InColumnGroup
                    Reprocess(InsertionMode.InColumnGroup, token)
                | StartTag, "tr" ->
                    templateModes.RemoveAt(templateModes.Count - 1)
                    templateModes.Add InsertionMode.InTableBody
                    Reprocess(InsertionMode.InTableBody, token)
                | StartTag, ("td" | "th") ->
                    templateModes.RemoveAt(templateModes.Count - 1)
                    templateModes.Add InsertionMode.InRow
                    Reprocess(InsertionMode.InRow, token)
                | StartTag, _ ->
                    templateModes.RemoveAt(templateModes.Count - 1)
                    templateModes.Add InsertionMode.InBody
                    Reprocess(InsertionMode.InBody, token)
                | _ -> Done
            | _ -> Done

        | InsertionMode.AfterBody ->
            let other () = Reprocess(InsertionMode.InBody, token)
            match token with
            | Characters(NotSplit, text) -> SplitWhitespace text
            | Characters(Whitespace, _) -> this.Step(InsertionMode.InBody, token)
            | CommentToken text -> this.AppendCommentToHtml text
            | TagToken tag ->
                match tag.Kind, tag.Name with
                | StartTag, "html" -> this.Step(InsertionMode.InBody, token)
                | EndTag, "html" ->
                    // A fragment parse never gets past the body.
                    Done
                | _ -> other ()
            | Eof -> Done
            | _ -> other ()

        | InsertionMode.InFrameset ->
            match token with
            | Characters(NotSplit, text) -> SplitWhitespace text
            | Characters(Whitespace, text) -> this.AppendText text
            | CommentToken text -> this.AppendComment text
            | TagToken tag ->
                match tag.Kind, tag.Name with
                | StartTag, "html" -> this.Step(InsertionMode.InBody, token)
                | StartTag, "frameset" ->
                    this.InsertElementFor tag |> ignore
                    Done
                | EndTag, "frameset" ->
                    if openElems.Count <> 1 then
                        this.Pop() |> ignore
                    Done
                | StartTag, "frame" ->
                    this.InsertAndPopElementFor tag |> ignore
                    DoneAckSelfClosing
                | StartTag, "noframes" -> this.Step(InsertionMode.InHead, token)
                | _ -> Done
            | Eof -> Done
            | _ -> Done

        | InsertionMode.AfterFrameset ->
            match token with
            | Characters(NotSplit, text) -> SplitWhitespace text
            | Characters(Whitespace, text) -> this.AppendText text
            | CommentToken text -> this.AppendComment text
            | TagToken tag ->
                match tag.Kind, tag.Name with
                | StartTag, "html" -> this.Step(InsertionMode.InBody, token)
                | EndTag, "html" ->
                    this.SetMode InsertionMode.AfterAfterFrameset
                    Done
                | StartTag, "noframes" -> this.Step(InsertionMode.InHead, token)
                | _ -> Done
            | Eof -> Done
            | _ -> Done

        | InsertionMode.AfterAfterBody ->
            let other () = Reprocess(InsertionMode.InBody, token)
            match token with
            | Characters(NotSplit, text) -> SplitWhitespace text
            | Characters(Whitespace, _) -> this.Step(InsertionMode.InBody, token)
            | CommentToken text -> this.AppendCommentToDoc text
            | TagToken tag when tag.Kind = StartTag && tag.Name = "html" -> this.Step(InsertionMode.InBody, token)
            | Eof -> Done
            | _ -> other ()

        | InsertionMode.AfterAfterFrameset ->
            match token with
            | Characters(NotSplit, text) -> SplitWhitespace text
            | Characters(Whitespace, _) -> this.Step(InsertionMode.InBody, token)
            | CommentToken text -> this.AppendCommentToDoc text
            | TagToken tag ->
                match tag.Kind, tag.Name with
                | StartTag, "html" -> this.Step(InsertionMode.InBody, token)
                | StartTag, "noframes" -> this.Step(InsertionMode.InHead, token)
                | _ -> Done
            | Eof -> Done
            | _ -> Done

    member private this.SetMode(m: InsertionMode) = mode <- m

    member private this.StepInBody(token: Token) : ProcessResult =
        let anyNotWhitespace (text: string) = text |> Seq.exists (fun c -> not (Strings.isAsciiWhitespace c))
        match token with
        | NullCharacter -> Done
        | Characters(_, text) ->
            this.ReconstructActiveFormattingElements()
            if anyNotWhitespace text then framesetOk <- false
            this.AppendText text
        | CommentToken text -> this.AppendComment text
        | Eof ->
            if templateModes.Count > 0 then this.Step(InsertionMode.InTemplate, token) else Done
        | TagToken tag ->
            match tag.Kind, tag.Name with
            | StartTag, "html" ->
                if not (this.InHtmlElemNamed "template") then sink.AddAttrsIfMissing(openElems[0], tag.Attrs)
                Done
            | StartTag, ("base" | "basefont" | "bgsound" | "link" | "meta" | "noframes" | "script" | "style" | "template" | "title")
            | EndTag, "template" -> this.Step(InsertionMode.InHead, token)
            | StartTag, "body" ->
                if openElems.Count > 1 && this.HtmlElemNamed(openElems[1], "body") && openElems.Count <> 1 && not (this.InHtmlElemNamed "template") then
                    framesetOk <- false
                    sink.AddAttrsIfMissing(openElems[1], tag.Attrs)
                Done
            | StartTag, "frameset" ->
                if not framesetOk then
                    Done
                elif openElems.Count <= 1 || not (this.HtmlElemNamed(openElems[1], "body")) then
                    Done
                else
                    let body = openElems[1]
                    sink.RemoveFromParent body
                    openElems.RemoveRange(1, openElems.Count - 1)
                    this.InsertElementFor tag |> ignore
                    this.SetMode InsertionMode.InFrameset
                    Done
            | EndTag, "body" ->
                if this.InScopeNamed(defaultScope, "body") then this.SetMode InsertionMode.AfterBody
                Done
            | EndTag, "html" ->
                if this.InScopeNamed(defaultScope, "body") then Reprocess(InsertionMode.AfterBody, token) else Done
            | StartTag,
              ("address" | "article" | "aside" | "blockquote" | "center" | "details" | "dialog" | "dir" | "div" | "dl" | "fieldset"
              | "figcaption" | "figure" | "footer" | "header" | "hgroup" | "main" | "nav" | "ol" | "p" | "search" | "section"
              | "summary" | "ul")
            | StartTag, "menu" ->
                this.CloseElementInButtonScope()
                this.InsertElementFor tag |> ignore
                Done
            | StartTag, ("h1" | "h2" | "h3" | "h4" | "h5" | "h6") ->
                this.CloseElementInButtonScope()
                if this.CurrentNodeIn headingTag then this.Pop() |> ignore
                this.InsertElementFor tag |> ignore
                Done
            | StartTag, ("pre" | "listing") ->
                this.CloseElementInButtonScope()
                this.InsertElementFor tag |> ignore
                ignoreLf <- true
                framesetOk <- false
                Done
            | StartTag, "form" ->
                if formElem >= 0 && not (this.InHtmlElemNamed "template") then
                    ()
                else
                    this.CloseElementInButtonScope()
                    let elem = this.InsertElementFor tag
                    if not (this.InHtmlElemNamed "template") then formElem <- elem
                Done
            | StartTag, ("li" | "dd" | "dt") ->
                let list = tag.Name = "li"
                framesetOk <- false
                let mutable toClose: string | null = null
                let mutable i = openElems.Count - 1
                let mutable scanning = true
                while scanning && i >= 0 do
                    let name = this.ElemName openElems[i]
                    let canClose =
                        name.Ns = Ns.Html
                        && (if list then name.Local = "li" else name.Local = "dd" || name.Local = "dt")
                    if canClose then
                        toClose <- name.Local
                        scanning <- false
                    elif specialTag name && not (name.Ns = Ns.Html && (name.Local = "address" || name.Local = "div" || name.Local = "p")) then
                        scanning <- false
                    else
                        i <- i - 1
                match toClose with
                | null -> ()
                | name ->
                    this.GenerateImpliedEndExcept name
                    this.ExpectToClose name
                this.CloseElementInButtonScope()
                this.InsertElementFor tag |> ignore
                Done
            | StartTag, "plaintext" ->
                this.CloseElementInButtonScope()
                this.InsertElementFor tag |> ignore
                ToPlaintext
            | StartTag, "button" ->
                if this.InScopeNamed(defaultScope, "button") then
                    this.GenerateImpliedEndTags cursoryImpliedEnd
                    this.PopUntilNamed "button" |> ignore
                this.ReconstructActiveFormattingElements()
                this.InsertElementFor tag |> ignore
                framesetOk <- false
                Done
            | EndTag,
              ("address" | "article" | "aside" | "blockquote" | "button" | "center" | "details" | "dialog" | "dir" | "div" | "dl"
              | "fieldset" | "figcaption" | "figure" | "footer" | "header" | "hgroup" | "listing" | "main" | "menu" | "nav" | "ol"
              | "pre" | "search" | "section" | "summary" | "ul") ->
                if this.InScopeNamed(defaultScope, tag.Name) then
                    this.GenerateImpliedEndTags cursoryImpliedEnd
                    this.ExpectToClose tag.Name
                Done
            | EndTag, "form" ->
                if not (this.InHtmlElemNamed "template") then
                    let node = formElem
                    formElem <- -1
                    if node < 0 then
                        Done
                    elif not (this.InScope(defaultScope, (fun n -> node = n))) then
                        Done
                    else
                        this.GenerateImpliedEndTags cursoryImpliedEnd
                        this.RemoveFromStack node
                        Done
                else
                    if not (this.InScopeNamed(defaultScope, "form")) then
                        Done
                    else
                        this.GenerateImpliedEndTags cursoryImpliedEnd
                        this.PopUntilNamed "form" |> ignore
                        Done
            | EndTag, "p" ->
                if not (this.InScopeNamed(buttonScope, "p")) then this.InsertPhantom "p" |> ignore
                this.CloseP()
                Done
            | EndTag, ("li" | "dd" | "dt") ->
                let inScope =
                    if tag.Name = "li" then this.InScopeNamed(listItemScope, tag.Name) else this.InScopeNamed(defaultScope, tag.Name)
                if inScope then
                    this.GenerateImpliedEndExcept tag.Name
                    this.ExpectToClose tag.Name
                Done
            | EndTag, ("h1" | "h2" | "h3" | "h4" | "h5" | "h6") ->
                if this.InScope(defaultScope, (fun n -> this.ElemIn(n, headingTag))) then
                    this.GenerateImpliedEndTags cursoryImpliedEnd
                    this.PopUntil headingTag |> ignore
                Done
            | StartTag, "a" ->
                this.HandleMisnestedATags()
                this.ReconstructActiveFormattingElements()
                this.CreateFormattingElementFor tag |> ignore
                Done
            | StartTag, ("b" | "big" | "code" | "em" | "font" | "i" | "s" | "small" | "strike" | "strong" | "tt" | "u") ->
                this.ReconstructActiveFormattingElements()
                this.CreateFormattingElementFor tag |> ignore
                Done
            | StartTag, "nobr" ->
                this.ReconstructActiveFormattingElements()
                if this.InScopeNamed(defaultScope, "nobr") then
                    this.AdoptionAgency "nobr"
                    this.ReconstructActiveFormattingElements()
                this.CreateFormattingElementFor tag |> ignore
                Done
            | EndTag,
              ("a" | "b" | "big" | "code" | "em" | "font" | "i" | "nobr" | "s" | "small" | "strike" | "strong" | "tt" | "u") ->
                this.AdoptionAgency tag.Name
                Done
            | StartTag, ("applet" | "marquee" | "object") ->
                this.ReconstructActiveFormattingElements()
                this.InsertElementFor tag |> ignore
                activeFormatting.Add Marker
                framesetOk <- false
                Done
            | EndTag, ("applet" | "marquee" | "object") ->
                if this.InScopeNamed(defaultScope, tag.Name) then
                    this.GenerateImpliedEndTags cursoryImpliedEnd
                    this.ExpectToClose tag.Name
                    this.ClearActiveFormattingToMarker()
                Done
            | StartTag, "table" ->
                // Quirks mode is never on in a fragment parse.
                this.CloseElementInButtonScope()
                this.InsertElementFor tag |> ignore
                framesetOk <- false
                this.SetMode InsertionMode.InTable
                Done
            | EndTag, "br" -> this.Step(InsertionMode.InBody, TagToken { tag with Kind = StartTag; Attrs = [||] })
            | StartTag, ("area" | "br" | "embed" | "img" | "keygen" | "wbr" | "input") ->
                let keepFramesetOk = if tag.Name = "input" then this.IsTypeHidden tag else false
                this.ReconstructActiveFormattingElements()
                this.InsertAndPopElementFor tag |> ignore
                if not keepFramesetOk then framesetOk <- false
                DoneAckSelfClosing
            | StartTag, ("param" | "source" | "track") ->
                this.InsertAndPopElementFor tag |> ignore
                DoneAckSelfClosing
            | StartTag, "hr" ->
                this.CloseElementInButtonScope()
                this.InsertAndPopElementFor tag |> ignore
                framesetOk <- false
                DoneAckSelfClosing
            | StartTag, "image" -> this.Step(InsertionMode.InBody, TagToken { tag with Name = "img" })
            | StartTag, "textarea" ->
                ignoreLf <- true
                framesetOk <- false
                this.ParseRawData(tag, Rcdata)
            | StartTag, "xmp" ->
                this.CloseElementInButtonScope()
                this.ReconstructActiveFormattingElements()
                framesetOk <- false
                this.ParseRawData(tag, Rawtext)
            | StartTag, "iframe" ->
                framesetOk <- false
                this.ParseRawData(tag, Rawtext)
            | StartTag, "noembed" -> this.ParseRawData(tag, Rawtext)
            | StartTag, "select" ->
                this.ReconstructActiveFormattingElements()
                this.InsertElementFor tag |> ignore
                framesetOk <- false
                // mode == InBody, but this.mode may differ when processing "as in the rules for InBody".
                this.SetMode(
                    match mode with
                    | InsertionMode.InTable
                    | InsertionMode.InCaption
                    | InsertionMode.InTableBody
                    | InsertionMode.InRow
                    | InsertionMode.InCell -> InsertionMode.InSelectInTable
                    | _ -> InsertionMode.InSelect
                )
                Done
            | StartTag, ("optgroup" | "option") ->
                if this.CurrentNodeNamed "option" then this.Pop() |> ignore
                this.ReconstructActiveFormattingElements()
                this.InsertElementFor tag |> ignore
                Done
            | StartTag, ("rb" | "rtc") ->
                if this.InScopeNamed(defaultScope, "ruby") then this.GenerateImpliedEndTags cursoryImpliedEnd
                this.InsertElementFor tag |> ignore
                Done
            | StartTag, ("rp" | "rt") ->
                if this.InScopeNamed(defaultScope, "ruby") then this.GenerateImpliedEndExcept "rtc"
                this.InsertElementFor tag |> ignore
                Done
            | StartTag, "math" ->
                this.ReconstructActiveFormattingElements()
                this.EnterForeign(tag, Ns.MathMl)
            | StartTag, "svg" ->
                this.ReconstructActiveFormattingElements()
                this.EnterForeign(tag, Ns.Svg)
            | StartTag, ("caption" | "col" | "colgroup" | "frame" | "head" | "tbody" | "td" | "tfoot" | "th" | "thead" | "tr") -> Done
            | StartTag, _ ->
                // Scripting is off, so <noscript> is an ordinary element.
                this.ReconstructActiveFormattingElements()
                this.InsertElementFor tag |> ignore
                Done
            | EndTag, _ ->
                this.ProcessEndTagInBody tag
                Done

    // --- The token sink ----------------------------------------------------------------------

    /// The stack of open elements, whose length the tree depth limit watches.
    member _.OpenElementsLen = openElems.Count

    member _.AdjustedCurrentNodePresentButNotInHtmlNamespace() : bool =
        openElems.Count > 0 && (sink.ElemName(if openElems.Count = 1 && contextElem >= 0 then contextElem else openElems[openElems.Count - 1])).Ns <> Ns.Html

    /// Starts the parse: the fragment's root, and the insertion mode its context element asks for.
    member this.StartFragment() =
        if this.HtmlElemNamed(contextElem, "template") then templateModes.Add InsertionMode.InTemplate
        // https://html.spec.whatwg.org/multipage/#parsing-html-fragments
        // 5. Let root be a new html element with no attributes.
        // 6. Append the element root to the Document node created above.
        // 7. Set up the parser's stack of open elements so that it contains just the single element root.
        this.CreateRoot [||]
        // 10. Reset the parser's insertion mode appropriately.
        mode <- this.ResetInsertionMode()

    member private this.ProcessToCompletion(first: Token) : TokenSinkResult =
        // Queue of additional tokens yet to be processed. It stays empty in the common case where
        // we don't split whitespace.
        let mutable more: Queue<Token> | null = null
        let mutable token = first
        let mutable result = TokenSinkResult.Continue
        let mutable fin = false
        while not fin do
            let r = if this.IsForeign token then this.StepForeign token else this.Step(mode, token)
            match r with
            | Done
            | DoneAckSelfClosing ->
                match more with
                | null -> fin <- true
                | q when q.Count = 0 -> fin <- true
                | q -> token <- q.Dequeue()
            | Reprocess(m, t) ->
                mode <- m
                token <- t
            | SplitWhitespace buf ->
                if buf.Length = 0 then
                    fin <- true
                else
                    let isWs = Strings.isAsciiWhitespace buf[0]
                    let mutable n = 1
                    while n < buf.Length && Strings.isAsciiWhitespace buf[n] = isWs do
                        n <- n + 1
                    token <- Characters((if isWs then Whitespace else NotWhitespace), buf.Substring(0, n))
                    if n < buf.Length then
                        if isNull more then more <- Queue<Token>()
                        (nonNull more).Enqueue(Characters(NotSplit, buf.Substring n))
            | ScriptEnd node ->
                result <- TokenSinkResult.Script node
                fin <- true
            | ToPlaintext ->
                result <- TokenSinkResult.Plaintext
                fin <- true
            | ToRawData k ->
                result <- TokenSinkResult.RawDataState k
                fin <- true
        result

    member this.ProcessTag(tag: Tag) : TokenSinkResult =
        ignoreLf <- false
        this.ProcessToCompletion(TagToken tag)

    member this.ProcessComment(text: string) : unit =
        ignoreLf <- false
        this.ProcessToCompletion(CommentToken text) |> ignore

    member this.ProcessChars(text: string) : unit =
        let skip = ignoreLf
        ignoreLf <- false
        let text = if skip && text.Length > 0 && text[0] = '\n' then text.Substring 1 else text
        if text.Length > 0 then this.ProcessToCompletion(Characters(NotSplit, text)) |> ignore

    member this.ProcessNull() : unit =
        ignoreLf <- false
        this.ProcessToCompletion NullCharacter |> ignore

    member this.ProcessEof() : unit =
        ignoreLf <- false
        this.ProcessToCompletion Eof |> ignore

    /// Doctypes and parse errors: only forgetting that a newline is to be skipped.
    member this.ProcessIgnored() : unit = ignoreLf <- false

    member this.End() : unit = openElems.Clear()

/// Gumbo's tree depth limit. Before it reads each token, Gumbo stops, as if the input had ended
/// there, once the stack of open elements holds more than `max_tree_depth` elements, and Nokogiri
/// adds one to the limit for a fragment's `html` element (nokogiri's ext/nokogiri/gumbo.c).
///
/// So the stack is checked as soon as a token has been handled, before the tokenizer reads on:
/// the next token can be all the rest of the input. Gumbo doesn't check after the end of the
/// input, though, when text left pending in a table can reopen formatting elements past the limit.
[<Sealed>]
type DepthLimit(treeBuilder: TreeBuilder, input: Input, maxOpenElements: int) =
    inherit TokenSink()
    let mutable exceeded = false

    member _.Exceeded = exceeded

    /// Stops the tokenizer by taking away the rest of the input.
    member private _.Stop() =
        exceeded <- true
        input.Clear()

    member private this.Check() =
        if treeBuilder.OpenElementsLen > maxOpenElements then this.Stop()

    override this.ProcessTag tag =
        if exceeded then
            TokenSinkResult.Continue
        else
            let result = treeBuilder.ProcessTag tag
            this.Check()
            result

    override this.ProcessComment text =
        if not exceeded then
            treeBuilder.ProcessComment text
            this.Check()

    override this.ProcessChars text =
        if not exceeded then
            treeBuilder.ProcessChars text
            this.Check()

    override this.ProcessNull() =
        if not exceeded then
            treeBuilder.ProcessNull()
            this.Check()

    override this.ProcessEof() =
        if not exceeded then treeBuilder.ProcessEof()

    override this.ProcessDoctype(_) =
        if not exceeded then
            treeBuilder.ProcessIgnored()
            this.Check()

    override this.ProcessParseError() =
        if not exceeded then
            treeBuilder.ProcessIgnored()
            this.Check()

    override this.End() = treeBuilder.End()

    override this.AdjustedCurrentNodePresentButNotInHtmlNamespace() =
        treeBuilder.AdjustedCurrentNodePresentButNotInHtmlNamespace()
