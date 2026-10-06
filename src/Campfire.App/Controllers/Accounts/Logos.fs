// Port of rust/crates/campfire/src/controllers/accounts/logos.rs
//
// `Accounts::LogosController` (reference/app/controllers/accounts/logos_controller.rb): the
// account logo (or the stock app icon) as a PNG, public so it can be the PWA icon.
namespace Campfire.App.Controllers

open System.Threading.Tasks
open Campfire.Views
open Campfire.Kit
open Campfire.App
open Campfire.App.Presenters
open Campfire.Db
open Campfire.Routes
open Campfire.Storage

module Logos =
    /// `expires_in 5.minutes, public: true, stale_while_revalidate: 1.week`
    [<Literal>]
    let private MaxAge = 300UL

    [<Literal>]
    let private StaleWhileRevalidate = 604_800UL

    /// `allow_unauthenticated_access only: :show`
    let show (c: Ctx) : Task<Result<Response, Error>> =
        act {
            c.UseLiveResponse() // `include ActiveStorage::Streaming`
            do! Concerns.beforeActions c (Before.allowUnauthenticatedAccess Before.Default)
            let! (account: Account option) = c.App.Read(fun conn -> Account.first conn)

            // `stale?(etag: Current.account)`; there's no accounts/logos/show template to digest.
            let freshness =
                { Freshness.Default with
                    Etag =
                        match account with
                        | Some account -> FragmentCache.cacheKeyWithVersion "accounts" account.Id (account.UpdatedAt.ToDateTimeOffset())
                        | None -> null }
            match c.FreshWhen freshness with
            | ValueSome notModified -> return notModified
            | ValueNone ->
                c.ExpiresIn(MaxAge, { ExpiresIn.Default with Public = true; StaleWhileRevalidate = ValueSome StaleWhileRevalidate })

                let small = c.ParamStr "size" = "small"
                let! (variant: Campfire.Storage.Blob option) =
                    match account with
                    // `logo.variant(size).processed if logo.variable?`: :small is 192, :large 512, both PNG.
                    | Some account ->
                        let size = if small then 192L else 512L
                        AttachmentWrites.processedVariant c.App (Record.account account.Id) "logo" (Variation.resizeToLimit size size (Some "png"))
                    | None -> Task.FromResult(Ok None)
                match variant with
                | Some variant ->
                    let path = DiskService.pathFor c.App.Storage.Service variant.Key
                    return! c.SendFile(path, SendOptions.Inline "image/png")
                // send_stock_icon
                | None ->
                    let filename = if small then "app-icon-192.png" else "app-icon.png"
                    let! path = Avatars.assetFile $"logos/{filename}"
                    return! c.SendFile(path, SendOptions.Inline "image/png")
        }

    /// `Current.account.logo.destroy`
    let destroy (c: Ctx) : Task<Result<Response, Error>> =
        act {
            c.UseLiveResponse() // `include ActiveStorage::Streaming`
            do! Concerns.beforeActions c Before.Default
            do! Concerns.ensureCanAdminister c
            let! (account: Account) = AccountsController.currentAccount c
            let! (_: bool) = c.App.Write(fun tx -> AttachmentWrites.destroy tx (Record.account account.Id) "logo")
            return! c.RedirectTo(c.UrlFor(Routes.editAccount ()))
        }
