// Port of rust/crates/views/src/helpers/forms.rs
/// `form_with`, its `FormBuilder` fields, `button_to`, `hidden_field_tag` and `button_tag`,
/// reproducing ActionView's attribute order (see `form_helper.rb`, `form_tag_helper.rb`,
/// `tags/*.rb` and `url_helper.rb#button_to` in the reference image's Rails).
///
/// In templates a form is a block, so the `<form>` tag is written after its fields have rendered
/// (that's how Rails learns about `multipart` from a `file_field`): the fields render into a
/// writer of their own first, which is what `Filters.formWith` does.
///
///     let form = Forms.formWith (Routes.firstRun ()) |> fun f -> f.Model("user").Class("center")
///     Filters.formWith w form (fun w ->
///         w.Lit t1
///         form.TextField(w, "name", user.Name, Tag.attrs().Class("input")))
module Campfire.Views.Helpers.Forms

open System
open Campfire.Views
open Campfire.Views.Helpers.Tag

let private hiddenInputStart = Utf8.lit "</form>"

/// The hidden `_method` field (`method_tag`).
let methodTag (w: Out) (method: string) : unit =
    legacyTag w "input" (attrs().Type("hidden").Name("_method").Value(method))

/// `object_name.gsub(/\]\[|[^-a-zA-Z0-9:.]/, "_").delete_suffix("_")`.
let sanitizeObjectName (name: string) : string =
    let replaced = name.Replace("][", "_")
    let sanitized =
        String(replaced.ToCharArray() |> Array.map (fun c -> if Char.IsAsciiLetterOrDigit c || c = '-' || c = ':' || c = '.' then c else '_'))
    if sanitized.EndsWith '_' then sanitized.Substring(0, sanitized.Length - 1) else sanitized

/// `sanitize_to_id`: `]` removed, other non-id characters become "_".
let sanitizeToId (name: string) : string =
    let removed = name.Replace("]", "")
    String(removed.ToCharArray() |> Array.map (fun c -> if Char.IsAsciiLetterOrDigit c || c = '-' || c = '_' || c = ':' || c = '.' then c else '_'))

/// Whether a field rendered so far needs `enctype="multipart/form-data"`, shared by a form and the
/// builders it makes (`Rc<Cell<bool>>`).
[<Sealed>]
type private MultipartFlag() =
    member val IsSet = false with get, set

/// `Tags::Base#tag_name`; a model-less `form_with` names fields after the method alone.
let private tagName (objectName: string) (method: string) : string =
    if objectName = "" then method else $"{objectName}[{method}]"

/// `Tags::Base#tag_id`: the sanitized object name and method joined by "_".
let private tagId (objectName: string) (method: string) : string =
    if objectName = "" then method else $"{sanitizeObjectName objectName}_{method}"

let private addDefaultNameAndId (objectName: string) (method: string) (options: Attrs) : unit =
    options.FetchOrSet("name", ValueSome(Text(tagName objectName method)))
    options.FetchOrSet("id", ValueSome(Text(tagId objectName method)))

/// `form_with(url:, model:, method:, id:, class:, data:)`.
[<Sealed>]
type FormWith private (action: string, multipart: MultipartFlag) =
    let mutable method = "post"
    let mutable objectName: string | null = null
    let mutable id: string | null = null
    let mutable cls: string | null = null
    let mutable data = attrs ()

    internal new(action: string) = FormWith(action, MultipartFlag())

    member private _.ObjectName: string =
        match objectName with
        | null -> ""
        | name -> name

    /// `model:` — the param key fields are scoped under ("user", "account").
    member this.Model(paramKey: string) : FormWith =
        objectName <- paramKey
        this

    /// `method:`; a persisted `model:` implies "patch", so pass it for those too.
    member this.Method(value: string) : FormWith =
        method <- value
        this

    member this.Id(value: string) : FormWith =
        id <- value
        this

    member this.Class(value: string) : FormWith =
        cls <- value
        this

    member this.Data(key: string, value: AttrValue) : FormWith =
        data <- data.Data(key, value)
        this

    member this.Data(key: string, value: string) : FormWith = this.Data(key, Text value)

    /// `auto_submit_form_with` (`FormsHelper`): prepends the auto-submit Stimulus controller.
    member this.AutoSubmit() : FormWith =
        let existing = defaultArg (data.GetStr "data-controller") ""
        let controller = ("auto-submit " + existing).Trim()
        data.Set("data-controller", ValueSome(Text controller))
        this

    /// `multipart: true`; also set by any `file_field`.
    member this.Multipart() : FormWith =
        multipart.IsSet <- true
        this

    /// `<form ...>` plus the `_method` hidden field (`html_options_for_form_with` +
    /// `extra_tags_for_form`). No `authenticity_token`: forgery protection is by `Sec-Fetch-Site`.
    /// (See `Campfire.Kit`'s `Ctx.VerifyAuthenticityToken`.)
    member this.Open(w: Out) : unit =
        let html = Attrs(data.Count + 6)
        html.AttrOpt("id", id).AttrOpt("class", cls).Merge(data) |> ignore
        if multipart.IsSet then html.Attr("enctype", "multipart/form-data") |> ignore
        let method = method.ToLowerInvariant()
        let formMethod = if method = "get" then "get" else "post"
        html.Attr("action", action).Attr("accept-charset", "UTF-8").Attr("method", formMethod) |> ignore
        openTag w "form" html
        w.Byte(byte '>')
        match method with
        | "get"
        | "post"
        | "" -> ()
        | _ -> methodTag w method

    /// The whole form around `content`, which writes the fields (a block-less `form_with` passes
    /// nothing). The fields render first, into a writer of their own, so a `file_field` among them
    /// can make the form multipart.
    member this.Wrap(w: Out, content: Out -> unit) : unit =
        let inner = Out.Rent 512
        try
            content inner
            this.Open w
            w.Append inner
            w.Lit hiddenInputStart
        finally
            Out.Return inner

    member this.TextField(w: Out, method: string, value: string option, options: Attrs) : unit =
        this.InputField(w, "text", method, value, options)

    member this.EmailField(w: Out, method: string, value: string option, options: Attrs) : unit =
        this.InputField(w, "email", method, value, options)

    member this.UrlField(w: Out, method: string, value: string option, options: Attrs) : unit =
        this.InputField(w, "url", method, value, options)

    /// Password fields never render the model's value (`{ value: nil }.merge!(options)`).
    member this.PasswordField(w: Out, method: string, options: Attrs) : unit =
        let merged = attrs ()
        merged.Set("value", ValueNone)
        this.InputField(w, "password", method, None, merged.Merge options)

    member this.HiddenField(w: Out, method: string, value: string option, options: Attrs) : unit =
        this.InputField(w, "hidden", method, value, options)

    member this.FileField(w: Out, method: string, options: Attrs) : unit =
        multipart.IsSet <- true
        this.InputField(w, "file", method, None, options)

    member this.TextArea(w: Out, method: string, value: string option, options: Attrs) : unit =
        addDefaultNameAndId this.ObjectName method options
        // `Tags::TextArea#render`: the value is the element's content, after a newline.
        let content =
            match options.Remove "value" with
            | ValueSome value -> value.AsString
            | ValueNone -> defaultArg value ""
        contentTagText w "textarea" options content

    /// `form.check_box(method, options, checked_value, unchecked_value)`; `current` is the
    /// model's value, compared with `checked_value`.
    member this.CheckBox(w: Out, method: string, options: Attrs, checkedValue: string, uncheckedValue: string, current: string) : unit =
        // `Tags::CheckBox#render`: a hidden unchecked value, then the checkbox.
        options.Set("type", ValueSome(Text "checkbox"))
        options.Set("value", ValueSome(Text checkedValue))
        if current = checkedValue then options.Set("checked", ValueSome(Text "checked"))
        addDefaultNameAndId this.ObjectName method options
        let hidden = attrs ()
        for key in [ "name"; "disabled"; "form" ] do
            if options.Has key then hidden.Set(key, options.Get key)
        legacyTag w "input" (hidden.Type("hidden").Value(uncheckedValue))
        legacyTag w "input" options

    /// `form.fields_for(:settings)`: a builder for `object_name[settings]`.
    member this.FieldsFor(name: string) : FormWith =
        let nested = this.Clone()
        nested.Model($"{this.ObjectName}[{name}]") |> ignore
        nested

    member this.Clone() : FormWith =
        let copy = FormWith(action, multipart)
        copy.Method method |> ignore
        match objectName with
        | null -> ()
        | name -> copy.Model name |> ignore
        match id with
        | null -> ()
        | value -> copy.Id value |> ignore
        match cls with
        | null -> ()
        | value -> copy.Class value |> ignore
        copy.ReplaceData(data.Copy())
        copy

    member internal _.ReplaceData(value: Attrs) : unit = data <- value

    /// `Tags::TextField#render`.
    member private this.InputField(w: Out, fieldType: string, method: string, value: string option, options: Attrs) : unit =
        if fieldType = "file" then multipart.IsSet <- true
        if not (options.Has "size") then options.Set("size", options.Get "maxlength")
        options.SetDefault("type", ValueSome(Text fieldType))
        if fieldType <> "file" then
            options.FetchOrSet("value", (match value with Some v -> ValueSome(Text v) | None -> ValueNone))
        addDefaultNameAndId this.ObjectName method options
        legacyTag w "input" options

let formWith (url: string) : FormWith = FormWith url

/// `form.button(options) { ... }` / `button_tag`.
let buttonTag (w: Out) (options: Attrs) (content: string) : unit =
    contentTag w "button" (attrs().Attr("name", "button").Attr("type", "submit").Merge options) content

/// `button_tag`'s attributes: `{ name: "button", type: "submit" }` merged with the options.
let buttonOptions (options: Attrs) : Attrs = attrs().Attr("name", "button").Attr("type", "submit").Merge options

/// `hidden_field_tag(name, value, options)`.
let hiddenFieldTag (w: Out) (name: string) (value: string option) (options: Attrs) : unit =
    let baseAttrs = attrs().Type("hidden").Name(name).Id(sanitizeToId name).AttrOpt("value", value)
    legacyTag w "input" (baseAttrs.Merge options)

/// The form, its `_method` field and the opening button tag.
let openButtonTo (w: Out) (url: string) (options: Attrs) : unit =
    let method =
        match options.Remove "method" with
        | ValueSome value -> value.AsString
        | ValueNone -> "post"
    let formClass =
        match options.Remove "form_class" with
        | ValueSome value -> value.AsString
        | ValueNone -> "button_to"
    let formMethod = if method = "get" then "get" else "post"
    openTag w "form" (attrs().Attr("class", formClass).Attr("method", formMethod).Attr("action", url))
    w.Byte(byte '>')
    if method = "delete" || method = "patch" || method = "put" then methodTag w method
    openContentTag w "button" (options.Attr("type", "submit"))

let closeButtonTo (w: Out) : unit =
    closeTag w "button"
    w.Lit hiddenInputStart

/// `button_to(url, options) { content }`. `options` may carry `method` ("delete", "put",
/// "patch", "post" or "get"), `form_class`, and the button's own attributes.
let buttonTo (w: Out) (url: string) (options: Attrs) (content: string) : unit =
    openButtonTo w url options
    w.Raw content
    closeButtonTo w

/// `button_to(url, options) do ... end`: the block renders straight into the button.
let inline buttonToBlock (w: Out) (url: string) (options: Attrs) ([<InlineIfLambda>] content: Out -> unit) : unit =
    openButtonTo w url options
    content w
    closeButtonTo w
