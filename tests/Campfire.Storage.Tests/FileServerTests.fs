// rust/crates/storage/src/file_server.rs has no tests of its own (the app's integration tests
// exercise it through DiskController). These pin what its code says Rack::Files does.
module Campfire.Storage.Tests.FileServerTests

open System.IO
open Xunit
open Campfire.Storage
open Campfire.Storage.FileServer

let private get : Request = { Method = "GET"; Range = None; IfModifiedSince = None }

let private withFile (f: string -> unit) : unit =
    use file = TempFile.Create()
    File.WriteAllText(file.Path, "0123456789")
    f file.Path

let private header (name: string) (served: Served) : string option =
    served.Headers |> List.tryFind (fun (n, _) -> n = name) |> Option.map snd

let private serve (request: Request) (path: string) : Served =
    (serveFile request path None None).Value

[<Fact>]
let ``a whole file is served with the headers Rack gives it`` () =
    withFile (fun path ->
        let served = serve get path
        Assert.Equal(200, served.Status)
        Assert.Equal<(string * string) list>(
            [ "last-modified", (header "last-modified" served).Value
              "content-type", "application/octet-stream"
              "content-length", "10"
              "content-disposition", "attachment" ],
            served.Headers
        )
        Assert.Equal<BodyPart list>([ File(path, 0UL, 9UL) ], served.Body))

[<Fact>]
let ``the type and disposition are the ones given`` () =
    withFile (fun path ->
        let served = (serveFile get path (Some "image/png") (Some "inline; filename=\"a.png\"")).Value
        Assert.Equal(Some "image/png", header "content-type" served)
        Assert.Equal(Some "inline; filename=\"a.png\"", header "content-disposition" served))

[<Fact>]
let ``a head request has the length but no body`` () =
    withFile (fun path ->
        let served = serve { get with Method = "HEAD" } path
        Assert.Equal(200, served.Status)
        Assert.Equal(Some "10", header "content-length" served)
        Assert.Empty served.Body)

[<Fact>]
let ``one byte range is a partial response`` () =
    withFile (fun path ->
        let served = serve { get with Range = Some "bytes=2-5" } path
        Assert.Equal(206, served.Status)
        Assert.Equal(Some "bytes 2-5/10", header "content-range" served)
        Assert.Equal(Some "4", header "content-length" served)
        Assert.Equal<BodyPart list>([ File(path, 2UL, 5UL) ], served.Body))

[<Fact>]
let ``several byte ranges are a multipart response`` () =
    withFile (fun path ->
        let served = serve { get with Range = Some "bytes=0-1,4-5" } path
        Assert.Equal(206, served.Status)
        // `send_file_headers!` sets the content type after Rack::Files, multipart or not.
        Assert.Equal(Some "application/octet-stream", header "content-type" served)
        Assert.Equal(5, served.Body.Length)
        let total =
            served.Body
            |> List.sumBy (fun part ->
                match part with
                | Bytes bytes -> uint64 bytes.Length
                | File(_, start, stop) -> stop - start + 1UL)
        Assert.Equal(Some(string total), header "content-length" served)
        match served.Body[0] with
        | Bytes heading ->
            Assert.Equal(
                "\r\n--AaB03x\r\ncontent-type: text/plain\r\ncontent-range: bytes 0-1/10\r\n\r\n",
                System.Text.Encoding.UTF8.GetString heading
            )
        | other -> failwith $"{other}"
        Assert.Equal(
            "\r\n--AaB03x--\r\n",
            match List.last served.Body with
            | Bytes closing -> System.Text.Encoding.UTF8.GetString closing
            | other -> failwith $"{other}"
        ))

[<Fact>]
let ``a range past the end is a 416 without x-cascade`` () =
    withFile (fun path ->
        let served = serve { get with Range = Some "bytes=100-200" } path
        Assert.Equal(416, served.Status)
        Assert.Equal(Some "bytes */10", header "content-range" served)
        Assert.Equal(None, header "x-cascade" served)
        Assert.Equal<BodyPart list>([ Bytes(System.Text.Encoding.UTF8.GetBytes "Byte range unsatisfiable\n") ], served.Body))

[<Fact>]
let ``a file that hasn't changed is a 304`` () =
    withFile (fun path ->
        let lastModified = (header "last-modified" (serve get path)).Value
        let served = serve { get with IfModifiedSince = Some lastModified } path
        Assert.Equal(304, served.Status)
        Assert.Empty served.Body)

[<Fact>]
let ``options lists the methods`` () =
    withFile (fun path ->
        let served = serve { get with Method = "OPTIONS" } path
        Assert.Equal(200, served.Status)
        Assert.Equal(Some "GET, HEAD, OPTIONS", header "allow" served))

[<Fact>]
let ``a missing file is an io error`` () =
    use dir = TempDir.Create()
    match serveFile get (Path.Combine(dir.Path, "missing")) None None with
    | Error(StorageError.Io _) -> ()
    | other -> failwith $"{other}"
