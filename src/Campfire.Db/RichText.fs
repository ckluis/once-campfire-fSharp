// Port of rust/crates/db/src/rich_text.rs
namespace Campfire.Db

/// What the models need from Action Text. The real pipeline lives in `Campfire.RichText`;
/// the app plugs it in through `RichText`. Tests have a stand-in, `Testing.BasicRichText`.
///
/// Both methods get the connection the caller is already on (the writer's transaction, or the
/// reader it holds) for any record lookups: they must never check out another connection, which
/// deadlocks once every pooled reader waits on the writer.
type RichText =
    /// `ActionText::Content#to_plain_text` of a stored body. Mention attachments render as
    /// `attachable_plain_text_representation`, i.e. `"@#{name}"`
    /// (`reference/app/models/user/mentionable.rb`).
    abstract ToPlainText: conn: Conn * html: string -> string

    /// `body.attachables.grep(User).uniq`: user ids from mention attachments, in document
    /// order, deduplicated (`reference/app/models/message/mentionee.rb`).
    abstract MentionedUserIds: conn: Conn * html: string -> int64 list
