// Port of the #[cfg(test)] module in rust/crates/kit/src/body.rs
module Campfire.Kit.Tests.BodyTests

open System
open System.IO
open System.Text
open Microsoft.AspNetCore.Http
open Xunit
open Campfire.RailsCompat
open Campfire.Kit
open Campfire.Kit.Tests.Helpers

let private nameOf (part: Part) = match part.Name with null -> None | n -> Some n
let private filenameOf (part: Part) = match part.Filename with null -> None | n -> Some n

[<Fact>]
let ``dispositions`` () =
    let part = RequestBody.parseDisposition """form-data; name="message[attachment]"; filename="C:\Users\me\cat.png" """
    Assert.Equal(Some "message[attachment]", nameOf part)
    Assert.Equal(Some "cat.png", filenameOf part)

    let part = RequestBody.parseDisposition """form-data; name="a"; filename="with \"quotes\".txt" """
    Assert.Equal(Some "with \"quotes\".txt", filenameOf part)

    let part = RequestBody.parseDisposition "form-data; name=file; filename*=UTF-8''r%C3%A9sum%C3%A9.pdf"
    Assert.Equal(Some "file", nameOf part)
    Assert.Equal(Some "résumé.pdf", filenameOf part)

    let part = RequestBody.parseDisposition """form-data; name="a"; filename="100%.txt" """
    Assert.Equal(Some "100%.txt", filenameOf part)

let private multipartBody (boundary: string) (parts: (string * string) list) : byte[] =
    let body = StringBuilder()
    for (disposition, content) in parts do
        body.Append($"--{boundary}\r\n{disposition}\r\n\r\n{content}\r\n") |> ignore
    body.Append($"--{boundary}--\r\n") |> ignore
    Encoding.UTF8.GetBytes(body.ToString())

let private headersOf (contentType: string) : IHeaderDictionary =
    let headers = HeaderDictionary()
    headers["content-type"] <- contentType
    headers

let private parse (meth: string) (headers: IHeaderDictionary) (body: byte[]) (limit: int voption) =
    use stream = new MemoryStream(body)
    (RequestBody.parse meth headers (ValueSome(int64 body.Length)) stream limit).GetAwaiter().GetResult()

let private paramsOf (parsed: Result<ParsedBody, BodyError>) : ParamMap =
    match parsed with
    | Ok parsed ->
        match parsed.Params with
        | Ok pars -> pars
        | Error e -> failwith $"{e}"
    | Error e -> failwith $"{e}"

[<Fact>]
let ``multipart fields and files`` () =
    let boundary = "XyZ"
    let body =
        multipartBody
            boundary
            [ "Content-Disposition: form-data; name=\"_method\"", "patch"
              "Content-Disposition: form-data; name=\"user[name]\"", "Jo"
              "Content-Disposition: form-data; name=\"user[avatar]\"; filename=\"me.png\"\r\nContent-Type: image/png", "PNGDATA"
              "Content-Disposition: form-data; name=\"user[empty]\"; filename=\"\"", ""
              "Content-Disposition: form-data; name=\"tags[]\"", "a"
              "Content-Disposition: form-data; name=\"tags[]\"", "b" ]
    let result = parse "POST" (headersOf $"multipart/form-data; boundary={boundary}") body ValueNone
    let files = match result with Ok p -> p.Files | Error e -> failwith $"{e}"
    try
        let pars = paramsOf result
        Assert.Equal("patch", pars.Str "_method")
        let user = (pars.Get "user").Value
        Assert.Equal("Jo", (user.Get "name").Value.AsStr)
        Assert.True((user.Get "empty").IsNone)
        let avatar = nonNull (user.Get "avatar").Value.AsFile
        Assert.Equal("me.png", avatar.OriginalFilename)
        Assert.Equal("image/png", avatar.ContentType)
        Assert.Equal(7L, avatar.Size)
        Assert.Equal<byte[]>(Encoding.UTF8.GetBytes "PNGDATA", avatar.Read())
        Assert.Equal(json """["a", "b"]""", ((pars.ToJson()).TryGet "tags").Value)
    finally
        for file in files do
            file.Delete()

[<Fact>]
let ``multipart size limit`` () =
    let body = multipartBody "B" [ "Content-Disposition: form-data; name=\"f\"; filename=\"x\"", String('x', 1000) ]
    match parse "POST" (headersOf "multipart/form-data; boundary=B") body (ValueSome 100) with
    | Error TooLarge -> ()
    | other -> failwith $"{other}"

[<Fact>]
let ``multipart text fields are capped together`` () =
    let half = String('x', RequestBody.MultipartTextLimit / 2)
    let headers = headersOf "multipart/form-data; boundary=B"
    let field = "Content-Disposition: form-data; name=\"a[]\""
    let file = "Content-Disposition: form-data; name=\"f\"; filename=\"x\""
    let fits = multipartBody "B" [ field, half; field, half; file, half ]
    let parsed = parse "POST" headers fits ValueNone
    match parsed with
    | Ok p ->
        Assert.True p.Params.IsOk
        for f in p.Files do
            f.Delete()
    | Error e -> failwith $"{e}"
    let over = multipartBody "B" [ field, half; field, half; field, "x" ]
    match parse "POST" headers over ValueNone with
    | Error TooLarge -> ()
    | other -> failwith $"{other}"

[<Fact>]
let ``multipart parts and files are limited`` () =
    let headers = headersOf "multipart/form-data; boundary=B"
    let tooManyParts =
        multipartBody "B" [ for _ in 0 .. RequestBody.MultipartPartLimit -> "Content-Disposition: form-data; name=\"a[]\"", "x" ]
    match parse "POST" headers tooManyParts ValueNone with
    | Ok { Params = Error(ParamError.Limit _) } -> ()
    | other -> failwith $"{other}"
    let tooManyFiles =
        multipartBody
            "B"
            [ for n in 0 .. RequestBody.MultipartFileLimit -> $"Content-Disposition: form-data; name=\"f{n}\"; filename=\"x{n}\"", "x" ]
    match parse "POST" headers tooManyFiles ValueNone with
    | Ok { Params = Error(ParamError.Limit _) } -> ()
    | other -> failwith $"{other}"

[<Fact>]
let ``malformed multipart is a parse error`` () =
    let garbage = Encoding.UTF8.GetBytes "--B\r\nContent-Disposition: form-data; name=\"a\"\r\n\r\nno end"
    match parse "POST" (headersOf "multipart/form-data; boundary=B") garbage ValueNone with
    | Ok { Params = Error ParamError.Parse } -> ()
    | other -> failwith $"{other}"

[<Fact>]
let ``forms json and raw bodies`` () =
    let headers = HeaderDictionary()
    headers["content-type"] <- "application/x-www-form-urlencoded"
    let body = Encoding.UTF8.GetBytes "a[b]=1&c=2"
    let parsed = parse "POST" headers body ValueNone
    Assert.Equal(json """{"a": {"b": "1"}, "c": "2"}""", (paramsOf parsed).ToJson())
    Assert.Equal<byte[]>(body, (match parsed with Ok p -> p.Raw.ToArray() | _ -> [||]))

    headers["content-type"] <- "application/json"
    let parsed = parse "POST" headers (Encoding.UTF8.GetBytes """{"url":"x"}""") ValueNone
    Assert.Equal(json """{"url": "x"}""", (paramsOf parsed).ToJson())

    headers["content-type"] <- "text/plain"
    let parsed = parse "POST" headers (Encoding.UTF8.GetBytes "Hello!") ValueNone
    Assert.True((paramsOf parsed).IsEmpty)
    Assert.Equal<byte[]>(Encoding.UTF8.GetBytes "Hello!", (match parsed with Ok p -> p.Raw.ToArray() | _ -> [||]))

    // A POST without a content type is parsed as a form (Rack's form_data?).
    let parsed = parse "POST" (HeaderDictionary()) (Encoding.UTF8.GetBytes "Hello!") ValueNone
    Assert.Equal(json """{"Hello!": null}""", (paramsOf parsed).ToJson())
    let parsed = parse "PUT" (HeaderDictionary()) (Encoding.UTF8.GetBytes "Hello!") ValueNone
    Assert.True((paramsOf parsed).IsEmpty)

    match parse "POST" (HeaderDictionary()) (Encoding.UTF8.GetBytes(String('x', 20))) (ValueSome 10) with
    | Error TooLarge -> ()
    | other -> failwith $"{other}"

[<Fact>]
let ``a body without a known length is read up to the limit`` () =
    let headers = HeaderDictionary()
    headers["content-type"] <- "application/x-www-form-urlencoded"
    let read (body: byte[]) (limit: int voption) =
        use stream = new MemoryStream(body)
        (RequestBody.parse "POST" headers ValueNone stream limit).GetAwaiter().GetResult()
    Assert.Equal(json """{"a": "1"}""", (paramsOf (read (Encoding.UTF8.GetBytes "a=1") ValueNone)).ToJson())
    match read (Encoding.UTF8.GetBytes(String('x', 20))) (ValueSome 10) with
    | Error TooLarge -> ()
    | other -> failwith $"{other}"

[<Fact>]
let ``boundaries are read as multer reads them`` () =
    let boundary (ct: string) = Multipart.parseBoundary ct
    Assert.Equal(ValueSome "----campfire", boundary "multipart/form-data; boundary=----campfire")
    Assert.Equal(ValueSome "a b", boundary "multipart/form-data; charset=utf-8; boundary=\"a b\"")
    Assert.Equal(ValueSome "x", boundary "Multipart/Form-Data;BOUNDARY=x")
    Assert.Equal(ValueNone, boundary "multipart/form-data")
    Assert.Equal(ValueNone, boundary "multipart/mixed; boundary=x")
    Assert.Equal(ValueNone, boundary "application/x-www-form-urlencoded; boundary=x")
