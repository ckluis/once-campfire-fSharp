// Port of the parts of rust/crates/campfire/src/controllers/presenters/accounts.rs the sign-in page,
// the layout and the concerns need (the rest, the view models of the account, user and bot screens,
// follows with the controllers unit).
namespace Campfire.App.Presenters

open Campfire.App
open Campfire.Db
open Campfire.Kit
open Campfire.Routes
open Campfire.Views
open Campfire.Views.Accounts

module Accounts =
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
