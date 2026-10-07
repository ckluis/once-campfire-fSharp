// Port of rust/crates/views/src/autocompletable.rs (the view-model and JSON; the templates are
// modules under Templates/Autocompletable)
/// Views for `reference/app/views/autocompletable`.
module Campfire.Views.Autocompletable

open Campfire.RailsCompat
open Campfire.Views.Helpers
open Campfire.Views.Users

/// `autocompletable/users/_user.json.jbuilder`; `index.json.jbuilder` is a JSON array of these.
type UserJson =
    { /// `h(user.name)`: HTML-escaped.
      Name: string
      Value: int64
      /// `fresh_user_avatar_url(user)`: absolute.
      AvatarUrl: string
      Sgid: string }

module UserJson =
    let create (user: MentionUser) (baseUrl: string) : UserJson =
        { Name = Tag.escape user.User.Name
          Value = user.User.Id
          AvatarUrl = baseUrl + user.User.AvatarPath
          Sgid = user.AttachableSgid }

    let toValue (user: UserJson) : Value =
        Value.Object
            [ "name", Value.String user.Name
              "value", Value.Int user.Value
              "avatar_url", Value.String user.AvatarUrl
              "sgid", Value.String user.Sgid ]

/// `autocompletable/users/index.json.jbuilder`.
let usersIndexJson (users: MentionUser list) (baseUrl: string) : string =
    Json.encode (Value.Array(users |> List.map (fun user -> UserJson.toValue (UserJson.create user baseUrl))))
