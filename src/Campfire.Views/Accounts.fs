// Port of rust/crates/views/src/accounts.rs (the view-models; the templates they feed are modules
// under Templates/Accounts)
/// Views for `reference/app/views/accounts`.
module Campfire.Views.Accounts

open Campfire.Routes
open Campfire.Views.Users

/// `User.administrator.first`, shown by `accounts/_help_contact`.
type HelpContact = { Name: string; EmailAddress: string }

/// What `accounts/edit` shows (`accounts::Edit`'s fields).
type EditView =
    { /// `Current.account.id`: `form_with model: @account` posts to `/account.<id>` because the
      /// account is a singular resource (a quirk the reference ships with).
      AccountId: int64
      JoinCode: string
      RestrictRoomCreationToAdministrators: bool
      Administrators: UserSummary list
      Members: UserSummary list
      /// `@page.next_param` unless `@page.last?`.
      NextPage: string option }

    member this.AccountAction: string = Routes.account () + "." + string this.AccountId

/// A bot's room (`room_display_name(room)`, the room's name for shared rooms).
type BotRoom = { Id: int64; Name: string }

/// A bot, as `accounts/bots/_bot` and `_form` see it.
type Bot =
    { User: UserSummary
      /// `User#bot_key`: "id-token".
      BotKey: string
      /// `bot.rooms.without_directs.ordered`.
      Rooms: BotRoom list }

/// The fields `accounts/bots/_form` fills in.
type BotForm =
    { Name: string option
      WebhookUrl: string option
      /// `url_for(bot.avatar)` when attached.
      AvatarAttachmentUrl: string option }

module BotForm =
    /// `BotForm::default()`.
    let empty: BotForm =
        { Name = None
          WebhookUrl = None
          AvatarAttachmentUrl = None }
