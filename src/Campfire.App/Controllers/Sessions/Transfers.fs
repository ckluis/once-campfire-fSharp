// Port of rust/crates/campfire/src/controllers/sessions/transfers.rs
//
// `Sessions::TransfersController` (reference/app/controllers/sessions/transfers_controller.rb):
// sign in on another device with a user's transfer link.
namespace Campfire.App.Controllers

open System.Threading.Tasks
open Campfire.Views
open Campfire.Kit
open Campfire.App
open Campfire.App.Presenters
open Campfire.Db

module SessionTransfers =
    module TransferPage = Campfire.Views.Templates.Sessions.Transfers.Show

    let private showSize = RenderSize()

    /// `allow_unauthenticated_access`: an auto-submitting form that PUTs back to this URL.
    let show (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c (Before.allowUnauthenticatedAccess Before.Default)
            do! c.RespondTo [ Format.Html ] |> Result.map ignore
            // `url_for({})`: this request's own path.
            let action = c.Request.Path
            return!
                Presenters.Page.framedPage
                    c
                    Status.Ok
                    showSize
                    (fun w ctx -> TransferPage.render w ctx action)
                    TransferPage.head
                    (fun w _ -> TransferPage.content w action)
        }

    let update (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c (Before.allowUnauthenticatedAccess Before.Default)
            let transferId = match c.ParamStr "id" with null -> "" | id -> id
            let userId = Accounts.userIdFromTransferId c.App.Secrets transferId (c.Now())
            // `User.active.find_by_transfer_id(params[:id])`
            let! user =
                match userId with
                | Some id -> c.App.Read(fun conn -> User.findById conn id |> Option.filter User.isActive)
                | None -> Task.FromResult(Ok None)
            match user with
            | Some user ->
                let! (_: Session) = Concerns.startNewSessionFor c user
                return! c.RedirectTo(Concerns.postAuthenticatingUrl c)
            | None -> return c.Head Status.BadRequest
        }
