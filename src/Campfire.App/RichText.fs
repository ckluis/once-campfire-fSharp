// Port of rust/crates/campfire/src/rich_text.rs
//
// `Campfire.Db.RichText` over `Campfire.RichText`, for the models' Action Text needs
// (`plain_text_body` for FTS, push and webhooks; `mentionees`).
//
// The models call this on the database writer thread inside their transaction, or on a reader they
// hold. Record lookups use that same connection with the one resolver implementation the controllers
// use (`Presenters.DbResolver`): checking out another connection here deadlocks once every pooled
// reader is waiting on the writer.
namespace Campfire.App

open Microsoft.Extensions.Logging
open Campfire.App.Presenters
open Campfire.Db
open Campfire.RailsCompat
open Campfire.RailsCompat.Clock
open Campfire.RichText

type AppRichText(secrets: Secrets, clock: SharedClock, logger: ILogger) =
    member private _.WithContext(conn: Conn, f: RenderContext -> 'T) : 'T =
        let resolver = DbResolver(conn, secrets, clock.Now())
        f (resolver.RenderContext None)

    interface Campfire.Db.RichText with
        /// `message.body.to_plain_text`. Where it raises, Rails fails the save after its commit; here
        /// it's logged and the body has no plain text (see "Known differences" in the README).
        member this.ToPlainText(conn: Conn, html: string) : string =
            match this.WithContext(conn, fun ctx -> ActionText.toPlainText html ctx) with
            | Ok text -> text
            | Error error ->
                logger.LogError("to_plain_text raised error={Error}", error.Message)
                ""

        /// `body.attachables.grep(User).uniq`: verified SGIDs only.
        member this.MentionedUserIds(conn: Conn, html: string) : int64 list =
            match this.WithContext(conn, fun ctx -> ActionText.mentionedUsers html ctx) with
            | Ok users -> users |> List.map (fun user -> user.Id)
            | Error error ->
                logger.LogError("mentioned_users raised error={Error}", error.Message)
                []
