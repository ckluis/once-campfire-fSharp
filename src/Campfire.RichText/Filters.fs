// Port of rust/crates/richtext/src/filters.rs
//
// `ContentFilters::TextMessagePresentationFilters` (reference/app/helpers/content_filters/*.rb):
// RemoveSoloUnfurledLinkText, SanitizeTags, SanitizeAttributes, applied in that order.
module Campfire.RichText.Filters

open System
open Campfire.Ruby
open Campfire.RichText.RubyExt

// --- RemoveSoloUnfurledLinkText ----------------------------------------------------------------

let private twitterDomains = [ "x.com"; "twitter.com" ]

let normalizeTweetUrl (url: string option) : Result<string option, RenderError> =
    match url with
    | None -> Ok None
    | Some url ->
        let isTwitterUrl = not (isBlank url) && twitterDomains |> List.exists (fun d -> (RubyString.strip url).Contains(d, StringComparison.Ordinal))
        if not isTwitterUrl then
            Ok(Some url)
        else
            match RubyUri.parse url with
            | Error InvalidUri -> Ok(Some url)
            | Error InvalidComponent -> Error(Raised "URI::InvalidComponentError")
            | Ok parsed ->
                let host =
                    if parsed.Host |> Option.map Unicode.Chars.toLowercase = Some "x.com" then Some "twitter.com" else parsed.Host
                Ok(Some(RubyUri.toS { parsed with Host = host; Query = None }))

/// A message that is nothing but a link to what it unfurls shows just the unfurl.
let removeSoloUnfurledLinkText (content: Content) (ctx: RenderContext) : Result<Content, RenderError> =
    let dom = content.Dom
    let root = content.Root
    let unfurledLinks =
        dom.Descendants root
        |> Seq.filter (fun n -> dom.IsNamed(n, Content.AttachmentTag) && dom.Attr(n, "content-type") = ValueSome Attachables.OpengraphEmbedContentType)
        |> List.ofSeq
    result {
        let! soloUnfurledUrl =
            if unfurledLinks.Length = 1 then
                Attachables.opengraphEmbedFromNode dom unfurledLinks[0] ctx |> Result.map (Option.bind (fun embed -> embed.Href))
            else
                Ok None
        let! plainText = Content.toPlainText content ctx
        let! left = normalizeTweetUrl soloUnfurledUrl
        let! right = normalizeTweetUrl (Some plainText)
        if left <> right then
            return content
        else
            let isTrixBody = dom.Descendants root |> Seq.exists (fun n -> dom.IsNamed(n, "div"))
            if isTrixBody then
                // Every div gets the unfurl as its only content
                let unfurl = dom.ToHtml unfurledLinks[0]
                for div in dom.Descendants root |> Seq.filter (fun n -> dom.IsNamed(n, "div")) |> List.ofSeq do
                    do! parseErr (dom.SetInnerHtml(div, unfurl))
            else
                for p in dom.Descendants root |> Seq.filter (fun n -> dom.IsNamed(n, "p")) |> List.ofSeq do
                    let hasAttachment = dom.Descendants p |> Seq.exists (fun n -> dom.IsNamed(n, Content.AttachmentTag))
                    if not hasAttachment then dom.Detach p
            return content
    }

// --- SanitizeTags ------------------------------------------------------------------------------

let private sanitizeTagsAllowed =
    System.Collections.Generic.HashSet<string>(Sanitizer.sanitizeTagsAllowedTags, System.StringComparer.Ordinal)

/// Removes every element outside the allowlist, together with its contents.
let sanitizeTags (content: Content) : Content =
    let dom = content.Dom
    let allowed = sanitizeTagsAllowed
    let disallowed =
        dom.Descendants content.Root
        |> Seq.filter (fun n ->
            match dom.LocalName n with
            | ValueSome name -> not (allowed.Contains name)
            | ValueNone -> false)
        |> List.ofSeq
    for node in disallowed do
        dom.Detach node
    content

// --- SanitizeAttributes ------------------------------------------------------------------------

/// Scrubs attributes with Rails' safe-list sanitizer over SanitizeTags' own tags.
let sanitizeAttributes (content: Content) : Result<Content, RenderError> =
    result {
        let! html = Sanitizer.sanitize (Content.toHtml content) SafeList.contentFilter |> parseErr
        return! Content.wrap html
    }

let apply (content: Content) (ctx: RenderContext) : Result<Content, RenderError> =
    result {
        let! content = removeSoloUnfurledLinkText content ctx
        let content = sanitizeTags content
        return! sanitizeAttributes content
    }
