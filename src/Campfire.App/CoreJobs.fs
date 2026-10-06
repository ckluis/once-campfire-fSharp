// Port of `Registry::with_core_jobs` and its two handlers in rust/crates/campfire/src/jobs.rs
//
// The jobs the app core performs itself. They need the app, which owns the `Jobs` that enqueue them,
// so they live apart from the runner (`Jobs.fs`).
namespace Campfire.App

open System.Threading.Tasks
open Campfire.Db

module CoreJobs =
    /// `RemoveBannedContentJob`: `user.remove_banned_content`, which destroys each of the user's
    /// messages (each in its own transaction) and broadcasts its removal
    /// (`reference/app/models/user/bannable.rb`, `Message::Broadcasts#broadcast_remove`).
    let removeBannedContent (app: AppState) (event: Event) : Task<Result<unit, string>> =
        task {
            match event with
            | Event.RemoveBannedContent userId ->
                // Offloaded: it reads every message the user wrote.
                match! app.Db.ReadOffloaded(fun conn -> Message.byCreator conn userId) with
                | Error error -> return Error(DbError.display error)
                | Ok messages ->
                    let mutable failure: string option = None
                    for message in messages do
                        if failure.IsNone then
                            match! app.Db.Write(fun tx -> Message.destroy tx message) with
                            | Error error -> failure <- Some(DbError.display error)
                            | Ok() ->
                                match! app.Db.Read(fun conn -> Room.find conn message.RoomId) with
                                | Error error -> failure <- Some(DbError.display error)
                                | Ok room -> app.Broadcasts.MessageRemove(room, message)
                    return match failure with Some failure -> Error failure | None -> Ok()
            | _ -> return Ok()
        }

    /// `ActiveStorage::PurgeJob`
    let purgeBlob (app: AppState) (event: Event) : Task<Result<unit, string>> =
        match event with
        | Event.PurgeBlob blobId -> ActiveStorage.purge app blobId
        | _ -> Task.FromResult(Ok())

    /// The jobs the app core performs itself.
    let withCoreJobs () : Registry<AppState> =
        let registry = Registry<AppState>()
        registry.Handle(JobKind.RemoveBannedContent, removeBannedContent)
        registry.Handle(JobKind.PurgeBlob, purgeBlob)
        registry
