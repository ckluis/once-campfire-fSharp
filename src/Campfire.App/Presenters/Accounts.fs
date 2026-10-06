// Port of rust/crates/campfire/src/controllers/presenters/accounts.rs
//
// Maps database rows into the view models `Campfire.Views` renders for the account, session and
// user screens (sessions, first run, users, accounts, autocompletable, pwa): what the Rails views
// read off `@user`, `Current.account` and friends, computed up front. The `ViewContext` builder
// for every page is `Layout` (`ViewContext.fs`).
//
// Queries the db library doesn't have yet are written here against the reference's SQL.
// (`touch` is in `Common`: `Attachments` needs it and F# files are ordered.)
namespace Campfire.App.Presenters

open System
open System.Text
open Campfire.App
open Campfire.Db
open Campfire.Kit
open Campfire.RailsCompat
open Campfire.Routes
open Campfire.Views
open Campfire.Views.Accounts
open Campfire.Views.Users

/// The sidebar of `Users::SidebarsController#show`.
type Sidebar =
    { DirectMemberships: SidebarDirectItem list
      OtherMemberships: SidebarRoom list
      DirectPlaceholderUsers: UserSummary list }

module Accounts =
    /// `User::Transferable::TRANSFER_LINK_EXPIRY_DURATION`
    let TransferLinkExpiry: TimeSpan = TimeSpan.FromHours 4.0

    /// `user.transfer_id`: `signed_id(purpose: :transfer, expires_in: 4.hours)`.
    let transferId (secrets: Secrets) (userId: int64) (now: DateTimeOffset) : string =
        SignedId.generate secrets "User" userId (Some "transfer") (Some(now + TransferLinkExpiry))

    /// `User.find_by_transfer_id(id)`: the user id, if the signature and expiry check out.
    let userIdFromTransferId (secrets: Secrets) (transferId: string) (now: DateTimeOffset) : int64 option =
        SignedId.verify secrets "User" transferId (Some "transfer") now

    /// `User.from_avatar_token(sid)`'s verification half (`find_signed!(sid, purpose: :avatar)`).
    let userIdFromAvatarToken (secrets: Secrets) (token: string) (now: DateTimeOffset) : int64 option =
        SignedId.verify secrets "User" token (Some "avatar") now

    /// `user.attachable_sgid`.
    let attachableSgid (secrets: Secrets) (userId: int64) : string =
        GlobalId.attachableSgid secrets (GlobalId.create "User" (string userId))

    /// `fresh_account_logo_path(size:)`: `v` is `Current.account&.updated_at&.to_fs(:number)`.
    let freshAccountLogoPath (account: Account option) (size: string option) : string =
        let v = account |> Option.map (fun account -> Common.toFsNumber account.UpdatedAt)
        Routes.freshAccountLogo v size

    /// `platform` as the views see it (`ApplicationPlatform.new(request.user_agent)`).
    let platform (c: Ctx) : Platform = ApplicationPlatform.toView (Current.platform c)

    /// `User.administrator.first`, for `accounts/_help_contact`.
    let helpContact (conn: Conn) : HelpContact option =
        conn.QueryOne(
            """SELECT "users"."name", "users"."email_address" FROM "users" WHERE "users"."role" = 1 ORDER BY "users"."id" ASC LIMIT 1""",
            [||],
            fun r -> { Name = r.Text 0; EmailAddress = defaultArg (r.OptText 1) "" }
        )

    /// `User.none?`
    let noUsers (conn: Conn) : bool = User.count conn = 0L

    // --- Users -----------------------------------------------------------------------------------------

    /// A user for `users/_mention` and the autocompletable views.
    let mentionUser (secrets: Secrets) (user: User) : MentionUser =
        { User = Common.userSummary secrets user
          AttachableSgid = attachableSgid secrets user.Id }

    /// `room_display_name(room, for_user:)`: a direct room is named after its other members.
    let roomDisplayName (conn: Conn) (room: Room) (forUser: User) : string =
        let names =
            if Room.isDirect room then
                Room.users conn room |> List.filter (fun user -> user.Id <> forUser.Id) |> List.map (fun user -> user.Name)
            else
                []
        Rooms.roomDisplayName room.Name (Room.isDirect room) names (Some forUser.Name)

    /// `Room.model_name.param_key` for the room's STI class.
    let roomParamKey (roomType: RoomType) : string =
        match roomType with
        | Open -> "rooms_open"
        | Closed -> "rooms_closed"
        | Direct -> "rooms_direct"

    /// Users::ProfilesController#show: `Current.user.memberships.with_ordered_room.partition { |m| m.room.direct? }`,
    /// returned as `(direct, shared)`.
    let profileMemberships (conn: Conn) (user: User) : ProfileMembership list * ProfileMembership list =
        let views =
            Membership.withOrderedRoom conn user.Id
            |> List.map (fun (membership, room) ->
                { RoomId = room.Id
                  RoomParamKey = roomParamKey room.RoomType
                  RoomDisplayName = roomDisplayName conn room user
                  Involvement = membership.Involvement |> Option.map Involvement.name |> Option.defaultValue ""
                  Direct = Room.isDirect room })
        let direct, shared = views |> List.partition (fun view -> view.Direct)
        direct, shared

    // --- Sidebar -----------------------------------------------------------------------------------------

    /// `Users::SidebarsController::DIRECT_PLACEHOLDERS`
    [<Literal>]
    let DirectPlaceholders = 20L

    /// `users/sidebars/rooms/_direct` locals: `room.users.without(membership.user).presence || [ membership.user ]`.
    let sidebarDirect (conn: Conn) (secrets: Secrets) (membership: Membership) (room: Room) : SidebarDirect =
        let users = Room.users conn room
        let members =
            match users |> List.filter (fun user -> user.Id <> membership.UserId) with
            | [] -> [ User.find conn membership.UserId ]
            | others -> others
        { RoomId = room.Id
          Unread = Membership.unread membership
          UpdatedAtEpoch = Common.epochString room.UpdatedAt
          Members = members |> List.map (Common.userSummary secrets)
          MembershipId = membership.Id
          MembershipUpdatedAt = membership.UpdatedAt.ToDateTimeOffset() }

    /// `find_direct_placeholder_users`. `exclude_user_ids` is `Membership.where(room_id: directs).pluck(:user_id).uniq`
    /// `.including(Current.user.id)`: `including` appends even when the id is already there, and the
    /// limit counts that duplicate.
    let private directPlaceholderUsers (conn: Conn) (secrets: Secrets) (user: User) : UserSummary list =
        let directRoomIds = Room.forUserOfType conn user.Id Direct |> List.map (fun room -> room.Id)
        let excluded = ResizeArray<int64>()
        if not directRoomIds.IsEmpty then
            let sql =
                $"""SELECT "memberships"."user_id" FROM "memberships" WHERE "memberships"."room_id" IN ({Sql.placeholders directRoomIds.Length})"""
            for id in conn.QueryAll(sql, directRoomIds |> List.map I |> Array.ofList, fun r -> r.Int64 0) do
                if not (excluded.Contains id) then excluded.Add id
        excluded.Add user.Id
        // The limit goes in the SQL: under ORDER BY, SQLite recompiles a statement with a bound LIMIT
        // every time it runs.
        let limit = max (DirectPlaceholders - int64 excluded.Count) 0L
        let sql =
            $"""{User.Select} WHERE "users"."status" = 0 AND "users"."id" NOT IN ({Sql.placeholders excluded.Count}) ORDER BY "users"."created_at" ASC LIMIT {limit}"""
        User.findBySql conn sql (excluded |> Seq.map I |> Array.ofSeq) |> List.map (Common.userSummary secrets)

    /// Users::SidebarsController#show.
    let sidebar (conn: Conn) (secrets: Secrets) (user: User) : Sidebar =
        let allMemberships = Membership.visibleWithOrderedRoom conn user.Id

        // `select { direct }.sort_by { |m| m.room.updated_at }.reverse`
        let direct =
            allMemberships
            |> List.filter (fun (_, room) -> Room.isDirect room)
            |> List.sortBy (fun (_, room) -> room.UpdatedAt.Microsecond)
            |> List.rev

        let directMemberships =
            direct
            |> List.map (fun (membership, room) ->
                // `cache membership` wraps the whole partial: on a hit Rails loads none of its members.
                match Users.cachedDirectRoomFragment membership.Id (membership.UpdatedAt.ToDateTimeOffset()) with
                | null -> View(sidebarDirect conn secrets membership room)
                | html -> Cached html)
        let otherMemberships =
            allMemberships
            |> List.filter (fun (membership, _) -> not (direct |> List.exists (fun (d, _) -> d.Id = membership.Id)))
            |> List.map (fun (membership, room) ->
                { Id = room.Id
                  ParamKey = roomParamKey room.RoomType
                  Name = defaultArg room.Name ""
                  Unread = Membership.unread membership })
        { DirectMemberships = directMemberships
          OtherMemberships = otherMemberships
          DirectPlaceholderUsers = directPlaceholderUsers conn secrets user }

    // --- Account ---------------------------------------------------------------------------------------

    /// AccountsController#account_users: `User.where(status: [ :active, :banned ])` for
    /// administrators, `User.active` otherwise; `.ordered.without_bots`.
    let accountUsers (conn: Conn) (canAdminister: bool) : User list =
        let status = if canAdminister then "\"users\".\"status\" IN (0, 2)" else "\"users\".\"status\" = 0"
        User.findBySql conn $"{User.Select} WHERE {status} AND \"users\".\"role\" != 2 ORDER BY LOWER(name)" [||]

    // --- Helpers ---------------------------------------------------------------------------------------

    /// `ORDER BY LOWER(name)`: SQLite lowercases ASCII only. Compared as UTF-8 bytes, the way
    /// SQLite's BINARY collation does.
    let sortByLowerName (name: 'T -> string) (items: 'T list) : 'T list =
        let lower (text: string) = String.map (fun c -> if c >= 'A' && c <= 'Z' then char (int c + 32) else c) text
        let compare (a: string) (b: string) =
            ReadOnlySpan<byte>(Encoding.UTF8.GetBytes a).SequenceCompareTo(ReadOnlySpan<byte>(Encoding.UTF8.GetBytes b))
        // Stable, as `sort_by_cached_key` is.
        items |> List.map (fun item -> lower (name item), item) |> List.sortWith (fun (a, _) (b, _) -> compare a b) |> List.map snd

    /// A bot row for `accounts/bots/_bot`: its key and `bot.rooms.without_directs.ordered`.
    let bot (conn: Conn) (secrets: Secrets) (bot: User) : Bot =
        let rooms = Room.forUserWithoutDirects conn bot.Id |> sortByLowerName (fun room -> defaultArg room.Name "")
        { User = Common.userSummary secrets bot
          BotKey = User.botKey bot
          Rooms = rooms |> List.map (fun room -> { Id = room.Id; Name = defaultArg room.Name "" }) }

    /// The fields `accounts/bots/_form` fills in: `image_tag bot.avatar` is the blob's absolute
    /// redirect URL.
    let botForm (conn: Conn) (storage: Campfire.Storage.Storage) (baseUrl: string) (bot: User) : BotForm =
        let avatar = Attachments.attachedBlob conn "User" bot.Id "avatar"
        { Name = Some bot.Name
          WebhookUrl = User.webhookUrl conn bot
          AvatarAttachmentUrl =
            avatar |> Option.map (fun blob -> baseUrl + Campfire.Storage.Paths.blobRedirectPath storage.Verifier blob None) }

    /// A push subscription with its user agent parsed like `UserAgent.parse(push_subscription.user_agent)`.
    let pushSubscription (subscription: Campfire.Db.PushSubscription) : Campfire.Views.Users.PushSubscription =
        let agent = UserAgent.parse (defaultArg subscription.UserAgent "")
        { Id = subscription.Id
          Endpoint = defaultArg subscription.Endpoint ""
          Browser = UserAgent.Agent.browser agent
          Version = (UserAgent.Agent.version agent).String
          Platform = defaultArg (UserAgent.Agent.platform agent) "" }

    /// A permitted string attribute: `Some` when the key was given (its value may be nil).
    let stringAttribute (parameters: ParamMap) (key: string) : string option option =
        if not (parameters.ContainsKey key) then
            None
        else
            Some(
                match parameters.Get key with
                | ValueSome param -> Option.ofObj (param.ToS())
                | ValueNone -> None
            )
