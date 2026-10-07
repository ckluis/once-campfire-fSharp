// Port of rust/crates/db/src/events.rs
namespace Campfire.Db

/// Side effects that leave the database. Models emit these at the point Rails performs
/// them; the caller supplies the `EventSink` that turns them into jobs, cable
/// disconnects and so on. The sink runs on the writer thread, so it must hand work off
/// (push onto a queue) rather than do it inline.
type Event =
    /// `Room::PushMessageJob.perform_later(room, message)`, from `Room#receive` after a
    /// message's create commits (`reference/app/models/room.rb`).
    | PushMessage of roomId: int64 * messageId: int64
    /// `ActionCable.server.remote_connections.where(current_user: user).disconnect(reconnect:)`
    /// (`reference/app/models/user.rb`). `reconnect: true` comes from
    /// `reset_remote_connections` (membership destroyed, sign out); `false` from
    /// deactivate and ban, which emit it inside their transaction, before anything is deleted.
    | DisconnectUser of userId: int64 * reconnect: bool
    /// `RemoveBannedContentJob.perform_later(user)` (`User::Bannable#apply_ban`).
    | RemoveBannedContent of userId: int64
    /// `Bot::WebhookJob.perform_later(bot, message)` (`User::Bot#deliver_webhook_later`).
    | DeliverWebhook of botId: int64 * messageId: int64
    /// `ActiveStorage::Blob#purge_later`: the blob lost its attachment when its record was
    /// destroyed (`has_one_attached` defaults to `dependent: :purge_later`).
    | PurgeBlob of blobId: int64

type EventSink =
    abstract Emit: Event -> unit
