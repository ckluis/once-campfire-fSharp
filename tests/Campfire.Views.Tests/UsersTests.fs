// What the view-models of users.rs, users/summary.rs and accounts.rs compute (Rust has no unit tests of
// them; their bytes are compared by bin/views-differential where a template or helper prints them)
module Campfire.Views.Tests.UsersTests

open Xunit
open Campfire.RailsCompat
open Campfire.Views
open Campfire.Views.Users

let private user (name: string) : UserSummary = { UserSummary.empty with Id = 1L; Name = name }

[<Fact>]
let ``a name splits on ascii whitespace only`` () =
    Assert.Equal<string list>([ "David"; "Heinemeier"; "Hansson" ], (user "  David \t Heinemeier\nHansson").NameParts)
    // A vertical tab and a no-break space are not separators (awk-style `split(' ')`).
    Assert.Equal<string list>([ "a\u000Bb"; "c d" ], (user "a\u000Bb c d").NameParts)
    Assert.Equal("David", (user "David Hansson").FirstName)
    Assert.Equal("", (user "   ").FirstName)

[<Fact>]
let ``a user knows its role and status`` () =
    let bot = { user "Bot" with Role = Bot }
    Assert.True bot.Bot
    Assert.False bot.Administrator
    Assert.True({ user "A" with Role = Administrator }.Administrator)
    Assert.True({ user "B" with Status = Banned }.Banned)
    Assert.True({ user "D" with Status = Deactivated }.Deactivated)
    Assert.True((user "N").Active)
    Assert.Equal("administrator", Role.asStr Administrator)

[<Fact>]
let ``a user title and initials come from the helpers`` () =
    let kevin = { user "Kevin Smith" with Bio = Some "Programmer" }
    Assert.Equal("Kevin Smith – Programmer", kevin.Title)
    Assert.Equal("KS", kevin.Initials)
    Assert.Equal("Kevin Smith – Programmer", kevin.Avatar.Title)

[<Fact>]
let ``a direct room in the sidebar names its members by their initials`` () =
    let direct (names: string list) (unread: bool) : SidebarDirect =
        { RoomId = 1L
          Unread = unread
          UpdatedAtEpoch = "0"
          Members = names |> List.map user
          MembershipId = 1L
          MembershipUpdatedAt = Timestamps.unixEpoch }
    Assert.Equal("DH+KS", (direct [ "david hansson"; "kevin smith" ] false).MemberInitials)
    Assert.Equal("DHH", (direct [ "david heinemeier hansson jr" ] false).MemberInitials)
    Assert.Equal("A, B, and C", (direct [ "a"; "b"; "c" ] false).MemberInitials)
    Assert.Equal("direct unread", (direct [ "a" ] true).ClassNames)
    Assert.Equal("direct", (direct [ "a" ] false).ClassNames)

[<Fact>]
let ``a shared room in the sidebar is unread or not`` () =
    let room unread : SidebarRoom = { Id = 1L; ParamKey = "rooms_open"; Name = "HQ"; Unread = unread }
    Assert.EndsWith("unread", (room true).ClassNames)
    Assert.Equal("align-center gap room btn txt-nowrap", (room false).ClassNames)

[<Fact>]
let ``direct room fragments live under the memberships key`` () =
    let key = KeyBuf.Of ""
    directRoomFragmentKey key 5L (Timestamps.unixEpoch)
    Assert.StartsWith("views/users/sidebars/rooms/_direct:", key.ToString())
    Assert.EndsWith("/memberships/5-19700101000000000000", key.ToString())
    Assert.Null(FragmentCache.read (fun key -> directRoomFragmentKey key 5L Timestamps.unixEpoch))
