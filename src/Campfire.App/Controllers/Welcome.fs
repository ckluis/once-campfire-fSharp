// Port of rust/crates/campfire/src/controllers/welcome.rs
//
// `WelcomeController` (reference/app/controllers/welcome_controller.rb): the root URL.
namespace Campfire.App.Controllers

open System.Threading.Tasks
open Campfire.Kit
open Campfire.App
open Campfire.App.Presenters
open Campfire.Db
open Campfire.Routes
open Campfire.Views.Templates.Welcome

module Welcome =
    let private showSize = Campfire.Views.RenderSize()

    /// To the last room visited, or a page saying there are no rooms yet.
    let show (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            let! user = Concerns.requireCurrentUser c
            let! anyRooms = c.App.Read(fun conn -> not (List.isEmpty (Room.forUser conn user.Id)))
            if anyRooms then
                // `redirect_to room_url(last_room_visited)`
                let! (lastRoom: Room option) = Concerns.lastRoomVisited c
                match lastRoom with
                | Some room -> return! c.RedirectTo(c.UrlFor(Routes.room room.Id))
                | None -> return! Error(Internal(exn "no last room"))
            else
                do! c.RespondTo [ Format.Html ] |> Result.map ignore
                return!
                    Page.framedPage
                        c
                        Status.Ok
                        showSize
                        (fun w ctx -> Show.render w ctx user.Name)
                        ignore
                        (fun w ctx -> Show.content w ctx user.Name)
        }
