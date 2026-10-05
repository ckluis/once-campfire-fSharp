// Port of rust/crates/richtext/src/lib.rs
//
// Campfire's Action Text content pipeline: what a stored message body looks like on screen, in
// the editor, and as plain text.
//
// The Rails app is the oracle (see reference/app/helpers/messages_helper.rb,
// reference/app/helpers/content_filters/, reference/app/helpers/rich_text_helper.rb and
// reference/lib/rails_ext/). Every step reproduces the Ruby it mirrors, including where Nokogiri
// serializes markup and parses it back, because the output is shaped by those round trips.
//
// Sanitization is our own allowlist walker over the html5ever port (`Sanitizer`), not a sanitizer
// library: the layers need Loofah's exact rules (unwrapping rather than dropping HTML elements,
// dropping foreign elements with their contents, its URI protocol checks, its attribute
// re-escaping), a content-removing tag filter, and the markup to come back serialized exactly as
// Nokogiri does so auto_link's regular expressions see the same text.
namespace Campfire.RichText

open System
open Campfire.RailsCompat
open Campfire.Ruby
open Campfire.RichText.RubyExt
open Campfire.RichText.Unicode

/// How `messages/_message.html.erb` shows a text message body.
[<RequireQualifiedAccess>]
type Presentation =
    | Html of string
    /// `message_tag` rescued an exception: render `messages/_unrenderable.html.erb` instead.
    | Unrenderable

module ActionText =
    /// The text branch of `MessagesHelper#message_presentation`:
    /// `auto_link h(TextMessagePresentationFilters.apply(message.body.body)), ...`.
    let messagePresentation (body: string) (ctx: RenderContext) : Result<string, RenderError> =
        result {
            let! content = Content.load body ctx
            let! filtered = Filters.apply content ctx
            let! rendered = Content.toRenderedHtmlWithLayout filtered ctx
            return! Autolink.autoLink rendered SafeList.autoLink |> parseErr
        }

    /// `messagePresentation` with its rescue: an exception renders as an empty string, unless logging
    /// it raises again, in which case the whole message is unrenderable.
    let presentMessage (body: string) (ctx: RenderContext) : Presentation =
        match messagePresentation body ctx with
        | Ok html -> Presentation.Html html
        | Error(Unrenderable _) -> Presentation.Unrenderable
        | Error _ -> Presentation.Html ""

    /// `message.body.to_plain_text` (Action Text's `RichText#to_plain_text`), which feeds
    /// `Message#plain_text_body`: the search index, push notification bodies, the webhook's plain
    /// body, emoji detection and `/play` commands.
    let toPlainText (body: string) (ctx: RenderContext) : Result<string, RenderError> =
        result {
            let! content = Content.load body ctx
            return! Content.toPlainText content ctx
        }

    /// The value Lexxy's editor receives when editing a message: `RichTextHelper#editable_body`, then
    /// Lexxy's `render_custom_attachments_in`. `None` when the body is blank (no `value` attribute).
    /// The caller HTML-escapes it into the `<lexxy-editor value="...">` attribute.
    let editableValue (body: string) (ctx: RenderContext) : Result<string option, RenderError> =
        result {
            // editable_body: every attachment rebuilt from its attachable, on the stored markup as is
            let dom = Dom()
            let! root = parseErr (dom.ParseFragment(RubyString.strip body))
            for node in Content.attachmentNodes dom root do
                let! attachment = Attachables.attachmentFromNode dom node ctx
                match attachment.Attachable with
                // A mention of a deleted user, say: nothing to edit, so it leaves the editor (Rails raises).
                | Attachable.Missing _ -> dom.Detach node
                | attachable ->
                    let! contentType = Attachables.attachableContentType attachable
                    let! content = Content.renderAttachmentHtml attachment ctx
                    dom.SetAttr(node, "content-type", contentType)
                    dom.SetAttr(node, "content", content)
            let editable = dom.ToHtml root
            if isBlank editable then
                return None
            else
                // Lexxy: attachments without a url get their rendered partial as a JSON string
                let dom = Dom()
                let! root = parseErr (dom.ParseFragment(RubyString.strip editable))
                for node in Content.attachmentNodes dom root do
                    let noUrl =
                        match dom.Attr(node, "url") with
                        | ValueNone -> true
                        | ValueSome url -> isBlank url
                    if noUrl then
                        let! attachment = Attachables.attachmentFromNode dom node ctx
                        let! content = Content.renderAttachmentHtml attachment ctx
                        dom.SetAttr(node, "content", Json.encode (Value.String content))
                return Some(dom.ToHtml root)
        }

    /// `Message::Mentionee#mentioned_users`: users attached with a verified SGID, once each.
    let mentionedUsers (body: string) (ctx: RenderContext) : Result<MentionUser list, RenderError> =
        Content.load body ctx
        |> Result.map (fun content ->
            let users = ResizeArray<MentionUser>()
            for node in Content.attachmentNodes content.Dom content.Root do
                match Attachables.actionTextAttachableFromNode content.Dom node ctx with
                | Attachable.User user when not (users.Exists(fun u -> u.Id = user.Id)) -> users.Add user
                | _ -> ()
            List.ofSeq users)

    /// `Webhook#without_recipient_mentions`: the plain body with the bot's own "@Name" removed and
    /// leading and trailing Unicode whitespace trimmed.
    let withoutRecipientMentions (plainText: string) (recipientName: string) : string =
        let replaced = plainText.Replace("@" + recipientName, "", StringComparison.Ordinal)
        let mutable a = 0
        let mutable b = replaced.Length
        while a < b && Chars.isWhitespace replaced[a] do
            a <- a + 1
        while b > a && Chars.isWhitespace replaced[b - 1] do
            b <- b - 1
        replaced.Substring(a, b - a)
