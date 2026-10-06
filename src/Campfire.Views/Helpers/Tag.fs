// Port of rust/crates/views/src/helpers/{tag.rs, html.rs}
/// `ActionView::Helpers::TagHelper`: attribute rendering (`tag_options`) and element builders.
///
/// `Attrs` keeps insertion order, since Rails renders attributes in hash order. Build one with
/// `attrs ()` and chain setters, mirroring the Ruby options hash:
/// `attrs().Class("btn").Data("turbo_frame", "_top")` is `class: "btn", data: { turbo_frame: "_top" }`.
///
/// Rust moves an `Attrs` into the helper it is for, and the helper adds to it or rearranges it
/// (`view()` copies it where the caller keeps it). The same rule here: an `Attrs` is built for one
/// tag and handed over, never kept to be passed again; `Copy` makes one that can be.
module Campfire.Views.Helpers.Tag

open System
open System.Collections.Generic
open Campfire.Ruby
open Campfire.Views

/// `TagHelper::BOOLEAN_ATTRIBUTES`: rendered as `name="name"` when true, omitted when false.
let private booleanAttributes =
    HashSet<string>(
        [ "allowfullscreen"; "allowpaymentrequest"; "async"; "autofocus"; "autoplay"; "checked"; "compact"; "controls"; "declare"; "default"
          "defaultchecked"; "defaultmuted"; "defaultselected"; "defer"; "disabled"; "enabled"; "formnovalidate"; "hidden"; "indeterminate"
          "inert"; "ismap"; "itemscope"; "loop"; "multiple"; "muted"; "nohref"; "nomodule"; "noresize"; "noshade"; "novalidate"; "nowrap"
          "open"; "pauseonexit"; "playsinline"; "readonly"; "required"; "reversed"; "scoped"; "seamless"; "selected"; "sortable"; "truespeed"
          "typemustmatch"; "visible" ],
        StringComparer.Ordinal
    )

/// HTML void elements, which the `tag.*` builder renders without a closing tag.
let private voidElements =
    HashSet<string>(
        [ "area"; "base"; "br"; "col"; "embed"; "hr"; "img"; "input"; "keygen"; "link"; "meta"; "source"; "track"; "wbr" ],
        StringComparer.Ordinal
    )

/// An attribute's value.
[<Struct>]
type AttrValue =
    /// A plain string, escaped on output.
    | Text of text: string
    /// An html_safe string: only `"` is replaced on output.
    | Safe of html: string
    | Bool of flag: bool
    /// An integer, which Rails prints with `to_s`.
    | Int of number: int64
    /// `text` followed by an integer, written without making the string (`dom_id`, `"avatar-#{id}"`).
    /// `text` is a literal or a cached prefix of the template's own, so it is written as it is, unescaped:
    /// never user text.
    | Numbered of text: string * number: int64

    /// The value as Ruby's `to_s` prints it.
    member this.AsString: string =
        match this with
        | Text text
        | Safe text -> text
        | Bool true -> "true"
        | Bool false -> "false"
        | Int number -> number.ToString(Globalization.CultureInfo.InvariantCulture)
        | Numbered(text, number) -> text + number.ToString(Globalization.CultureInfo.InvariantCulture)

/// `Value`'s conversions from the types templates pass.
module AttrValue =
    let ofHtml (html: Html) : AttrValue = Safe(html.ToString())

/// `"data-" + key.dasherize` and `"aria-" + key.dasherize`, made once per distinct key: the keys are
/// the literals of templates, so the tables stay as small as the templates are and no request makes
/// a name.
module private AttrNames =
    let private data = System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal)
    let private aria = System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal)

    let private name (table: System.Collections.Concurrent.ConcurrentDictionary<string, string>) (prefix: string) (key: string) : string =
        match table.TryGetValue key with
        | true, found -> found
        | _ ->
            let made = String.Intern(prefix + key.Replace('_', '-'))
            table[key] <- made
            made

    let dataName (key: string) : string = name data "data-" key
    let ariaName (key: string) : string = name aria "aria-" key

let private eqQuote = Utf8.lit "=\""
let private quot = Utf8.lit "&quot;"
let private closeOpen = Utf8.lit "</"
let private selfClose = Utf8.lit " />"

/// An ordered options hash. A `ValueNone` value is kept so that later assignments stay in place
/// (Ruby's `options["value"] = nil` keeps the key's position) but is never rendered.
[<Sealed; AllowNullLiteral>]
type Attrs(capacity: int) =
    let mutable names: string[] = Array.zeroCreate (max capacity 4)
    let mutable values: AttrValue voption[] = Array.zeroCreate (max capacity 4)
    let mutable count = 0

    new() = Attrs 4

    member _.Count: int = count
    member _.IsEmpty: bool = count = 0

    member _.NameAt(index: int) : string = names[index]
    member _.ValueAt(index: int) : AttrValue voption = values[index]

    member private _.IndexOf(name: string) : int =
        let mutable found = -1
        let mutable i = 0
        while found < 0 && i < count do
            if String.Equals(names[i], name, StringComparison.Ordinal) then found <- i
            i <- i + 1
        found

    /// Sets `name`, replacing an earlier value in place (Ruby hash assignment semantics).
    member this.Set(name: string, value: AttrValue voption) : unit =
        match this.IndexOf name with
        | -1 ->
            if count = names.Length then
                Array.Resize(&names, count * 2)
                Array.Resize(&values, count * 2)
            names[count] <- name
            values[count] <- value
            count <- count + 1
        | i -> values[i] <- value

    member this.Attr(name: string, value: AttrValue) : Attrs =
        this.Set(name, ValueSome value)
        this

    member this.Attr(name: string, value: string) : Attrs = this.Attr(name, Text value)
    member this.Attr(name: string, value: bool) : Attrs = this.Attr(name, Bool value)
    member this.Attr(name: string, value: int) : Attrs = this.Attr(name, Int(int64 value))
    member this.Attr(name: string, value: int64) : Attrs = this.Attr(name, Int value)
    member this.Attr(name: string, value: Html) : Attrs = this.Attr(name, AttrValue.ofHtml value)

    /// Sets `name` only when `value` is `Some`, like passing a possibly-nil option.
    member this.AttrOpt(name: string, value: string option) : Attrs =
        this.Set(name, (match value with Some v -> ValueSome(Text v) | None -> ValueNone))
        this

    member this.AttrOpt(name: string, value: string | null) : Attrs =
        this.Set(name, (match value with null -> ValueNone | v -> ValueSome(Text v)))
        this

    /// `data: { key: value }`: `data-key` with underscores dashed.
    member this.Data(key: string, value: AttrValue) : Attrs = this.Attr(AttrNames.dataName key, value)
    member this.Data(key: string, value: string) : Attrs = this.Data(key, Text value)
    member this.Data(key: string, value: bool) : Attrs = this.Data(key, Bool value)
    member this.Data(key: string, value: int) : Attrs = this.Data(key, Int(int64 value))

    member this.Aria(key: string, value: AttrValue) : Attrs = this.Attr(AttrNames.ariaName key, value)
    member this.Aria(key: string, value: string) : Attrs = this.Aria(key, Text value)
    member this.Aria(key: string, value: bool) : Attrs = this.Aria(key, Bool value)

    /// `aria: { hidden: "true" }`, the most common option in Campfire's views.
    member this.AriaHidden() : Attrs = this.Aria("hidden", "true")

    member this.Class(value: string) : Attrs = this.Attr("class", value)
    member this.Id(value: string) : Attrs = this.Attr("id", value)
    member this.Id(value: AttrValue) : Attrs = this.Attr("id", value)
    member this.Style(value: string) : Attrs = this.Attr("style", value)
    member this.Style(value: AttrValue) : Attrs = this.Attr("style", value)
    member this.Title(value: string) : Attrs = this.Attr("title", value)
    member this.Alt(value: string) : Attrs = this.Attr("alt", value)
    member this.Role(value: string) : Attrs = this.Attr("role", value)
    member this.Name(value: string) : Attrs = this.Attr("name", value)
    member this.Type(value: string) : Attrs = this.Attr("type", value)
    member this.Value(value: string) : Attrs = this.Attr("value", value)
    member this.Target(value: string) : Attrs = this.Attr("target", value)
    member this.Placeholder(value: string) : Attrs = this.Attr("placeholder", value)
    member this.Autocomplete(value: string) : Attrs = this.Attr("autocomplete", value)
    member this.Accept(value: string) : Attrs = this.Attr("accept", value)
    member this.Loading(value: string) : Attrs = this.Attr("loading", value)
    member this.Tabindex(value: int) : Attrs = this.Attr("tabindex", value)
    member this.Maxlength(value: int) : Attrs = this.Attr("maxlength", value)
    member this.Rows(value: int) : Attrs = this.Attr("rows", value)
    /// `image_tag`'s `size:` option, expanded into width and height by `Assets.imageTag`.
    member this.Size(value: int) : Attrs = this.Attr("size", value)
    member this.Size(value: string) : Attrs = this.Attr("size", value)
    member this.Method(value: string) : Attrs = this.Attr("method", value)

    member this.Hidden() : Attrs = this.Attr("hidden", true)
    member this.Required(value: bool) : Attrs = this.Attr("required", value)
    member this.Autofocus() : Attrs = this.Attr("autofocus", true)
    member this.Readonly() : Attrs = this.Attr("readonly", true)
    member this.Disabled(value: bool) : Attrs = this.Attr("disabled", value)
    member this.Checked(value: bool) : Attrs = this.Attr("checked", value)

    /// `options[name] ||= value`: sets only when absent or nil.
    member this.SetDefault(name: string, value: AttrValue voption) : unit =
        if (this.Get name).IsNone then this.Set(name, value)

    /// `options.fetch(name) { value }`: sets only when the key is absent (a nil stays nil).
    member this.FetchOrSet(name: string, value: AttrValue voption) : unit =
        if not (this.Has name) then this.Set(name, value)

    member this.Get(name: string) : AttrValue voption =
        match this.IndexOf name with
        | -1 -> ValueNone
        | i -> values[i]

    member this.GetStr(name: string) : string option =
        match this.Get name with
        | ValueSome value -> Some value.AsString
        | ValueNone -> None

    member this.Has(name: string) : bool = this.IndexOf name >= 0

    member this.Remove(name: string) : AttrValue voption =
        match this.IndexOf name with
        | -1 -> ValueNone
        | i ->
            let removed = values[i]
            for j in i .. count - 2 do
                names[j] <- names[j + 1]
                values[j] <- values[j + 1]
            count <- count - 1
            removed

    /// Appends `other`'s entries, overriding in place like `Hash#merge!`.
    member this.Merge(other: Attrs) : Attrs =
        for i in 0 .. other.Count - 1 do
            this.Set(other.NameAt i, other.ValueAt i)
        this

    /// A copy with room for a few more entries (`Attrs::view`).
    member this.Copy() : Attrs =
        let copy = Attrs(count + 4)
        for i in 0 .. count - 1 do
            copy.Set(names[i], values[i])
        copy

    /// `link_to(url, **attributes, data: defaults.merge(attributes.delete(:data)))`: the default
    /// data attributes go where the caller's first `data-*` attribute is (or at the end), ahead of
    /// the caller's own data attributes, which override them.
    member this.WithDefaultData(defaults: (string * AttrValue)[]) : Attrs =
        let isData (name: string) = name.StartsWith("data-", StringComparison.Ordinal)
        let mutable position = count
        let mutable i = count - 1
        while i >= 0 do
            if isData names[i] then position <- i
            i <- i - 1
        let before = Attrs(count + defaults.Length)
        for i in 0 .. position - 1 do
            before.Set(names[i], values[i])
        let data = Attrs(defaults.Length + 4)
        for (key, value) in defaults do
            data.Set(key, ValueSome value)
        let after = Attrs 4
        for i in position .. count - 1 do
            if isData names[i] then data.Set(names[i], values[i]) else after.Set(names[i], values[i])
        for i in 0 .. data.Count - 1 do
            before.Set(data.NameAt i, data.ValueAt i)
        for i in 0 .. after.Count - 1 do
            before.Set(after.NameAt i, after.ValueAt i)
        before

let attrs () : Attrs = Attrs()

/// One attribute of `tag_options`, with Rails' value rules.
let private renderAttr (w: Out) (name: string) (value: AttrValue) : unit =
    let start () =
        w.Byte(byte ' ')
        w.Raw name
        w.Lit eqQuote
    match value with
    | Bool flag when booleanAttributes.Contains name ->
        if flag then
            start ()
            w.Raw name
            w.Byte(byte '"')
    | Bool _ ->
        start ()
        w.Raw value.AsString
        w.Byte(byte '"')
    | Int number ->
        start ()
        w.Int number
        w.Byte(byte '"')
    | Numbered(text, number) ->
        start ()
        w.Raw text
        w.Int number
        w.Byte(byte '"')
    | Text text ->
        start ()
        w.Text text
        w.Byte(byte '"')
    | Safe html ->
        start ()
        let mutable rest = html.AsSpan()
        let mutable index = rest.IndexOf '"'
        while index >= 0 do
            w.Raw(rest.Slice(0, index))
            w.Lit quot
            rest <- rest.Slice(index + 1)
            index <- rest.IndexOf '"'
        w.Raw rest
        w.Byte(byte '"')

/// `tag_options`: the attributes, each with a leading space.
let renderAttrs (w: Out) (attrs: Attrs) : unit =
    for i in 0 .. attrs.Count - 1 do
        match attrs.ValueAt i with
        | ValueSome value -> renderAttr w (attrs.NameAt i) value
        | ValueNone -> ()

/// `String#dasherize`.
let dasherize (key: string) : string = key.Replace('_', '-')

/// `<name attributes` (the tag left open).
let openTag (w: Out) (name: string) (attrs: Attrs) : unit =
    w.Byte(byte '<')
    w.Raw name
    renderAttrs w attrs

/// `content_tag`'s opening tag. A textarea's content starts on a new line.
let openContentTag (w: Out) (name: string) (attrs: Attrs) : unit =
    openTag w name attrs
    w.Byte(byte '>')
    if name = "textarea" then w.Byte(byte '\n')

let closeTag (w: Out) (name: string) : unit =
    w.Lit closeOpen
    w.Raw name
    w.Byte(byte '>')

/// `content_tag(name, content, options)` with already-safe content.
let contentTag (w: Out) (name: string) (attrs: Attrs) (content: string) : unit =
    openContentTag w name attrs
    w.Raw content
    closeTag w name

/// `content_tag(name, options) do ... end`: the block renders straight into the tag.
let inline contentTagBlock (w: Out) (name: string) (attrs: Attrs) ([<InlineIfLambda>] content: Out -> unit) : unit =
    openContentTag w name attrs
    content w
    closeTag w name

/// `content_tag` with plain-text content, escaped.
let contentTagText (w: Out) (name: string) (attrs: Attrs) (content: string) : unit =
    openContentTag w name attrs
    w.Text content
    closeTag w name

/// `tag.name(**options)` from the tag builder: void elements have no closing tag and no slash,
/// others render empty. Underscores in the name become dashes (`tag.turbo_frame`).
let builderTag (w: Out) (name: string) (attrs: Attrs) : unit =
    let name = if name.Contains '_' then dasherize name else name
    openTag w name attrs
    w.Byte(byte '>')
    if not (voidElements.Contains name) then closeTag w name

/// Legacy `tag(:name, options)`, used by `image_tag` and form fields: always self-closing with `" />"`.
let legacyTag (w: Out) (name: string) (attrs: Attrs) : unit =
    openTag w name attrs
    w.Lit selfClose

// html.rs

/// `ERB::Util.html_escape`.
let escape (text: string) : string = Erb.htmlEscape text
