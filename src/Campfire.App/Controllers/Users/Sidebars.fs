// Port of rust/crates/campfire/src/controllers/users/sidebars.rs
//
// `Users::SidebarsController` (reference/app/controllers/users/sidebars_controller.rb): the room
// list, loaded into the `user_sidebar` turbo frame.
namespace Campfire.App.Controllers

open System.Threading.Tasks
open Campfire.Views
open Campfire.Views.Users
open Campfire.Kit
open Campfire.App
open Campfire.App.Channels
open Campfire.App.Presenters
open Campfire.Db

module Sidebars =
    module ShowPage = Campfire.Views.Templates.Users.Sidebars.Show

    let private showSize = RenderSize()

    let show (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            do! c.RespondTo [ Format.Html ] |> Result.map ignore
            let! (user: User) = Concerns.requireCurrentUser c
            let secrets = c.App.Secrets
            let fragments = c.App.FragmentCache
            let! sidebar, restricted =
                c.App.Read(fun conn ->
                    // The direct rooms' fragments come from the store the render then uses.
                    let sidebar = FragmentCache.withCache fragments (fun () -> Accounts.sidebar conn secrets user)
                    let restricted =
                        Account.first conn
                        |> Option.exists (fun account -> AccountSettings.restrictRoomCreationToAdministrators (Account.settings account))
                    sidebar, restricted)

            let data: SidebarShow =
                { CurrentUser = Common.userSummary secrets user
                  // turbo_stream_from :rooms / turbo_stream_from Current.user, :rooms
                  RoomsStream = Campfire.RailsCompat.Turbo.signedStreamName secrets [ "rooms" ]
                  UserRoomsStream = Campfire.RailsCompat.Turbo.signedStreamName secrets [ Campfire.RailsCompat.GlobalId.toParam (Gid.userGid user.Id); "rooms" ]
                  DirectMemberships = sidebar.DirectMemberships
                  DirectPlaceholderUsers = sidebar.DirectPlaceholderUsers
                  OtherMemberships = sidebar.OtherMemberships
                  // `Current.user.administrator? || !Current.account.settings.restrict_room_creation_to_administrators?`
                  CanCreateRooms = User.isAdministrator user || not restricted }
            return!
                Page.framedPage
                    c
                    Status.Ok
                    showSize
                    (fun w ctx -> ShowPage.render w ctx data)
                    ShowPage.head
                    (fun w ctx -> ShowPage.content w ctx data)
        }
