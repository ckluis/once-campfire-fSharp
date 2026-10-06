// Port of rust/crates/views/src/users.rs and users/summary.rs (the view-models; the templates they
// feed are modules under Templates/Users)
/// Views for `reference/app/views/users`: the user view-model shared by every users/accounts/
/// autocompletable template, and what the sidebar and profile show.
module Campfire.Views.Users

open System
open Campfire.RailsCompat
open Campfire.Views.Helpers

type Role =
    | Member
    | Administrator
    | Bot

module Role =
    let asStr (role: Role) : string =
        match role with
        | Member -> "member"
        | Administrator -> "administrator"
        | Bot -> "bot"

type Status =
    | Active
    | Deactivated
    | Banned

/// A `User` row as the views see it.
type UserSummary =
    { Id: int64
      Name: string
      Bio: string option
      EmailAddress: string option
      Role: Role
      Status: Status
      /// `fresh_user_avatar_path(user)`.
      AvatarPath: string }

    member this.Active = this.Status = Active
    member this.Banned = this.Status = Banned
    member this.Deactivated = this.Status = Deactivated
    member this.Bot = this.Role = Bot
    member this.Administrator = this.Role = Administrator

    /// `User#title`.
    member this.Title: string = UsersHelper.userTitle this.Name this.Bio

    /// `User#initials`.
    member this.Initials: string = UsersHelper.initials this.Name

    member this.Avatar: UsersHelper.AvatarUser = { Id = this.Id; Title = this.Title; AvatarPath = this.AvatarPath }

    /// `name.split(' ')` (awk-style split on ASCII whitespace: space, tab, newline, form feed, carriage return).
    member this.NameParts: string list =
        this.Name.Split([| ' '; '\t'; '\n'; '\u000C'; '\r' |], StringSplitOptions.RemoveEmptyEntries) |> List.ofArray

    /// `name.split(' ')[0]`.
    member this.FirstName: string =
        match this.NameParts with
        | first :: _ -> first
        | [] -> ""

module UserSummary =
    /// `UserSummary::default()`.
    let empty: UserSummary =
        { Id = 0L
          Name = ""
          Bio = None
          EmailAddress = None
          Role = Member
          Status = Active
          AvatarPath = "" }

/// A user as `users/_mention` and the autocompletable views see it.
type MentionUser =
    { User: UserSummary
      /// `user.attachable_sgid`.
      AttachableSgid: string }

/// A membership row on the profile (`users/profiles/_membership`).
type ProfileMembership =
    { RoomId: int64
      /// "rooms_open", "rooms_closed" or "rooms_direct".
      RoomParamKey: string
      /// `room_display_name(membership.room)`.
      RoomDisplayName: string
      Involvement: string
      Direct: bool }

    member this.InvolvementRoom: RoomsHelper.InvolvementRoom =
        { Id = this.RoomId
          ParamKey = this.RoomParamKey
          Direct = this.Direct }

/// A `Push::Subscription`, with its user agent parsed (`UserAgent.parse`).
type PushSubscription =
    { Id: int64
      Endpoint: string
      Browser: string
      Version: string
      Platform: string }

/// A direct room in the sidebar (`users/sidebars/rooms/_direct`).
type SidebarDirect =
    { RoomId: int64
      Unread: bool
      /// `room.updated_at.to_fs(:epoch)`.
      UpdatedAtEpoch: string
      /// `room.users.without(membership.user).presence || [ membership.user ]`, in that order.
      Members: UserSummary list
      /// The membership's id and `updated_at`: the partial is `cache membership`.
      MembershipId: int64
      MembershipUpdatedAt: Timestamp }

    member this.ClassNames: string = if this.Unread then "direct unread" else "direct"

    /// `members.map { |m| m.name.split(' ')[0, 3].map { |s| s[0].capitalize }.join }.to_sentence(two_words_connector: '+')`.
    member this.MemberInitials: string =
        let firstCharacter (part: string) = part.EnumerateRunes() |> Seq.head |> string
        let initials =
            this.Members
            |> List.map (fun member' ->
                member'.NameParts |> List.truncate 3 |> List.map (firstCharacter >> Application.capitalize) |> String.concat "")
        Application.toSentence initials "+"

/// A direct room on its way into the sidebar: the `users/sidebars/rooms/_direct` fragment when
/// the cache already holds this membership version (`cache membership` wraps the whole partial,
/// so Rails evaluates none of it then), else the view to render it from.
type SidebarDirectItem =
    | Cached of Fragment
    | View of SidebarDirect

/// A shared room in the sidebar (`users/sidebars/rooms/_shared`).
type SidebarRoom =
    { Id: int64
      /// "rooms_open" or "rooms_closed".
      ParamKey: string
      Name: string
      Unread: bool }

    member this.ClassNames: string =
        if this.Unread then "align-center gap room btn txt-nowrap unread" else "align-center gap room btn txt-nowrap"

/// What `users/sidebars/show` shows.
type SidebarShow =
    { CurrentUser: UserSummary
      /// `Turbo::StreamsChannel.signed_stream_name(:rooms)`.
      RoomsStream: string
      /// `Turbo::StreamsChannel.signed_stream_name([ Current.user, :rooms ])`.
      UserRoomsStream: string
      DirectMemberships: SidebarDirectItem list
      DirectPlaceholderUsers: UserSummary list
      OtherMemberships: SidebarRoom list
      /// `Current.user.administrator? || !Current.account.settings.restrict_room_creation_to_administrators?`.
      CanCreateRooms: bool }

/// The template digest in `users/sidebars/rooms/_direct`'s fragment keys: the partial.
let directRoomDigest: string = FragmentCache.digest [| "users/sidebars/rooms/_direct" |]

let directRoomFragmentKey (key: KeyBuf) (membershipId: int64) (updatedAt: Timestamp) : unit =
    FragmentCache.pushRecordFragmentKey key "users/sidebars/rooms/_direct" directRoomDigest "memberships" membershipId updatedAt

/// The `users/sidebars/rooms/_direct` fragment for this membership version, if the current store
/// holds it.
let cachedDirectRoomFragment (membershipId: int64) (updatedAt: Timestamp) : Fragment =
    FragmentCache.read (fun key -> directRoomFragmentKey key membershipId updatedAt)
