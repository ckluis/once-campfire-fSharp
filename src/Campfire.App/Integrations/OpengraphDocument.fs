// Port of rust/crates/campfire/src/integrations/opengraph/document.rs
//
// `Opengraph::Document` (reference/app/models/opengraph/document.rb).
namespace Campfire.App.Integrations

open System

module OpengraphDocument =
    /// `Opengraph::Metadata::ATTRIBUTES`, in the order `Hash#slice` returns them.
    let Attributes: string list = [ "title"; "url"; "image"; "description" ]

    /// `String#blank?`: empty or only (Unicode) whitespace. (`Char.IsWhiteSpace` also counts U+001C to U+001F,
    /// which `char::is_whitespace` and `[[:space:]]` don't.)
    let isBlank (s: string) : bool =
        let whiteSpace (c: char) =
            (c >= '\t' && c <= '\r')
            || c = ' '
            || c = '\u0085'
            || c = ' '
            || c = ' '
            || (c >= ' ' && c <= ' ')
            || c = ' '
            || c = ' '
            || c = ' '
            || c = ' '
            || c = '　'
        s |> Seq.forall whiteSpace

    /// `//*/meta[starts-with(@property, "og:") or starts-with(@name, "og:")]`
    let private isOpengraphTag (meta: MetaElement) : bool =
        let startsWithOg (value: string option) =
            match value with
            | Some value -> value.StartsWith("og:", StringComparison.Ordinal)
            | None -> false
        startsWithOg (meta.Attr "property") || startsWithOg (meta.Attr "name")

    /// `opengraph_attributes`: from each `meta` whose `property` or `name` starts with "og:", the key is that
    /// attribute (`property` when present) with every "og:" removed, and the value its non-blank `content`. Later
    /// tags win. Without a meta charset, non-ASCII characters are dropped
    /// (`content.encode("UTF-8", "binary", invalid: :replace, undef: :replace, replace: "")`).
    let opengraphAttributes (body: byte[] option) : (string * string) list =
        let html = OpengraphHtml.decode (defaultArg body [||])
        let metas = OpengraphHtml.metaElements html
        let metaEncoding = OpengraphHtml.metaEncoding metas

        // Only the `ATTRIBUTES` keys are sliced out, so only they are kept.
        let found = Collections.Generic.Dictionary<string, string>()
        for meta in metas |> List.filter isOpengraphTag do
            let key = if meta.HasAttr "property" then "property" else "name"
            let name = (defaultArg (meta.Attr key) "").Replace("og:", "")
            if List.contains name Attributes then
                match meta.Attr "content" |> Option.filter (fun c -> not (isBlank c)) with
                | Some content ->
                    let content = if metaEncoding.IsSome then content else String(content.ToCharArray() |> Array.filter (fun c -> c < '\u0080'))
                    found[name] <- content
                | None -> ()
        [ for key in Attributes do
              match found.TryGetValue key with
              | true, value -> key, value
              | _ -> () ]
