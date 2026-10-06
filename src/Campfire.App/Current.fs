// Port of the `Current` part of rust/crates/campfire/src/concerns.rs
//
// `Current` attributes (`Current.user`, `Current.session`, `authenticated_by`) live in the `Ctx`'s
// extensions, one per type; read them with these functions. They are split from `Concerns` (the
// before-actions) only because F# files are ordered: the layout loads what it shows of `Current`, and
// `Concerns` renders pages through the layout.
namespace Campfire.App

open Campfire.Kit
open Campfire.Db
open Campfire.Ruby

/// `Current.user`
type CurrentUserSlot = CurrentUserSlot of User

/// `Current.session`
type CurrentSessionSlot = CurrentSessionSlot of Session

/// `authenticated_by` (`"".inquiry`, `"session"` or `"bot_key"`).
type AuthenticatedBy =
    | Anonymous
    | BySession
    | BotKey

/// The route that matched the current request (`request.path_parameters` plus the endpoint),
/// available to actions as `c.Current<MatchedRoute>()`.
type MatchedRoute = { Endpoint: string }

module Current =
    let currentUser (c: Ctx) : User option =
        match c.Current<CurrentUserSlot>() with
        | ValueSome(CurrentUserSlot user) -> Some user
        | ValueNone -> None

    /// `Current.user` where a before-action guarantees one (after `require_authentication`).
    let requireCurrentUser (c: Ctx) : Result<User, Error> =
        match currentUser c with
        | Some user -> Ok user
        | None -> Error(Internal(exn "no Current.user"))

    let currentSession (c: Ctx) : Session option =
        match c.Current<CurrentSessionSlot>() with
        | ValueSome(CurrentSessionSlot session) -> Some session
        | ValueNone -> None

    /// `signed_in?`
    let signedIn (c: Ctx) : bool = (currentUser c).IsSome

    let authenticatedBy (c: Ctx) : AuthenticatedBy =
        match c.Current<AuthenticatedBy>() with
        | ValueSome by -> by
        | ValueNone -> Anonymous

    let setAuthenticatedBy (c: Ctx) (by: AuthenticatedBy) : unit = c.SetCurrent by

    /// `platform` (`helper_method`): `@platform ||= ApplicationPlatform.new(request.user_agent)`, the
    /// one `allow_browser` kept, or a new one when it didn't parse the header.
    let platform (c: Ctx) : ApplicationPlatform =
        match c.Current<ApplicationPlatform>() with
        | ValueSome platform -> platform
        | ValueNone -> ApplicationPlatform.create c.Request.UserAgent

    /// The `last_room` cookie, as `find_by(id: cookies[:last_room])` casts it (like an integer column).
    let lastRoomCookie (c: Ctx) : int64 option =
        match c.Cookies.Get "last_room" with
        | null -> None
        | value -> Ruby.integerCast value

    /// `last_room_visited` inside a read that's already on a reader, for the user and the
    /// `lastRoomCookie`: the cookie's room if the user is in it, else `Current.user.rooms.original`.
    let lastRoomVisitedIn (conn: Conn) (userId: int64) (lastRoom: int64 option) : Room option =
        let found =
            match lastRoom with
            | Some roomId -> Room.findForUser conn userId roomId
            | None -> None
        match found with
        | Some room -> Some room
        | None -> Room.originalForUser conn userId
