// Port of rust/crates/campfire/src/integrations/opengraph/location.rs
//
// `Opengraph::Location` (reference/app/models/opengraph/location.rb): a URL that is valid when it parses as http(s) and
// its host resolves to a public address, which is memoized and pinned for the fetch.
namespace Campfire.App.Integrations

open System.Net
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Campfire.RichText

module private LocationPatterns =
    /// `FILES_AND_MEDIA_URL_REGEX`
    let FilesAndMediaUrl =
        Regex(
            @"\bhttps?://\S+\.(?:zip|tar|tar\.gz|tar\.bz2|tar\.xz|gz|bz2|rar|7z|dmg|exe|msi|pkg|deb|iso|jpg|jpeg|png|gif|bmp|mp4|mov|avi|mkv|wmv|flv|heic|heif|mp3|wav|ogg|aac|wma|webm|ogv|mpg|mpeg)\b",
            RegexOptions.CultureInvariant ||| RegexOptions.NonBacktracking
        )

[<Sealed>]
type Location(net: Network, url: string option, cancellation: CancellationToken, logger: ILogger) =
    /// `parsed_url` is `URI.parse(url) rescue nil`.
    let parsedUrl =
        url
        |> Option.bind (fun url ->
            match RubyUri.parse url with
            | Ok uri -> Some uri
            | Error _ -> None)

    let mutable resolvedIp: IPAddress option option = None

    /// `resolved_ip`: `PrivateNetworkGuard.resolve(parsed_url.host) rescue nil`, memoized.
    member _.ResolvedIp() : Task<IPAddress option> =
        task {
            match resolvedIp with
            | Some ip -> return ip
            | None ->
                let! ip =
                    task {
                        match parsedUrl |> Option.bind (fun uri -> uri.Host) with
                        | Some host ->
                            match! Guard.resolve net.Resolver host with
                            | Ok ip -> return Some ip
                            | Error _ -> return None
                        | None -> return None
                    }
                resolvedIp <- Some ip
                return ip
        }

    /// `valid?`: both validations run, so the host is resolved even for a non-http URL.
    member this.IsValid() : Task<bool> =
        task {
            let http = parsedUrl |> Option.exists RubyUri.isHttp
            let! ip = this.ResolvedIp()
            return http && ip.IsSome
        }

    /// `read_html`: nothing for invalid URLs or ones that look like files and media.
    member this.ReadHtml() : Task<byte[] option> =
        task {
            let! valid = this.IsValid()
            if not valid || LocationPatterns.FilesAndMediaUrl.IsMatch(defaultArg url "") then
                return None
            else
                let! resolved = this.ResolvedIp()
                match parsedUrl, resolved with
                | Some uri, Some ip ->
                    match! OpengraphFetch.fetchDocument net uri ip cancellation with
                    | Ok html -> return html
                    | Error error ->
                        logger.LogWarning("Failed to fetch {Url} at {Ip} ({Error})", RubyUri.toS uri, ip, FetchError.message error)
                        return None
                | _ -> return None
        }

    /// `fetch_content_type`
    member this.FetchContentType() : Task<string option> =
        task {
            let! valid = this.IsValid()
            if not valid then
                return None
            else
                let! resolved = this.ResolvedIp()
                match parsedUrl, resolved with
                | Some uri, Some ip ->
                    match! OpengraphFetch.fetchContentType net uri ip cancellation with
                    | Ok contentType -> return contentType
                    | Error error ->
                        logger.LogWarning("Failed to fetch {Url} at {Ip} ({Error})", RubyUri.toS uri, ip, FetchError.message error)
                        return None
                | _ -> return None
        }
