// Port of rust/crates/campfire/src/integrations/webhook.rs
//
// `Webhook#deliver` (reference/app/models/webhook.rb): a bot's webhook gets the message as JSON, and its answer becomes
// the bot's reply.
//
// Intentionally unguarded, unlike Opengraph::Fetch: only an administrator sets this URL and it may point at internal
// services. Connect and each read time out after 7 seconds, and a timeout is itself answered with a text reply.
// Unlike Rails, the whole delivery must finish within a minute and the reply is read up to 100 MB (see
// `DeliveryDeadline`, `MaxReplySize`).
namespace Campfire.App.Integrations

open System
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Campfire.Db
open Campfire.RichText
open Campfire.Storage

/// What the bot says back in the message's room.
type WebhookReply =
    | ReplyNone
    /// `room.messages.create!(body: text, creator: bot).broadcast_create`: the text is assigned as the message's
    /// rich text body, as a bot's posted body is.
    | ReplyText of string
    /// `room.messages.create_with_attachment!(attachment: blob, creator: bot).broadcast_create`, with the blob
    /// from `WebhookAttachment.stageBlob`.
    | ReplyAttachment of WebhookAttachment

and WebhookAttachment =
    { Data: byte[]
      /// `"attachment.#{mime_type.symbol}"`: "attachment." for unregistered types.
      Filename: string
      /// `mime_type.to_s`: the registered type, which for a synonym differs from what was sent.
      ContentType: string }

type WebhookDelivery =
    {
        /// The webhook's status, or `None` when it timed out.
        Status: int option
        Reply: WebhookReply
    }

/// Raised out of `deliver` (the job fails), as in Rails: a bad URL, a connection failure other than a timeout, or a
/// reply content type Rails can't parse.
type WebhookError =
    /// `URI::InvalidURIError`, or `ArgumentError` from `Net::HTTP::Post.new`.
    | InvalidUrl of string
    | Http of HttpError
    /// `Mime::Type::InvalidMimeType`
    | InvalidMimeType of string
    | ReplyTooLarge

module WebhookError =
    let message (error: WebhookError) : string =
        match error with
        | InvalidUrl message -> message
        | Http error -> HttpError.message error
        | InvalidMimeType mime -> $"\"{mime}\" is not a valid MIME type"
        | ReplyTooLarge -> "the reply is larger than 100 MB"

module WebhookClient =
    /// `Webhook::ENDPOINT_TIMEOUT`
    let EndpointTimeout: TimeSpan = TimeSpan.FromSeconds(float Campfire.Db.Webhook.EndpointTimeoutSeconds)

    /// The most a delivery may take, connecting and reading the reply included. `EndpointTimeout` applies to each
    /// read, so an endpoint that keeps trickling bytes would otherwise hold a job slot forever.
    let DeliveryDeadline: TimeSpan = TimeSpan.FromSeconds 60.0

    /// The largest reply read (after decompression); a larger one fails the delivery. Rails reads any size into
    /// memory.
    [<Literal>]
    let MaxReplySize = 104857600

    /// The upload half of `ActiveStorage::Blob.create_and_upload!(io:, filename:, content_type:)`: the caller saves
    /// the staged blob's row.
    let stageBlob (storage: Storage) (attachment: WebhookAttachment) : StorageResult<Staged> =
        Storage.stageBytes storage attachment.Data (Filename.Filename attachment.Filename) (Some attachment.ContentType)

    let private timedOut (after: TimeSpan) : WebhookDelivery =
        { Status = None
          Reply = ReplyText $"Failed to respond within {int after.TotalSeconds} seconds" }

    /// `Mime::Type::MIME_REGEXP` (in the Ruby source, "\s" inside the double-quoted parameter pattern is a literal
    /// space).
    let private mimeRegexp =
        let name = @"[a-zA-Z0-9][a-zA-Z0-9!#$&\-^_.+]{0,126}"
        let value = $@"(?:{name}|""[^""\r\\]*"")"
        let parameter = $@" *; *{name}(?:={value})?"
        Regex(@"\A(?:\*/\*|" + name + "/(?:\\*|" + name + ")(?:" + parameter + @")*[ \t\n\x0B\x0C\r]*)\z", RegexOptions.CultureInvariant)

    /// `Mime::LOOKUP` in the reference app: Action Dispatch's registrations plus turbo-rails'.
    let private mimeLookupTable: (string * string * string) list =
        [ "text/html", "html", "text/html"
          "application/xhtml+xml", "html", "text/html"
          "text/plain", "text", "text/plain"
          "text/javascript", "js", "text/javascript"
          "application/javascript", "js", "text/javascript"
          "application/x-javascript", "js", "text/javascript"
          "text/css", "css", "text/css"
          "text/calendar", "ics", "text/calendar"
          "text/csv", "csv", "text/csv"
          "text/vcard", "vcf", "text/vcard"
          "text/vtt", "vtt", "text/vtt"
          "vtt", "vtt", "text/vtt"
          "text/markdown", "md", "text/markdown"
          "image/png", "png", "image/png"
          "image/jpeg", "jpeg", "image/jpeg"
          "image/gif", "gif", "image/gif"
          "image/bmp", "bmp", "image/bmp"
          "image/tiff", "tiff", "image/tiff"
          "image/svg+xml", "svg", "image/svg+xml"
          "image/webp", "webp", "image/webp"
          "video/mpeg", "mpeg", "video/mpeg"
          "audio/mpeg", "mp3", "audio/mpeg"
          "audio/ogg", "ogg", "audio/ogg"
          "audio/aac", "m4a", "audio/aac"
          "audio/mp4", "m4a", "audio/aac"
          "video/webm", "webm", "video/webm"
          "video/mp4", "mp4", "video/mp4"
          "font/otf", "otf", "font/otf"
          "font/ttf", "ttf", "font/ttf"
          "font/woff", "woff", "font/woff"
          "font/woff2", "woff2", "font/woff2"
          "application/xml", "xml", "application/xml"
          "text/xml", "xml", "application/xml"
          "application/x-xml", "xml", "application/xml"
          "application/rss+xml", "rss", "application/rss+xml"
          "application/atom+xml", "atom", "application/atom+xml"
          "application/x-yaml", "yaml", "application/x-yaml"
          "text/yaml", "yaml", "application/x-yaml"
          "multipart/form-data", "multipart_form", "multipart/form-data"
          "application/x-www-form-urlencoded", "url_encoded_form", "application/x-www-form-urlencoded"
          "application/json", "json", "application/json"
          "text/x-json", "json", "application/json"
          "application/jsonrequest", "json", "application/json"
          "application/problem+json", "json", "application/json"
          "application/pdf", "pdf", "application/pdf"
          "application/zip", "zip", "application/zip"
          "application/gzip", "gzip", "application/gzip"
          "application/x-gzip", "gzip", "application/gzip"
          "text/vnd.turbo-stream.html", "turbo_stream", "text/vnd.turbo-stream.html" ]

    /// `Mime::Type.lookup(string)`: a registered type (by its string or a synonym), else a new unregistered type, which
    /// must be a valid MIME type.
    let mimeLookup (string': string) : Result<string option * string, WebhookError> =
        let registered (s: string) =
            mimeLookupTable |> List.tryFind (fun (key, _, _) -> key = s) |> Option.map (fun (_, symbol, toS) -> Some symbol, toS)
        match registered string' with
        | Some found -> Ok found
        | None ->
            let withoutParameters = string'.Split(';')[0]
            let trimmed = withoutParameters.TrimEnd([| ' '; '\t'; '\n'; '\011'; '\012'; '\r'; '\000' |])
            match registered trimmed with
            | Some found -> Ok found
            | None -> if mimeRegexp.IsMatch trimmed then Ok(None, trimmed) else Error(InvalidMimeType trimmed)

    /// `extract_text_from`, else `extract_attachment_from`.
    let private reply (status: int) (contentType: string option) (body: byte[]) : Result<WebhookReply, WebhookError> =
        match contentType with
        | None -> Ok ReplyNone
        | Some contentType ->
            if status = 200 && (contentType = "text/html" || contentType = "text/plain") then
                Ok(ReplyText(Text.Encoding.UTF8.GetString body))
            else
                mimeLookup contentType
                |> Result.map (fun (symbol, registered) ->
                    let extension = defaultArg symbol ""
                    ReplyAttachment
                        { Data = body
                          Filename = $"attachment.{extension}"
                          ContentType = registered })

    /// `post(payload)` over `Net::HTTP.new(uri.host, uri.port)`: the status, content type and body.
    let private post (net: Network) (url: string) (payload: string) (cancellation: CancellationToken) : Task<Result<int * string option * byte[], WebhookError>> =
        task {
            match RubyUri.parse url with
            | Error _ -> return Error(InvalidUrl("bad URI (is not URI?): " + Campfire.RailsCompat.Json.generate (Campfire.RailsCompat.Value.String url)))
            | Ok uri when not (RubyUri.isHttp uri) -> return Error(InvalidUrl "not an HTTP URI")
            | Ok uri ->
                match uri.Host |> Option.filter (fun h -> h <> "") with
                | None -> return Error(InvalidUrl "no host component for URI")
                | Some host ->
                    let https = uri.Scheme |> Option.exists (fun s -> s.Equals("https", StringComparison.OrdinalIgnoreCase))
                    match uri.Port |> Option.filter (fun p -> p <= 65535UL) with
                    | None -> return Error(InvalidUrl "invalid port")
                    | Some port ->
                        let port = int port
                        let endpoint: Endpoint =
                            { Https = https
                              Host = host
                              Port = port
                              PinnedIp = None }
                        let hostname = if host.StartsWith '[' && host.EndsWith ']' then host.Substring(1, host.Length - 2) else host
                        let uriHost = if port = (if https then 443 else 80) then hostname else $"{hostname}:{port}"
                        let request =
                            Request.netHttp "POST" (Http.requestUri uri) (Some uriHost) [ "Content-Type", "application/json" ]
                            |> Request.transport true endpoint
                        let request = { request with Body = Text.Encoding.UTF8.GetBytes payload }
                        let timeouts = { Open = EndpointTimeout; Read = EndpointTimeout }
                        match! Http.exchange net endpoint request timeouts cancellation with
                        | Error error -> return Error(Http error)
                        | Ok response ->
                            use response = response
                            let status, contentType = response.Status, response.ContentType()
                            match! response.ReadBody MaxReplySize with
                            | Error error -> return Error(Http error)
                            | Ok TooLarge -> return Error ReplyTooLarge
                            | Ok(Complete body) -> return Ok(status, contentType, body)
        }

    let deliverWithin (net: Network) (url: string) (payload: string) (deadline: TimeSpan) : Task<Result<WebhookDelivery, WebhookError>> =
        task {
            use cancellation = new CancellationTokenSource(deadline)
            let! posted =
                task {
                    try
                        return! post net url payload cancellation.Token
                    with e when cancellation.IsCancellationRequested ->
                        return Error(Http(Io e))
                }
            match posted with
            | Ok(status, contentType, body) ->
                match reply status contentType body with
                | Ok reply -> return Ok { Status = Some status; Reply = reply }
                | Error error -> return Error error
            | Error(Http(OpenTimeout | ReadTimeout)) -> return Ok(timedOut EndpointTimeout)
            | Error _ when cancellation.IsCancellationRequested -> return Ok(timedOut deadline)
            | Error error -> return Error error
        }

    /// `Webhook#deliver(message)`: `payload` is `Campfire.Db.Webhook.payload`.
    let deliver (net: Network) (url: string) (payload: string) : Task<Result<WebhookDelivery, WebhookError>> =
        deliverWithin net url payload DeliveryDeadline
