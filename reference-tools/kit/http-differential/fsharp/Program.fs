/// Serves the app of Campfire.Kit.Tests.HttpApp (the Rust kit's http.rs app) on a port:
/// `HttpApp PORT [ssl]`.
module Campfire.Kit.HttpDifferential.Program

open System
open System.Net
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Logging
open Campfire.Kit
open Campfire.Kit.Tests

[<EntryPoint>]
let main argv =
    let port = if argv.Length > 0 then int argv[0] else 4002
    let config = if argv.Length > 1 && argv[1] = "ssl" then HttpApp.sslConfig else HttpApp.plainConfig
    let kit = HttpApp.kitFor config
    let builder = WebApplication.CreateBuilder()
    builder.WebHost.ConfigureKestrel(fun options ->
        Adapter.configureKestrel options
        options.Listen(IPAddress.Loopback, port))
    |> ignore
    builder.Logging.ClearProviders() |> ignore
    let app = builder.Build()
    Adapter.app kit (HttpApp.routes kit) app
    app.Run()
    0
