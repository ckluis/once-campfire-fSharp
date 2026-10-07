// Port of rust/crates/campfire/src/controllers/rooms/directs.rs
//
// `Rooms::DirectsController` (reference/app/controllers/rooms/directs_controller.rb). `index`
// and `destroy` are RoomsController's (`destroy` with this controller's `set_room` and an
// `ensure_can_administer` that always passes); `show` is RoomsController's without `set_room`,
// so `remember_last_room_visited` raises on the nil `@room` (`show`).
namespace Campfire.App.Controllers

open System.Threading.Tasks
open Campfire.App
open Campfire.App.Presenters
open Campfire.Db
open Campfire.Kit
open Campfire.Ruby
open Campfire.Views
open Campfire.Views.Rooms

module Directs =
    module NewPage = Campfire.Views.Templates.Rooms.Directs.New
    module EditPage = Campfire.Views.Templates.Rooms.Directs.Edit

    let private newSize = RenderSize()
    let private editSize = RenderSize()

    /// `show`: the room page, which checks membership. Rails inherits RoomsController#show without
    /// setting `@room`, so `remember_last_room_visited` raises (a 500).
    let show (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            match (match c.ParamStr "id" with null -> None | id -> Ruby.integerCast id) with
            | None -> return! Error NotFound
            | Some id -> return! RoomsController.redirectToRoom c id
        }

    let ``new`` (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            return! Page.framedPage c Status.Ok newSize (fun w ctx -> NewPage.render w ctx) NewPage.head (fun w ctx -> NewPage.content w ctx)
        }

    /// `broadcast_create_room`: `users/sidebars/rooms/_direct` for each membership, to its user.
    let private broadcastCreateRoom (c: Ctx) (room: Room) : Task<Result<unit, Error>> =
        let app = c.App
        let baseUrl = Page.rendererBaseUrl c
        app.Read(fun conn ->
            let presenter = Presenter(conn, app, None)
            let account = Account.first conn
            let directRooms =
                [ for membership in Membership.forRoom conn room.Id do
                      let direct = presenter.SidebarDirect membership
                      let html = Page.renderDetachedAt app account baseUrl (fun ctx -> UsersCached.directRoom ctx direct)
                      membership.Id, html ]
            app.Broadcasts.DirectRoomCreate(conn, room, Rendered.partials { Rendered.empty with DirectRooms = directRooms }))

    let create (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            let! (user: User) = Concerns.requireCurrentUser c
            // selected_users: `User.where(id: selected_users_ids.including(Current.user.id))`
            let ids = RoomsController.userIdsParam c @ [ user.Id ]
            let! (room: Room) =
                c.App.Write(fun tx ->
                    let users = RoomsController.existingUserIds tx.Conn ids
                    Room.findOrCreateDirectFor tx users user.Id)
            do! broadcastCreateRoom c room
            return! RoomsController.redirectToRoom c room.Id
        }

    let edit (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            let! (room: Room) = RoomsController.setRoom c Directs
            let! (current: User) = Concerns.requireCurrentUser c
            let app = c.App
            let! edit =
                app.Read(fun conn ->
                    let presenter = Presenter(conn, app, None)
                    // `@room.users.many? ? @room.users.without(Current.user) : @room.users`
                    let users = Room.users conn room
                    let users = if users.Length > 1 then users |> List.filter (fun user -> user.Id <> current.Id) else users
                    ({ RoomId = room.Id
                       DisplayName = presenter.RoomDisplayName(room, Some current)
                       Users = users |> List.map (PresenterSupport.userView app.Secrets) }: DirectEditView))
            return!
                Page.framedPage
                    c
                    Status.Ok
                    editSize
                    (fun w ctx -> EditPage.render w ctx edit)
                    EditPage.head
                    (fun w ctx -> EditPage.content w ctx edit)
        }

    let destroy (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            let! (room: Room) = RoomsController.setRoom c Directs
            // ensure_can_administer: every member of a direct room can.
            return! RoomsController.destroyRoom c room
        }
