// Port of rust/crates/campfire/src/integrations/opengraph/fetch.rs
//
// `Opengraph::Fetch` (reference/app/models/opengraph/fetch.rb): GET or HEAD against a pinned address, following up to
// 10 responses (any 3xx is a redirect), each redirect target parsed, required to be http(s), and resolved through the
// private network guard again. A document must be a 200 `text/html` of at most 5MB, by `Content-Length` and by what's
// actually read.
namespace Campfire.App.Integrations

open System
open System.Net
open System.Threading
open System.Threading.Tasks
open Campfire.RichText

type FetchError =
    | TooManyRedirects
    | RedirectDenied
    /// `URI::InvalidURIError` (a missing or unparsable `Location`), or a URI Net::HTTP refuses.
    | InvalidUri
    | Guard of GuardError
    | Http of HttpError

module FetchError =
    let message (error: FetchError) : string =
        match error with
        | TooManyRedirects -> "Opengraph::Fetch::TooManyRedirectsError"
        | RedirectDenied -> "Opengraph::Fetch::RedirectDeniedError"
        | InvalidUri -> "bad URI"
        | Guard error -> GuardError.message error
        | Http error -> HttpError.message error

module OpengraphFetch =
    [<Literal>]
    let AllowedDocumentContentType = "text/html"

    [<Literal>]
    let MaxBodySize = 5242880

    [<Literal>]
    let MaxRedirects = 10

    /// Each connect and each read; Rails leaves `Net::HTTP`'s 60 seconds. The unfurl as a whole has
    /// `UnfurlDeadline`.
    let private timeouts: Timeouts = { Open = TimeSpan.FromSeconds 5.0; Read = TimeSpan.FromSeconds 5.0 }

    /// `Net::HTTPGenericRequest#initialize`: `uri.hostname`, plus the port unless it's the scheme's default.
    let private hostHeader (host: string) (port: int) (https: bool) : string =
        let hostname = if host.StartsWith '[' && host.EndsWith ']' then host.Substring(1, host.Length - 2) else host
        let defaultPort = if https then 443 else 80
        if port = defaultPort then hostname else $"{hostname}:{port}"

    /// `Net::HTTP.start(url.host, url.port, ipaddr: ip, use_ssl: url.scheme == "https")` and
    /// `http.request(request_class.new(url))`.
    let private send (net: Network) (url: RubyUri) (ip: IPAddress) (meth: string) (cancellation: CancellationToken) : Task<Result<HttpResponse, FetchError>> =
        task {
            match url.Host |> Option.filter (fun h -> h <> ""), url.Port |> Option.filter (fun p -> p <= 65535UL) with
            | Some host, Some port ->
                let https = url.Scheme |> Option.exists (fun s -> s.Equals("https", StringComparison.OrdinalIgnoreCase))
                let endpoint: Endpoint =
                    { Https = https
                      Host = host
                      Port = int port
                      PinnedIp = Some ip }
                let request =
                    Request.netHttp meth (Http.requestUri url) (Some(hostHeader host (int port) https)) []
                    |> Request.transport false endpoint
                match! Http.exchange net endpoint request timeouts cancellation with
                | Ok response -> return Ok response
                | Error error -> return Error(Http error)
            | _ -> return Error InvalidUri
        }

    let private resolveRedirect (net: Network) (location: string option) : Task<Result<RubyUri * IPAddress, FetchError>> =
        task {
            match location |> Option.map RubyUri.parse with
            | None
            | Some(Error _) -> return Error InvalidUri
            | Some(Ok url) ->
                if not (RubyUri.isHttp url) then
                    return Error RedirectDenied
                else
                    match! Guard.resolve net.Resolver (defaultArg url.Host "") with
                    | Ok ip -> return Ok(url, ip)
                    | Error error -> return Error(Guard error)
        }

    let private request (net: Network) (url: RubyUri) (ip: IPAddress) (meth: string) (cancellation: CancellationToken) : Task<Result<HttpResponse, FetchError>> =
        task {
            let mutable url = url
            let mutable ip = ip
            let mutable result: Result<HttpResponse, FetchError> option = None
            let mutable attempt = 0
            while result.IsNone && attempt < MaxRedirects do
                match! send net url ip meth cancellation with
                | Error error -> result <- Some(Error error)
                | Ok response ->
                    if response.Status >= 300 && response.Status < 400 then
                        let location = response.Header "location"
                        (response :> IDisposable).Dispose()
                        match! resolveRedirect net location with
                        | Ok(next, nextIp) ->
                            url <- next
                            ip <- nextIp
                        | Error error -> result <- Some(Error error)
                    else
                        result <- Some(Ok response)
                attempt <- attempt + 1
            return defaultArg result (Error TooManyRedirects)
        }

    /// `fetch_document(url, ip:)`: the body, or `None` when the response isn't acceptable.
    let fetchDocument (net: Network) (url: RubyUri) (ip: IPAddress) (cancellation: CancellationToken) : Task<Result<byte[] option, FetchError>> =
        task {
            match! request net url ip "GET" cancellation with
            | Error error -> return Error error
            | Ok response ->
                use response = response
                if response.Status <> 200 || response.ContentType() <> Some AllowedDocumentContentType then
                    return Ok None
                else
                    match response.ContentLength() with
                    | Error error -> return Error(Http error)
                    | Ok length when (defaultArg length 0UL) > uint64 MaxBodySize -> return Ok None
                    | Ok _ ->
                        match! response.ReadBody MaxBodySize with
                        | Ok(Complete body) -> return Ok(Some body)
                        | Ok TooLarge -> return Ok None
                        | Error error -> return Error(Http error)
        }

    /// `fetch_content_type(url, ip:)`: the final response's `Content-Type`, whatever its status.
    let fetchContentType (net: Network) (url: RubyUri) (ip: IPAddress) (cancellation: CancellationToken) : Task<Result<string option, FetchError>> =
        task {
            match! request net url ip "HEAD" cancellation with
            | Error error -> return Error error
            | Ok response ->
                use response = response
                return Ok(response.Header "content-type")
        }
