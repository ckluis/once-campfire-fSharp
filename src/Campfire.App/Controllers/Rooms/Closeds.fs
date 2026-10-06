// Port of rust/crates/campfire/src/controllers/rooms/closeds.rs
//
// `Rooms::ClosedsController` (reference/app/controllers/rooms/closeds_controller.rb). `index` is
// RoomsController's; `destroy` is RoomsController's without `set_room`
// (`RoomsController.destroyWithoutRoom`).
namespace Campfire.App.Controllers

open System.Threading.Tasks
open Campfire.App
open Campfire.App.Presenters
open Campfire.Db
open Campfire.Kit
open Campfire.Views
open Campfire.Views.Rooms

module Closeds =
    module NewPage = Campfire.Views.Templates.Rooms.Closeds.New
    module EditPage = Campfire.Views.Templates.Rooms.Closeds.Edit

    let private newSize = RenderSize()
    let private editSize = RenderSize()

    let show (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            let! (room: Room) = RoomsController.setRoom c WithoutDirects
            Concerns.rememberLastRoomVisited c room.Id
            return! RoomsController.redirectToRoom c room.Id
        }

    let ``new`` (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            do! RoomsController.ensurePermissionToCreateRooms c
            let! (user: User) = Concerns.requireCurrentUser c
            // `@users = User.active.ordered`; the form shows them all as unselected.
            let! users = Opens.activeUsers c
            let form: ClosedFormView =
                { Room = { Id = None; Name = Some Opens.DefaultRoomName }
                  CanAdminister = true
                  CurrentUserId = user.Id
                  SelectedUsers = []
                  UnselectedUsers = users }
            return!
                Page.framedPage
                    c
                    Status.Ok
                    newSize
                    (fun w ctx -> NewPage.render w ctx form)
                    NewPage.head
                    (fun w ctx -> NewPage.content w ctx form)
        }

    /// `broadcast_create_room` / `broadcast_update_room`: the shared-room partial, rendered once, to
    /// every member's own rooms stream.
    let private broadcastToMembers (c: Ctx) (room: Room) (update: bool) : Task<Result<unit, Error>> =
        act {
            let! partials = RoomsController.renderSharedRoom c room
            let broadcasts = c.App.Broadcasts
            return!
                c.App.Read(fun conn ->
                    if update then
                        broadcasts.ClosedRoomUpdate(conn, room, Rendered.partials partials)
                    else
                        broadcasts.ClosedRoomCreate(conn, room, Rendered.partials partials))
        }

    let create (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            do! RoomsController.ensurePermissionToCreateRooms c
            let! name = RoomsController.roomNameParam c
            let name = Option.flatten name
            let! (user: User) = Concerns.requireCurrentUser c
            let granteeIds = RoomsController.userIdsParam c
            // Rooms::Closed.create_for(room_params, users: grantees)
            let! (room: Room) =
                c.App.Write(fun tx ->
                    let grantees = RoomsController.existingUserIds tx.Conn granteeIds
                    Room.createFor tx RoomType.Closed name user.Id grantees)
            do! broadcastToMembers c room false
            return! RoomsController.redirectToRoom c room.Id
        }

    let edit (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            let! (room: Room) = RoomsController.setRoom c WithoutDirects
            // force_room_type
            let room = { room with RoomType = RoomType.Closed }
            let! (current: User) = Concerns.requireCurrentUser c
            let secrets = c.App.Secrets
            let roomId = room.Id
            let! selected, unselected =
                c.App.ReadOffloaded(fun conn ->
                    let selectedIds = Room.userIds conn (Room.find conn roomId)
                    let selected, unselected =
                        User.activeOrdered conn |> List.partition (fun user -> List.contains user.Id selectedIds)
                    let views = List.map (PresenterSupport.userView secrets)
                    views selected, views unselected)
            let form: ClosedFormView =
                { Room = { Id = Some room.Id; Name = room.Name }
                  CanAdminister = User.canAdminister current (Some room.CreatorId) false
                  CurrentUserId = current.Id
                  SelectedUsers = selected
                  UnselectedUsers = unselected }
            return!
                Page.framedPage
                    c
                    Status.Ok
                    editSize
                    (fun w ctx -> EditPage.render w ctx form)
                    EditPage.head
                    (fun w ctx -> EditPage.content w ctx form)
        }

    let update (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            let! (room: Room) = RoomsController.setRoom c WithoutDirects
            do! RoomsController.ensureCanAdminister c room
            let! name = RoomsController.roomNameParam c
            let granteeIds = RoomsController.userIdsParam c
            // force_room_type, then `@room.update! room_params`
            let! (room: Room) = c.App.Write(fun tx -> Room.update tx room name (Some RoomType.Closed))
            // `@room.memberships.revise(granted: grantees, revoked: revokees)`
            do!
                c.App.Write(fun tx ->
                    let granted = RoomsController.existingUserIds tx.Conn granteeIds
                    let revoked = Room.userIds tx.Conn room |> List.filter (fun id -> not (List.contains id granteeIds))
                    Room.revise tx room granted revoked)
            do! broadcastToMembers c room true
            return! RoomsController.redirectToRoom c room.Id
        }
