// Port of rust/crates/campfire/src/controllers/rooms.rs
//
// `RoomsController` (reference/app/controllers/rooms_controller.rb), plus what its subclasses
// share: `set_room` over a `room_scope`, `ensure_can_administer`, `ensure_permission_to_create_rooms`.
//
// The subclasses (`Rooms::OpensController` and friends) re-declare `before_action :set_room`
// (and `ensure_can_administer`) with their own `only:`, which *replaces* the parent's callback.
// So the actions they inherit from here but don't list (`destroy` for opens/closeds, `show` for
// directs) run without `set_room` and raise on the nil `@room`, as in the reference.
namespace Campfire.App.Controllers

open System.Threading.Tasks
open Campfire.App
open Campfire.App.Channels
open Campfire.App.Presenters
open Campfire.Db
open Campfire.Kit
open Campfire.Routes
open Campfire.Ruby
open Campfire.Views

/// `room_scope`: which of `Current.user.rooms` a controller may act on.
type Scope =
    /// `Current.user.rooms` (RoomsController)
    | All
    /// `Current.user.rooms.without_directs` (opens, closeds)
    | WithoutDirects
    /// `Current.user.rooms.directs` (directs)
    | Directs

module Scope =
    let includes (scope: Scope) (room: Room) : bool =
        match scope with
        | All -> true
        | WithoutDirects -> room.RoomType <> RoomType.Direct
        | Directs -> room.RoomType = RoomType.Direct

module RoomsController =
    module ShowPage = Campfire.Views.Templates.Rooms.ShowPage
    module SharedPartial = Campfire.Views.Templates.Users.Sidebars.Rooms._Shared

    let private showSize = RenderSize()

    // --- Shared before-actions ---------------------------------------------------------------------

    /// `set_room`: `room_scope.find_by(id: params[:room_id] || params[:id])`, or back to the root with
    /// an alert.
    let setRoom (c: Ctx) (scope: Scope) : Task<Result<Room, Error>> =
        act {
            let! (user: User) = Concerns.requireCurrentUser c
            let id =
                match c.ParamStr "room_id" with
                | null -> c.ParamStr "id"
                | id -> id
            let! (room: Room option) =
                match (match id with null -> None | id -> Ruby.integerCast id) with
                | Some id -> c.App.Read(fun conn -> Room.findForUser conn user.Id id)
                | None -> Task.FromResult(Ok None)
            match room |> Option.filter (Scope.includes scope) with
            | Some room -> return room
            | None ->
                let root = c.UrlFor(Routes.root ())
                let! redirect = c.RedirectToWith(root, { Redirect.Default with Alert = "Room not found or inaccessible" })
                return! halt redirect
        }

    /// `ensure_can_administer`: `head :forbidden unless Current.user.can_administer?(@room)`.
    let ensureCanAdminister (c: Ctx) (room: Room) : Result<unit, Error> =
        match Concerns.requireCurrentUser c with
        | Error e -> Error e
        | Ok user ->
            if User.canAdminister user (Some room.CreatorId) false then
                Ok()
            else
                halt (Concerns.head Status.Forbidden)

    /// `ensure_permission_to_create_rooms`
    let ensurePermissionToCreateRooms (c: Ctx) : Task<Result<unit, Error>> =
        act {
            let! (user: User) = Concerns.requireCurrentUser c
            let! account = c.App.Read(fun conn -> Account.first conn)
            let restricted =
                account
                |> Option.exists (fun account -> AccountSettings.restrictRoomCreationToAdministrators (Account.settings account))
            if restricted && not (User.isAdministrator user) then
                return! halt (Concerns.head Status.Forbidden)
        }

    // --- Helpers -----------------------------------------------------------------------------------

    let redirectToRoot (c: Ctx) : Result<Response, Error> = c.RedirectTo(c.UrlFor(Routes.root ()))

    let redirectToRoom (c: Ctx) (roomId: int64) : Result<Response, Error> = c.RedirectTo(c.UrlFor(Routes.room roomId))

    /// `params.require(:room).permit(:name)`: `Some name` when the name was submitted.
    let roomNameParam (c: Ctx) : Result<string option option, Error> =
        c.Params.Require "room"
        |> Result.map (fun room ->
            let permitted = room.Permit(Params.permitKeys [ "name" ])
            match permitted.Get "name" with
            | ValueSome(Param.Str name) -> Some(Some name)
            | ValueSome _ -> Some None
            | ValueNone -> None)

    /// `params.fetch(:user_ids, [])` as ids `User.where(id:)` can match.
    let userIdsParam (c: Ctx) : int64 list =
        let ofStr (value: Param) : int64 option =
            match value with
            | Param.Str value -> Ruby.integerCast value
            | _ -> None
        match c.Param "user_ids" with
        | ValueSome(Param.Array values) -> values |> Seq.choose ofStr |> List.ofSeq
        | ValueSome value -> ofStr value |> Option.toList
        | ValueNone -> []

    /// `User.where(id: ids)`, as ids of existing users (in id order, like the query).
    let existingUserIds (conn: Conn) (ids: int64 list) : int64 list = User.whereIds conn ids |> List.map (fun user -> user.Id)

    /// Renders `users/sidebars/rooms/_shared` for `room` outside a request.
    let renderSharedRoom (c: Ctx) (room: Room) : Task<Result<Rendered, Error>> =
        let app = c.App
        let baseUrl = Page.rendererBaseUrl c
        app.Read(fun conn ->
            let presenter = Presenter(conn, app, None)
            let sidebarRoom = presenter.SidebarRoom room
            let account = Account.first conn
            let html =
                Page.renderDetachedAt app account baseUrl (fun _ -> Render.text (fun w -> SharedPartial.render w sidebarRoom))
            { Rendered.empty with SharedRoom = Some html })

    /// `rooms/show` with `find_messages`: the page around `params[:message_id]`, else the last page.
    let private renderShow (c: Ctx) (room: Room) : Task<Result<Response, Error>> =
        act {
            let app = c.App
            let! (user: User) = Concerns.requireCurrentUser c
            let messageId = match c.ParamStr "message_id" with null -> None | id -> Ruby.integerCast id
            let requestHost = Some c.Request.Host
            let! show =
                app.Read(fun conn ->
                    let messages =
                        match messageId |> Option.bind (fun id -> Message.findById conn id) with
                        | Some message when message.RoomId = room.Id -> Message.pageAround conn room.Id message
                        | _ -> Message.lastPage conn room.Id
                    let presenter = Presenter(conn, app, requestHost)
                    let original = Room.original conn |> Option.exists (fun original -> original.Id = room.Id)
                    let roomGid = Campfire.RailsCompat.GlobalId.toParam (Gid.roomGid room)
                    ({ Room = presenter.RoomView(room, user)
                       UpdatedAt = room.UpdatedAt.ToDateTimeOffset()
                       User = PresenterSupport.userView app.Secrets user
                       // The page's message fragments come from the store the render then uses.
                       Messages = FragmentCache.withCache app.FragmentCache (fun () -> presenter.Messages messages)
                       Invitation = original && not (Message.paged conn room.Id)
                       JoinCode = Account.first conn |> Option.map (fun account -> account.JoinCode) |> Option.defaultValue ""
                       MessagesStreamName = Campfire.RailsCompat.Turbo.signedStreamName app.Secrets [ roomGid; "messages" ] }: Campfire.Views.Rooms.ShowView))
            return!
                Page.framedPage
                    c
                    Status.Ok
                    showSize
                    (fun w ctx -> ShowPage.render w ctx show)
                    (fun w -> ShowPage.head w show)
                    (fun w ctx -> ShowPage.content w ctx show)
        }

    let destroyRoom (c: Ctx) (room: Room) : Task<Result<Response, Error>> =
        act {
            do! c.App.Write(fun tx -> Room.destroy tx room)
            // broadcast_remove_to :rooms, target: [ @room, :list ]
            c.App.Broadcasts.RoomRemove room
            return! redirectToRoot c
        }

    // --- Actions -----------------------------------------------------------------------------------

    /// `index`: `redirect_to room_url(Current.user.rooms.last)` (inherited by the room-type
    /// controllers). With no rooms, `room_url(nil)` raises.
    let index (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            let! (user: User) = Concerns.requireCurrentUser c
            let! (room: Room option) = c.App.Read(fun conn -> Room.lastForUser conn user.Id)
            match room with
            | None -> return! Error(Internal(exn "No route matches room_url(nil)"))
            | Some room -> return! c.RedirectTo(c.UrlFor(Routes.room room.Id))
        }

    /// `show`, also `GET /rooms/:room_id/@:message_id`.
    let show (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            let! (room: Room) = setRoom c All
            Concerns.rememberLastRoomVisited c room.Id
            return! renderShow c room
        }

    /// `destroy` (RoomsController and `Rooms::DirectsController`).
    let destroy (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            let! (room: Room) = setRoom c All
            do! ensureCanAdminister c room
            return! destroyRoom c room
        }

    /// `destroy` inherited by `Rooms::OpensController` and `Rooms::ClosedsController`, whose
    /// `set_room` doesn't run for it: `nil.destroy` raises NoMethodError.
    let destroyWithoutRoom (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            return! Error(Internal(exn "undefined method 'destroy' for nil"))
        }
