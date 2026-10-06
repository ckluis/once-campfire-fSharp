// Port of the helpers at the top of rust/crates/campfire/src/controllers/presenters.rs that the layout,
// the rich text resolver and the controllers' shared code need (the presenter that maps rows to the
// views' models, `Presenter`, follows with the controllers unit), and `storage_error` of
// rust/crates/campfire/src/active_storage.rs, which every attachment lookup needs.
namespace Campfire.App.Presenters

open System
open Microsoft.Data.Sqlite
open Campfire.Db
open Campfire.RailsCompat
open Campfire.Routes

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
