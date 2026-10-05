// Port of rust/crates/kit/src/exceptions.rs
//
// `ActionDispatch::PublicExceptions`: what an error looks like on the wire in production.
//
// JSON, XML and YAML requests get the `{ status:, error: }` hash in that format (the formats the
// hash responds to `to_<format>` for); everything else gets `public/<status>.html`, or an empty
// body with the status when that file doesn't exist. HEAD gets an empty body in the request's
// format, `*/*` included.
//
// Unlike rendered responses (`charset=utf-8`), these say `charset=UTF-8`: PublicExceptions and
// `ShowExceptions#pass_response` interpolate `ActionDispatch::Response.default_charset`, which
// the railtie sets from `config.encoding` (verified against the reference).
namespace Campfire.Kit

open System
open System.Collections.Generic
open System.Text

/// The `public/<status>.html` pages, held in memory.
[<Sealed>]
type ErrorPages(pages: IEnumerable<KeyValuePair<int, ReadOnlyMemory<byte>>>) =
    let pages = Dictionary<int, ReadOnlyMemory<byte>>(pages)

    static member Empty = ErrorPages(Seq.empty)

    static member Of(pages: (int * ReadOnlyMemory<byte>) seq) : ErrorPages =
        ErrorPages(pages |> Seq.map (fun (status, page) -> KeyValuePair(status, page)))

    member _.Get(status: int) : ReadOnlyMemory<byte> voption =
        match pages.TryGetValue status with
        | true, page -> ValueSome page
        | _ -> ValueNone

module Exceptions =
    [<Literal>]
    let private Charset = "charset=UTF-8"

    /// `render_format` and `pass_response` set `Content-Length` themselves, so `Rack::Deflater`
    /// leaves the empty ones alone (`should_deflate?` skips `Content-Length: 0`).
    let private withLength (status: int) (contentType: string) (body: ReadOnlyMemory<byte>) : Response =
        Response
            .WithBody(status, $"{contentType}; {Charset}", body)
            .Header(Hdr.ContentLength, string body.Length)

    /// Builder's text escaping (`Hash#to_xml`).
    let private xmlEscape (text: string) : string = text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")

    /// `Rack::Utils::HTTP_STATUS_CODES`.
    let private reason (status: int) : string =
        match status with
        | 422 -> "Unprocessable Content"
        | 413 -> "Content Too Large"
        | _ ->
            match Status.canonicalReason status with
            | null -> "Internal Server Error"
            | reason -> reason

    let render (pages: ErrorPages) (status: int) (format: Format voption) (head: bool) : Response =
        // `request.formats.first`, `*/*` included (only `formats` failing falls back to HTML).
        let contentType =
            match format with
            | ValueSome f -> f.String
            | ValueNone -> "text/html"
        if head then
            withLength status contentType ReadOnlyMemory.Empty
        else
            let error = reason status
            // `body.public_send("to_#{format}")` when the `{ status:, error: }` hash responds to it.
            let body =
                match format with
                | ValueSome f when f.Is "json" -> ValueSome $"""{{"status":{status},"error":"{error}"}}"""
                | ValueSome f when f.Is "xml" ->
                    ValueSome(
                        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<hash>\n  <status type=\"integer\">{status}</status>\n  <error>{xmlEscape error}</error>\n</hash>\n"
                    )
                | ValueSome f when f.Is "yaml" -> ValueSome $"---\n:status: {status}\n:error: {error}\n"
                | _ -> ValueNone
            match body with
            | ValueSome body -> withLength status contentType (ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes body))
            | ValueNone ->
                match pages.Get status with
                | ValueSome html -> withLength status "text/html" html
                // PublicExceptions answers X-Cascade: pass; ShowExceptions#pass_response turns that into
                // an empty page with the error's status.
                | ValueNone -> withLength status "text/html" ReadOnlyMemory.Empty
