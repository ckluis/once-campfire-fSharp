// Port of rust/crates/views/templates/users/sidebars/rooms/_shared.html (reference/app/views/users/sidebars/rooms/_shared.html.erb)
/// `users/sidebars/rooms/_shared.html.erb` on its own (broadcast, and rendered by the rooms controllers).
module Campfire.Views.Templates.Users.Sidebars.Rooms._Shared

open Campfire.Views
open Campfire.Views.Helpers
open Campfire.Views.Users

let private t0 = Utf8.lit "\n  <span class=\"overflow-ellipsis\">"
let private t1 = Utf8.lit "</span>\n"

let render (w: Out) (room: SidebarRoom) : unit =
    Filters.linkToRoom
        w
        room.Id
        (Tag.attrs()
            .Id(Turbo.domIdValue room.ParamKey room.Id (Some "list"))
            .Data("sorted_list_name", room.Name)
            .Style("--column-gap: 0.5em")
            .Class(room.ClassNames))
        (fun w ->
            w.Lit t0
            w.Text room.Name
            w.Lit t1)
