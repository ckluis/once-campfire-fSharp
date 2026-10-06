// Port of rust/crates/campfire/src/controllers/rooms/opens.rs
//
// `Rooms::OpensController` (reference/app/controllers/rooms/opens_controller.rb). `index` is
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

module Opens =
    module NewPage = Campfire.Views.Templates.Rooms.Opens.New
    module EditPage = Campfire.Views.Templates.Rooms.Opens.Edit

    let private newSize = RenderSize()
    let private editSize = RenderSize()

    /// `DEFAULT_ROOM_NAME`
    [<Literal>]
    let DefaultRoomName = "New room"

    /// `User.active.ordered`
    let activeUsers (c: Ctx) : Task<Result<Campfire.Views.Messages.UserView list, Error>> =
        let secrets = c.App.Secrets
        c.App.ReadOffloaded(fun conn -> User.activeOrdered conn |> List.map (PresenterSupport.userView secrets))

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
            let! users = activeUsers c
            let form: OpenFormView =
                { Room = { Id = None; Name = Some DefaultRoomName }
                  CanAdminister = true
                  Users = users }
            return!
                Page.framedPage
                    c
                    Status.Ok
                    newSize
                    (fun w ctx -> NewPage.render w ctx form)
                    NewPage.head
                    (fun w ctx -> NewPage.content w ctx form)
        }

    let create (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            do! RoomsController.ensurePermissionToCreateRooms c
            let! name = RoomsController.roomNameParam c
            let name = Option.flatten name
            let! (user: User) = Concerns.requireCurrentUser c
            // Rooms::Open.create_for(room_params, users: Current.user)
            let! (room: Room) = c.App.Write(fun tx -> Room.createFor tx RoomType.Open name user.Id [ user.Id ])
            let! partials = RoomsController.renderSharedRoom c room
            c.App.Broadcasts.OpenRoomCreate(room, Rendered.partials partials)
            return! RoomsController.redirectToRoom c room.Id
        }

    let edit (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            let! (room: Room) = RoomsController.setRoom c WithoutDirects
            // force_room_type
            let room = { room with RoomType = RoomType.Open }
            let! (user: User) = Concerns.requireCurrentUser c
            let! users = activeUsers c
            let form: OpenFormView =
                { Room = { Id = Some room.Id; Name = room.Name }
                  CanAdminister = User.canAdminister user (Some room.CreatorId) false
                  Users = users }
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
            // force_room_type, then `@room.update! room_params` saves the name and the new type.
            let! (room: Room) = c.App.Write(fun tx -> Room.update tx room name (Some RoomType.Open))
            let! partials = RoomsController.renderSharedRoom c room
            c.App.Broadcasts.OpenRoomUpdate(room, Rendered.partials partials)
            return! RoomsController.redirectToRoom c room.Id
        }
