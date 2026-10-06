// Port of rust/crates/campfire/src/controllers/pwa.rs
//
// `PwaController` (reference/app/controllers/pwa_controller.rb): the web app manifest and the
// service worker, at stable URLs.
namespace Campfire.App.Controllers

open System
open System.Threading.Tasks
open Campfire.Views
open Campfire.Views.Templates.Pwa
open Campfire.Assets
open Campfire.Kit
open Campfire.App
open Campfire.App.Presenters
open Campfire.Db

module Pwa =
    /// `allow_unauthenticated_access`, `skip_forgery_protection`
    let private before: Before = Before.skipForgeryProtection (Before.allowUnauthenticatedAccess Before.Default)

    /// `pwa/service_worker.js`
    let serviceWorker (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c before
            do! c.RespondTo [ Format.Js ] |> Result.map ignore
            return c.RenderAs(Status.Ok, "text/javascript; charset=utf-8", ServiceWorker.js)
        }

    /// `pwa/manifest.json.erb`
    let manifest (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c before
            do! c.RespondTo [ Format.Json ] |> Result.map ignore
            let! account = c.App.Read(fun conn -> Account.first conn)
            let logoPathSmall = Accounts.freshAccountLogoPath account (Some "small")
            let logoPath = Accounts.freshAccountLogoPath account None
            let baseUrl = c.UrlFor ""
            let body =
                Render.plain (fun w ->
                    Manifest.render w (account |> Option.map (fun account -> account.Name)) logoPathSmall logoPath baseUrl Assets.assetPath)
            return c.RenderAs(Status.Ok, "application/json; charset=utf-8", ReadOnlyMemory<byte> body)
        }
