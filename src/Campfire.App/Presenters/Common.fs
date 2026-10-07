// Port of the helpers at the top of rust/crates/campfire/src/controllers/presenters.rs that the layout,
// the rich text resolver and the controllers' shared code need (`user_summary`, `epoch_string`, the
// avatar paths; the presenter that maps rows to the views' models, `Presenter`, follows with the
// message controllers), `storage_error` of rust/crates/campfire/src/active_storage.rs, which every
// attachment lookup needs, and `touch` of presenters/accounts.rs, which `Attachments` calls (F# files
// are ordered, and `Accounts` reads attachments).
namespace Campfire.App.Presenters

open System
open Microsoft.Data.Sqlite
open Campfire.Db
open Campfire.RailsCompat
open Campfire.Routes
open Campfire.Views.Users

module Common =
    /// `Time#to_fs(:number)`: `%Y%m%d%H%M%S` in UTC (the app's time zone).
    let toFsNumber (time: Campfire.Db.Timestamp) : string =
        time.ToDateTimeOffset().UtcDateTime.ToString("yyyyMMddHHmmss", Globalization.CultureInfo.InvariantCulture)

    /// `user.avatar_token`: `signed_id(purpose: :avatar)`.
    let avatarToken (secrets: Secrets) (userId: int64) : string = SignedId.generate secrets "User" userId (Some "avatar") None

    /// `fresh_user_avatar_path(user)`.
    let avatarPath (secrets: Secrets) (user: User) : string =
        Routes.freshUserAvatar (avatarToken secrets user.Id) (toFsNumber user.UpdatedAt)

    /// A storage error inside a database closure. SQLite's own errors stay `Sqlite`, so that
    /// `DbError.isRecordNotUnique` sees them. (Neither project depends on the other, so this can't be
    /// a conversion in either.)
    let storageError (error: Campfire.Storage.StorageError) : DbError =
        match error with
        | Campfire.Storage.StorageError.Sql(:? SqliteException as e) -> Sqlite e
        | Campfire.Storage.StorageError.Sql e -> Other e
        | other -> Other(Exception(Campfire.Storage.StorageError.message other))

    /// `storageError` as the exception a model raises inside a read or write.
    let raiseStorage (result: Campfire.Storage.StorageResult<'T>) : 'T =
        match result with
        | Ok value -> value
        | Error error -> Err.fail (storageError error)

    /// `to_fs(:epoch)` as a string (milliseconds).
    let epochString (time: Campfire.Db.Timestamp) : string =
        string (Campfire.Views.MessagesSupport.epochMs (time.ToDateTimeOffset()))

    /// A `User` row as the users views see it.
    let userSummary (secrets: Secrets) (user: User) : UserSummary =
        { Id = user.Id
          Name = user.Name
          Bio = user.Bio
          EmailAddress = user.EmailAddress
          Role =
            (match user.Role with
             | Campfire.Db.Role.Member -> Campfire.Views.Users.Role.Member
             | Campfire.Db.Role.Administrator -> Campfire.Views.Users.Role.Administrator
             | Campfire.Db.Role.Bot -> Campfire.Views.Users.Role.Bot
             | other -> failwith $"role {int other} out of range")
          Status =
            (match user.Status with
             | Campfire.Db.Status.Active -> Campfire.Views.Users.Status.Active
             | Campfire.Db.Status.Deactivated -> Campfire.Views.Users.Status.Deactivated
             | Campfire.Db.Status.Banned -> Campfire.Views.Users.Status.Banned
             | other -> failwith $"status {int other} out of range")
          AvatarPath = avatarPath secrets user }

    /// `record.touch`: bumps `updated_at` (what `belongs_to :record, touch: true` does to an
    /// attachment's record, and `Blob#touch_attachments` after analysis).
    let touch (conn: Conn) (table: string) (id: int64) (now: Campfire.Db.Timestamp) : unit =
        conn.Execute($"UPDATE \"{table}\" SET \"updated_at\" = ? WHERE \"{table}\".\"id\" = ?", [| T now; I id |]) |> ignore
