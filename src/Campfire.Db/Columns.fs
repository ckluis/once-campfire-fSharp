// Port of the `columns!` lists in rust/crates/db/src/sql.rs and models/*.rs
namespace Campfire.Db

/// Every model is read by position from one list of columns, as SQL (`"table"."column", ...`):
/// reading by name costs a scan of the statement's column names for every field of every row
/// (bench/results/columns-20260930). A query must select its model's list, never `*`: databases
/// Rails migrated have their columns in another order than ones it loaded from `schema.rb`
/// (see `Schema`).
module Columns =
    /// users, in the order `User.fromRow` reads them.
    [<Literal>]
    let User = "\"users\".\"id\", \"users\".\"name\", \"users\".\"email_address\", \"users\".\"password_digest\", \"users\".\"role\", \"users\".\"status\", \"users\".\"bio\", \"users\".\"bot_token\", \"users\".\"created_at\", \"users\".\"updated_at\""

    /// messages, in the order `Message.fromRow` reads them.
    [<Literal>]
    let Message = "\"messages\".\"id\", \"messages\".\"room_id\", \"messages\".\"creator_id\", \"messages\".\"client_message_id\", \"messages\".\"created_at\", \"messages\".\"updated_at\""

    /// rooms, in the order `Room.fromRow` reads them.
    [<Literal>]
    let Room = "\"rooms\".\"id\", \"rooms\".\"name\", \"rooms\".\"type\", \"rooms\".\"creator_id\", \"rooms\".\"created_at\", \"rooms\".\"updated_at\""

    /// memberships, in the order `Membership.fromRow` reads them.
    [<Literal>]
    let Membership = "\"memberships\".\"id\", \"memberships\".\"room_id\", \"memberships\".\"user_id\", \"memberships\".\"involvement\", \"memberships\".\"unread_at\", \"memberships\".\"connected_at\", \"memberships\".\"connections\", \"memberships\".\"created_at\", \"memberships\".\"updated_at\""

    /// accounts, in the order `Account.fromRow` reads them.
    [<Literal>]
    let Account = "\"accounts\".\"id\", \"accounts\".\"name\", \"accounts\".\"join_code\", \"accounts\".\"custom_styles\", \"accounts\".\"settings\", \"accounts\".\"singleton_guard\", \"accounts\".\"created_at\", \"accounts\".\"updated_at\""

    /// boosts, in the order `Boost.fromRow` reads them.
    [<Literal>]
    let Boost = "\"boosts\".\"id\", \"boosts\".\"message_id\", \"boosts\".\"booster_id\", \"boosts\".\"content\", \"boosts\".\"created_at\", \"boosts\".\"updated_at\""

    /// action_text_rich_texts, in the order `RichTextRecord.fromRow` reads them.
    [<Literal>]
    let RichTextRecord = "\"action_text_rich_texts\".\"id\", \"action_text_rich_texts\".\"name\", \"action_text_rich_texts\".\"body\", \"action_text_rich_texts\".\"record_type\", \"action_text_rich_texts\".\"record_id\", \"action_text_rich_texts\".\"created_at\", \"action_text_rich_texts\".\"updated_at\""

    /// sessions, in the order `Session.fromRow` reads them.
    [<Literal>]
    let Session = "\"sessions\".\"id\", \"sessions\".\"user_id\", \"sessions\".\"token\", \"sessions\".\"ip_address\", \"sessions\".\"user_agent\", \"sessions\".\"last_active_at\", \"sessions\".\"created_at\", \"sessions\".\"updated_at\""

    /// active_storage_blobs, in the order `Blob.fromRow` reads them.
    [<Literal>]
    let Blob = "\"active_storage_blobs\".\"id\", \"active_storage_blobs\".\"key\", \"active_storage_blobs\".\"filename\", \"active_storage_blobs\".\"content_type\", \"active_storage_blobs\".\"metadata\", \"active_storage_blobs\".\"service_name\", \"active_storage_blobs\".\"byte_size\", \"active_storage_blobs\".\"checksum\", \"active_storage_blobs\".\"created_at\""

    /// active_storage_attachments, in the order `Attachment.fromRow` reads them.
    [<Literal>]
    let Attachment = "\"active_storage_attachments\".\"id\", \"active_storage_attachments\".\"name\", \"active_storage_attachments\".\"record_type\", \"active_storage_attachments\".\"record_id\", \"active_storage_attachments\".\"blob_id\", \"active_storage_attachments\".\"created_at\""

    /// bans, in the order `Ban.fromRow` reads them.
    [<Literal>]
    let Ban = "\"bans\".\"id\", \"bans\".\"user_id\", \"bans\".\"ip_address\", \"bans\".\"created_at\", \"bans\".\"updated_at\""

    /// push_subscriptions, in the order `PushSubscription.fromRow` reads them.
    [<Literal>]
    let PushSubscription = "\"push_subscriptions\".\"id\", \"push_subscriptions\".\"user_id\", \"push_subscriptions\".\"endpoint\", \"push_subscriptions\".\"p256dh_key\", \"push_subscriptions\".\"auth_key\", \"push_subscriptions\".\"user_agent\", \"push_subscriptions\".\"created_at\", \"push_subscriptions\".\"updated_at\""

    /// searches, in the order `Search.fromRow` reads them.
    [<Literal>]
    let Search = "\"searches\".\"id\", \"searches\".\"user_id\", \"searches\".\"query\", \"searches\".\"created_at\", \"searches\".\"updated_at\""

    /// webhooks, in the order `Webhook.fromRow` reads them.
    [<Literal>]
    let Webhook = "\"webhooks\".\"id\", \"webhooks\".\"user_id\", \"webhooks\".\"url\", \"webhooks\".\"created_at\", \"webhooks\".\"updated_at\""

    /// The columns' names, for checking a row against its list.
    let names (columns: string) : string list =
        columns.Split(", ") |> Array.map (fun c -> c.Substring(c.LastIndexOf('.') + 2).TrimEnd('"')) |> List.ofArray

/// `SELECT` and a model's columns, `FROM` its table: what a query for that model starts with, as a
/// literal so that every query built on it is a constant.
module Selects =
    [<Literal>]
    let User = "SELECT " + Columns.User + " FROM \"users\""

    [<Literal>]
    let Message = "SELECT " + Columns.Message + " FROM \"messages\""

    [<Literal>]
    let Room = "SELECT " + Columns.Room + " FROM \"rooms\""

    [<Literal>]
    let Membership = "SELECT " + Columns.Membership + " FROM \"memberships\""

    [<Literal>]
    let Boost = "SELECT " + Columns.Boost + " FROM \"boosts\""

    [<Literal>]
    let Webhook = "SELECT " + Columns.Webhook + " FROM \"webhooks\""
