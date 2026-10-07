// Port of rust/crates/campfire/src/integrations/opengraph/metadata.rs
//
// `Opengraph::Metadata` and its `Fetching` concern (reference/app/models/opengraph/metadata.rb,
// reference/app/models/opengraph/metadata/fetching.rb).
namespace Campfire.App.Integrations

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Campfire.RailsCompat
open Campfire.RichText

/// Where the Rails action raises (a 500).
type UnfurlError = Raised of string

module UnfurlError =
    let message (error: UnfurlError) : string =
        match error with
        | Raised cls -> $"raised {cls}"

module OpengraphMetadata =
    let private twitterHosts = [ "twitter.com"; "www.twitter.com"; "x.com"; "www.x.com" ]

    [<Literal>]
    let private FxTwitterHost = "fxtwitter.com"

    let private allowedImageContentTypes = [ "image/jpeg"; "image/png"; "image/gif"; "image/webp" ]

    /// Pages parsed at once. The blocking work outlives an unfurl that gives up at its deadline, so it's bounded by
    /// permits the work itself holds, not by the unfurl's slot.
    [<Literal>]
    let private MaxConcurrentParses = 4

    let private parses = new SemaphoreSlim(MaxConcurrentParses)

    /// Runs CPU-bound work (parsing and sanitizing pages of up to 5MB) off the async workers.
    let offTheRuntime (work: unit -> 'T) : Task<'T> =
        task {
            do! parses.WaitAsync()
            try
                return! Task.Run work
            finally
                parses.Release() |> ignore
        }

    /// The model's attributes as instance variables, in the order they were first assigned (which is the order
    /// `render json:` emits them).
    type Metadata = { Attributes: (string * string option) list }

    let private assign (attributes: (string * string option) list) (key: string) (value: string option) : (string * string option) list =
        if attributes |> List.exists (fun (k, _) -> k = key) then
            attributes |> List.map (fun (k, v) -> if k = key then k, value else k, v)
        else
            attributes @ [ key, value ]

    let get (metadata: Metadata) (key: string) : string option =
        metadata.Attributes |> List.tryFind (fun (k, _) -> k = key) |> Option.bind snd

    /// `tweet_url?`
    let private tweetUrl (url: string) : Result<bool, UnfurlError> =
        match RubyUri.parse url with
        | Ok uri ->
            Ok(
                (uri.Host |> Option.exists (fun h -> List.contains h twitterHosts))
                && (uri.Path |> Option.exists (fun p -> not (OpengraphDocument.isBlank p) && p <> "/"))
            )
        | Error InvalidUri -> Ok false
        | Error InvalidComponent -> Error(Raised "URI::InvalidComponentError")

    /// `replace_twitter_domain_for_opengraph_support`
    let private replaceTwitterDomain (url: string) : string option =
        match RubyUri.parse url with
        | Error _ -> None
        | Ok uri ->
            let uri =
                if uri.Host |> Option.exists (fun h -> List.contains h twitterHosts) then { uri with Host = Some FxTwitterHost } else uri
            Some(RubyUri.toS uri)

    /// `fetch_document(untrusted_url)`: tweets are read through fxtwitter.com. A tweet whose fxtwitter page can't be
    /// read raises (`nil.force_encoding`), as does a URL that `URI.parse` rejects with `URI::InvalidComponentError`.
    let private fetchDocument (net: Network) (url: string) (cancellation: CancellationToken) (logger: ILogger) : Task<Result<byte[] option, UnfurlError>> =
        task {
            match tweetUrl url with
            | Error error -> return Error error
            | Ok true ->
                let fxtwitterUrl = replaceTwitterDomain url
                let! html = Location(net, fxtwitterUrl, cancellation, logger).ReadHtml()
                match html with
                | Some html -> return Ok(Some html)
                | None -> return Error(Raised "NoMethodError")
            | Ok false ->
                let! html = Location(net, Some url, cancellation, logger).ReadHtml()
                return Ok html
        }

    /// `valid_canonical_url(url, fallback)`
    let private validCanonicalUrl (net: Network) (url: string option) (fallback: string) (cancellation: CancellationToken) (logger: ILogger) : Task<string> =
        task {
            match url with
            | Some url ->
                let! valid = Location(net, Some url, cancellation, logger).IsValid()
                return if valid then url else fallback
            | None -> return fallback
        }

    /// `valid_image_content_type(image)`: kept only when a HEAD says it's a JPEG, PNG, GIF or WebP.
    let private validImageContentType (net: Network) (image: string option) (cancellation: CancellationToken) (logger: ILogger) : Task<string option> =
        task {
            match image |> Option.filter (fun i -> not (OpengraphDocument.isBlank i)) with
            | None -> return None
            | Some image ->
                match RubyUri.parse image with
                | Error _ ->
                    logger.LogWarning("Failed to fetch image content tpye: {Image} (bad URI(is not URI?): {Quoted})", image, Json.generate (Value.String image))
                    return None
                | Ok _ ->
                    let! contentType = Location(net, Some image, cancellation, logger).FetchContentType()
                    match contentType |> Option.map (fun c -> c.ToLowerInvariant()) with
                    | Some contentType when List.contains contentType allowedImageContentTypes -> return Some image
                    | _ -> return None
        }

    /// `Metadata.from_url(url)`
    let fromUrl (net: Network) (url: string) (cancellation: CancellationToken) (logger: ILogger) : Task<Result<Metadata, UnfurlError>> =
        task {
            match! fetchDocument net url cancellation logger with
            | Error error -> return Error error
            | Ok body ->
                let! found = offTheRuntime (fun () -> OpengraphDocument.opengraphAttributes body)
                let og (key: string) = found |> List.tryFind (fun (k, _) -> k = key) |> Option.map snd
                let! canonicalUrl = validCanonicalUrl net (og "url") url cancellation logger
                let! image = validImageContentType net (og "image") cancellation logger
                let attributes = found |> List.map (fun (k, v) -> k, Some v)
                let attributes = assign attributes "url" (Some canonicalUrl)
                let attributes = assign attributes "image" image
                return Ok { Attributes = attributes }
        }

    /// `strip_tags` (Rails::HTML5::FullSanitizer): the text of the HTML5 fragment, serialized.
    let private stripTags (html: string) : Result<string, UnfurlError> =
        if html = "" then
            Ok ""
        else
            let dom = Dom()
            match dom.ParseFragment html with
            | Error _ -> Error(Raised "ArgumentError")
            | Ok fragment ->
                let text =
                    String.Concat(
                        [ for node in dom.Descendants fragment do
                              match dom.Text node with
                              | ValueSome text -> text
                              | ValueNone -> () ]
                    )
                let out = dom.NewFragment()
                if text <> "" then dom.Append(out, dom.CreateText text)
                Ok(dom.ToHtml out)

    /// `sanitize` (Rails::HTML5::SafeListSanitizer with its default allowlist).
    let private sanitizeHtml (html: string) : Result<string, UnfurlError> =
        match Sanitizer.sanitize html SafeList.defaults with
        | Ok html -> Ok html
        | Error _ -> Error(Raised "ArgumentError")

    /// `strip_tags` then `sanitize`, as `before_validation` runs them.
    let stripAndSanitize (html: string) : Result<string, UnfurlError> = stripTags html |> Result.bind sanitizeHtml

    /// `valid?`: sanitizes the title and description first (`before_validation`), then checks presence and, when
    /// there's an image, that it's a valid location.
    let validate (net: Network) (metadata: Metadata) (cancellation: CancellationToken) (logger: ILogger) : Task<Result<Metadata * bool, UnfurlError>> =
        task {
            let sanitizedKeys = [ "title"; "description" ]
            let values = sanitizedKeys |> List.map (get metadata)
            let! sanitized =
                offTheRuntime (fun () ->
                    values
                    |> List.map (fun value ->
                        match value with
                        | Some v -> stripAndSanitize v |> Result.map Some
                        | None -> Ok None))
            let mutable attributes = metadata.Attributes
            let mutable failure: UnfurlError option = None
            for (key, value) in List.zip sanitizedKeys sanitized do
                match value with
                | Ok value -> if failure.IsNone then attributes <- assign attributes key value
                | Error error -> if failure.IsNone then failure <- Some error
            match failure with
            | Some error -> return Error error
            | None ->
                let metadata = { Attributes = attributes }
                let present key = get metadata key |> Option.exists (fun v -> not (OpengraphDocument.isBlank v))
                let mutable valid = present "title" && present "url" && present "description"
                match get metadata "image" |> Option.filter (fun i -> not (OpengraphDocument.isBlank i)) with
                | Some image ->
                    let! imageValid = Location(net, Some image, cancellation, logger).IsValid()
                    valid <- valid && imageValid
                | None -> ()
                return Ok(metadata, valid)
        }

    /// `render json: opengraph` after `valid?`: `instance_values`, which by then include the validation context and
    /// the (empty) errors.
    let toJson (metadata: Metadata) : string =
        let json = Text.StringBuilder("{")
        for (key, value) in metadata.Attributes do
            let encoded =
                match value with
                | Some value -> Json.encode (Value.String value)
                | None -> Json.encode Value.Null
            json.Append(Json.encode (Value.String key)).Append(':').Append(encoded).Append(',') |> ignore
        json.Append("\"context_for_validation\":{\"context\":null},\"errors\":{}}") |> ignore
        json.ToString()
