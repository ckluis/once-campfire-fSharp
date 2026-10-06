// Port of rust/crates/views/templates/accounts/users/index.turbo_stream.html (reference/app/views/accounts/users/index.turbo_stream.erb)
/// `accounts/users/index.turbo_stream.erb`: the next page of users, and the container for the page after it.
module Campfire.Views.Templates.Accounts.Users.IndexTurboStream

open Campfire.Views
open Campfire.Views.Users

let private t0 = Utf8.lit "<turbo-stream action=\"replace\" target=\"next_page_container\"><template>"
let private t1 = Utf8.lit "</template></turbo-stream>\n\n"
let private t2 = Utf8.lit "<turbo-stream action=\"append\" target=\"account_users\"><template>"
let private t3 = Utf8.lit "</template></turbo-stream>\n"

let render (w: Out) (ctx: ViewContext) (users: UserSummary list) (nextPage: string option) : unit =
    w.Lit t0
    for user in users do
        _User.render w ctx user
    w.Lit t1
    match nextPage with
    | Some page ->
        w.Lit t2
        _NextPageContainer.render w page
        w.Lit t3
    | None -> ()
