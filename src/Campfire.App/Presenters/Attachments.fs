// Port of `attached_blob` in rust/crates/campfire/src/controllers/presenters/attachments.rs
//
// `has_one_attached` as `User::Avatar` (`:avatar`) and `Account` (`:logo`) use it, over
// `Campfire.Storage`. The rest of the file (assigning an uploaded file inside a record's save) follows
// with the controllers that need it.
namespace Campfire.App.Presenters

open Campfire.Db

module Attachments =
    /// `record.<name>.attached?`'s blob: the attachment's blob, if any.
    let attachedBlob (conn: Conn) (recordType: string) (recordId: int64) (name: string) : Campfire.Storage.Blob option =
        Common.raiseStorage (Campfire.Storage.Blob.attached conn.Raw recordType recordId name)
