// Port of rust/crates/storage/src/file_server.rs
/// `ActiveStorage::FileServer#serve_file` on top of `Rack::Files#serving` (rack 3.2): conditional
/// GET on mtime, single and multipart byte ranges, and 416s. HTTP-framework agnostic: the result
/// describes the response and the caller streams the body parts.
module Campfire.Storage.FileServer

open System
open System.Globalization
open System.IO
open Campfire.Ruby

[<Literal>]
let MultipartBoundary = "AaB03x"

type BodyPart =
    | Bytes of byte[]
    /// Inclusive byte range of `path`.
    | File of path: string * start: uint64 * stop: uint64

type Served =
    { Status: int
      /// In Rack's order; `content-type` and `content-disposition` are set last by FileServer.
      Headers: (string * string) list
      Body: BodyPart list }

type Request =
    { Method: string
      Range: string option
      IfModifiedSince: string option }

let private setHeader (headers: (string * string) list) (name: string) (value: string) : (string * string) list =
    if headers |> List.exists (fun (n, _) -> String.Equals(n, name, StringComparison.OrdinalIgnoreCase)) then
        headers |> List.map (fun (n, v) -> if String.Equals(n, name, StringComparison.OrdinalIgnoreCase) then (n, value) else (n, v))
    else
        headers @ [ name, value ]

/// `Time#httpdate`: `Sun, 06 Nov 1994 08:49:37 GMT`.
let private httpdate (time: DateTime) : string =
    time.ToUniversalTime().ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", CultureInfo.InvariantCulture)

let private finish (request: Request) (size: uint64) (headers: (string * string) list) (status: int) (body: BodyPart list) (length: uint64) : Served =
    { Status = status
      Headers = headers @ [ "content-length", string length ]
      Body = if request.Method = "HEAD" || size = 0UL then [] else body }

let private serving (request: Request) (path: string) : StorageResult<Served> =
    if request.Method = "OPTIONS" then
        Ok
            { Status = 200
              Headers = [ "allow", "GET, HEAD, OPTIONS"; "content-length", "0" ]
              Body = [] }
    else
        match Io.attempt (fun () -> FileInfo(path)) with
        | Error e -> Error e
        | Ok info when not info.Exists -> Error(StorageError.Io(FileNotFoundException($"No such file or directory: {path}")))
        | Ok info ->
            let lastModified = httpdate info.LastWriteTimeUtc
            if request.IfModifiedSince = Some lastModified then
                Ok { Status = 304; Headers = []; Body = [] }
            else
                // Disk keys have no extension, so Rack's mime lookup falls back to its default.
                let mimeType = "text/plain"
                let headers = [ "last-modified", lastModified; "content-type", mimeType ]
                let size = uint64 info.Length
                match Rack.byteRanges request.Range size with
                | None ->
                    let body = [ File(path, 0UL, (if size = 0UL then 0UL else size - 1UL)) ]
                    Ok(finish request size headers 200 body size)
                | Some [] ->
                    let body = "Byte range unsatisfiable\n"
                    Ok
                        { Status = 416
                          Headers =
                            [ "content-type", "text/plain"
                              "content-length", string body.Length
                              "x-cascade", "pass"
                              "content-range", $"bytes */{size}" ]
                          Body = [ Bytes(Text.Encoding.UTF8.GetBytes body) ] }
                | Some ranges ->
                    let parts, headers =
                        match ranges with
                        | [ (start, stop) ] -> [ File(path, start, stop) ], headers @ [ "content-range", $"bytes {start}-{stop}/{size}" ]
                        | _ ->
                            let headers = setHeader headers "content-type" $"multipart/byteranges; boundary={MultipartBoundary}"
                            let parts =
                                [ for start, stop in ranges do
                                      let heading =
                                          $"\r\n--{MultipartBoundary}\r\ncontent-type: {mimeType}\r\ncontent-range: bytes {start}-{stop}/{size}\r\n\r\n"
                                      Bytes(Text.Encoding.UTF8.GetBytes heading)
                                      File(path, start, stop)
                                  Bytes(Text.Encoding.UTF8.GetBytes $"\r\n--{MultipartBoundary}--\r\n") ]
                            parts, headers
                    let length =
                        parts
                        |> List.sumBy (fun part ->
                            match part with
                            | Bytes bytes -> uint64 bytes.Length
                            | File(_, start, stop) -> stop - start + 1UL)
                    Ok(finish request size headers 206 parts length)

/// `serve_file(path, content_type:, disposition:)` as `DiskController#show` calls it.
let serveFile (request: Request) (path: string) (contentType: string option) (disposition: string option) : StorageResult<Served> =
    match serving request path with
    | Error e -> Error e
    | Ok served ->
        let headers =
            if served.Status = 416 then
                served.Headers |> List.filter (fun (name, _) -> not (String.Equals(name, "x-cascade", StringComparison.OrdinalIgnoreCase)))
            else
                served.Headers
        let headers = setHeader headers "content-type" (defaultArg contentType "application/octet-stream")
        let headers = setHeader headers "content-disposition" (defaultArg disposition "attachment")
        Ok { served with Headers = headers }
