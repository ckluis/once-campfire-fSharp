// Port of rust/crates/campfire/src/controllers/rooms/involvements.rs
//
// `Rooms::InvolvementsController` (reference/app/controllers/rooms/involvements_controller.rb).
namespace Campfire.App.Controllers

open System.Threading.Tasks
open Campfire.App
open Campfire.App.Presenters
open Campfire.Db
open Campfire.Kit
open Campfire.Routes
open Campfire.Views
open Campfire.Views.Rooms

module Involvements =
    module ShowPage = Campfire.Views.Templates.Rooms.Involvements.Show

    /// `params[:involvement]` as the enum casts it: a blank value (missing, "", "  ", `[]`) is stored
    /// as nil, anything that isn't one of the values raises ArgumentError ('... is not a valid
    /// involvement'). Verified against the reference with `update!(involvement: "")`.
    let private involvementParam (c: Ctx) : Result<Involvement option, Error> =
        match c.Param "involvement" with
        | ValueNone -> Ok None
        | ValueSome param when param.IsBlank -> Ok None
        | ValueSome param ->
            match param with
            | Param.Str value ->
                match Involvement.fromName value with
                | Some involvement -> Ok(Some involvement)
                | None -> Error(Internal(exn $"\"{value}\" is not a valid involvement"))
            | other -> Error(Internal(exn $"{other.ToS()} is not a valid involvement"))

    let show (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            let! (membership: Membership), (room: Room) = Concerns.setRoom c
            let involvement: InvolvementView =
                { RoomId = room.Id
                  Kind = PresenterSupport.roomKind room.RoomType
                  Involvement = membership.Involvement |> Option.map Involvement.name |> Option.defaultValue "" }
            return! Page.content c Status.Ok (fun w ctx -> ShowPage.render w ctx involvement)
        }

    let update (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            let! (membership: Membership), (room: Room) = Concerns.setRoom c
            let! involvement = involvementParam c
            let previous = membership.Involvement
            let! (membership: Membership) = c.App.Write(fun tx -> Membership.updateInvolvement tx membership involvement)

            // broadcast_visibility_changes
            let! partials = RoomsController.renderSharedRoom c room
            do!
                c.App.Broadcasts.InvolvementChange(room, membership, previous, Rendered.partials partials)
                |> Result.mapError (fun nilInquiry -> Internal(exn (string nilInquiry)))

            return! c.RedirectTo(c.UrlFor(Routes.roomInvolvement room.Id))
        }
