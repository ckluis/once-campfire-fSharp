// Port of rust/crates/campfire/src/controllers/presenters/rich_text.rs
//
// Action Text's record lookups for `Campfire.RichText`, backed by the database: mention
// attachments resolve to users through their signed (or, for users only, unverified) GlobalIDs
// (`reference/lib/rails_ext/action_text_attachables.rb`).
namespace Campfire.App.Presenters

open System
open System.Globalization
open Campfire.Db
open Campfire.RailsCompat
open Campfire.RichText
open Campfire.Routes

type DbResolver(conn: Conn, secrets: Secrets, now: Timestamp) =
    member private _.MentionUser(user: User) : MentionUser =
        { Id = user.Id
          Name = user.Name
          Title = User.title user
          AttachableSgid = GlobalId.attachableSgid secrets (GlobalId.create "User" (string user.Id))
          UserPath = Routes.user user.Id
          AvatarPath = Common.avatarPath secrets user }

    /// `GlobalID::Locator` finds records by the model's primary key; ids that aren't integers
    /// never match a row.
    member private _.FindUser(id: string) : User option =
        match Int64.TryParse(id, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
        | true, id ->
            try
                User.findById conn id
            with DbException _ ->
                None
        | _ -> None

    member this.RenderContext(requestHost: string option) : RenderContext =
        { Resolver = this :> IAttachableResolver
          RequestHost = requestHost }

    interface IAttachableResolver with
        member this.LocateSigned(sgid: string) : SignedLookup =
            match GlobalId.locateSigned secrets sgid GlobalId.AttachablePurpose now with
            | None -> SignedLookup.Invalid
            | Some gid ->
                match gid.ModelName with
                | "User" ->
                    match this.FindUser gid.Id with
                    | Some user -> SignedLookup.User(this.MentionUser user)
                    | None -> SignedLookup.MissingRecord gid.ModelName
                | _ -> SignedLookup.MissingRecord gid.ModelName

        member this.FindGid(gid: string) : GidLookup =
            match GlobalId.parse gid with
            | None -> GidLookup.NotFound
            | Some gid ->
                match gid.ModelName with
                | "User" ->
                    match this.FindUser gid.Id with
                    | Some user -> GidLookup.User(this.MentionUser user)
                    | None -> GidLookup.NotFound
                | "Message"
                | "Room"
                | "Rooms::Open"
                | "Rooms::Closed"
                | "Rooms::Direct"
                | "Boost"
                | "Account" -> GidLookup.OtherModel
                | _ -> GidLookup.Raises
