// Port of `Server::call` in rust/crates/cable/src/server.rs
//
// The `/cable` endpoint (`ActionCable::Server::Base#call`). Anything that isn't a WebSocket upgrade
// from an allowed origin gets Rails' 404 "Page not found".
module Campfire.Cable.Endpoint

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Extensions
open Microsoft.AspNetCore.Http.Features
open Microsoft.AspNetCore.Routing
open Microsoft.Extensions.Logging
open Campfire.Cable.Socket

/// Every value of a header, one per header line.
let private values (headers: IHeaderDictionary) (name: string) : string list =
    [ for value in headers[name] do
          match value with
          | null -> ()
          | value -> value ]

/// `WebSocket::Driver.websocket?(env)`: a GET with `Connection: upgrade` and `Upgrade: websocket`.
let private websocketRequest (meth: string) (headers: IHeaderDictionary) : bool =
    let connectionUpgrade =
        values headers "Connection"
        |> List.exists (fun value -> value.Split(',') |> Array.exists (fun token -> token.Trim().Equals("upgrade", StringComparison.OrdinalIgnoreCase)))
    let upgradeWebsocket =
        match values headers "Upgrade" with
        | first :: _ -> first.Equals("websocket", StringComparison.OrdinalIgnoreCase)
        | [] -> false
    HttpMethods.IsGet meth && connectionUpgrade && upgradeWebsocket

/// The first protocol in the client's list that Action Cable supports.
let internal negotiateProtocol (headers: IHeaderDictionary) : string option =
    values headers "Sec-WebSocket-Protocol"
    |> Seq.collect (fun value -> value.Split(',') :> seq<string>)
    |> Seq.map (fun requested -> requested.Trim())
    |> Seq.tryPick (fun requested -> Protocol.Protocols |> Array.tryFind (fun supported -> supported = requested))

/// `Connection::Base#respond_to_invalid_request`.
let private pageNotFound (ctx: HttpContext) : Task =
    let body = "Page not found"
    ctx.Response.StatusCode <- StatusCodes.Status404NotFound
    ctx.Response.ContentType <- "text/plain; charset=utf-8"
    ctx.Response.ContentLength <- int64 body.Length
    ctx.Response.WriteAsync body

/// Serves one request to `/cable`: upgrades a WebSocket handshake and runs the connection until the
/// socket closes; anything else is a 404.
let call (server: Server<'U>) (ctx: HttpContext) : Task =
    task {
        server.StartHeartbeat()
        let request = ctx.Request
        let headers = request.Headers
        let upgrade = ctx.Features.Get<IHttpUpgradeFeature>()
        let handshake =
            if websocketRequest request.Method headers && server.AllowRequestOrigin headers then
                Handshake.accept headers
            else
                None
        match handshake, upgrade with
        | Some handshake, upgrade when not (isNull upgrade) && (nonNull upgrade).IsUpgradableRequest ->
            let upgrade = nonNull upgrade
            Handshake.responseHeaders handshake ctx.Response.Headers
            match negotiateProtocol headers with
            | Some protocol -> ctx.Response.Headers["Sec-WebSocket-Protocol"] <- protocol
            | None -> ()
            let connect = { Uri = request.GetEncodedPathAndQuery(); Headers = headers }
            let! stream = upgrade.UpgradeAsync()
            try
                do! Connection.run server stream handshake.Deflate connect ctx.Abort
            with e ->
                server.Logger.LogError(e, "Cable connection failed")
        | _ -> do! pageNotFound ctx
    }

/// Mounts the endpoint at `path` (normally `Protocol.DefaultMountPath`) for any method.
let map (path: string) (server: Server<'U>) (endpoints: IEndpointRouteBuilder) : unit =
    endpoints.Map(path, RequestDelegate(fun ctx -> call server ctx)) |> ignore
