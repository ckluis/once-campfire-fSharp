// Port of rust/crates/db/src/error.rs
namespace Campfire.Db

open System
open Microsoft.Data.Sqlite

/// `ActiveModel::Errors`: attribute/message pairs in the order they were added.
type Errors = { Items: (string * string) list }

module Errors =
    let empty : Errors = { Items = [] }

    let add (attribute: string) (message: string) (errors: Errors) : Errors =
        { Items = errors.Items @ [ attribute, message ] }

    let isEmpty (errors: Errors) : bool = List.isEmpty errors.Items

    let on (attribute: string) (errors: Errors) : string list =
        errors.Items |> List.filter (fun (a, _) -> a = attribute) |> List.map snd

    let private humanize (attribute: string) : string =
        let mutable trimmed = attribute
        while trimmed.EndsWith("_id", StringComparison.Ordinal) do
            trimmed <- trimmed.Substring(0, trimmed.Length - 3)
        let words = trimmed.Replace('_', ' ')
        if words.Length = 0 then "" else string (Char.ToUpperInvariant words[0]) + words.Substring 1

    /// `errors.full_messages`: "Endpoint must use HTTPS"
    let fullMessages (errors: Errors) : string list =
        errors.Items |> List.map (fun (attribute, message) -> $"{humanize attribute} {message}")

    let display (errors: Errors) : string = String.Join(", ", fullMessages errors)

type DbError =
    | Sqlite of SqliteException
    /// `ActiveRecord::RecordNotFound`
    | RecordNotFound of model: string
    /// `ActiveRecord::RecordInvalid`
    | RecordInvalid of Errors
    | WriterGone
    /// Any other failure, kept as its own type so that its source chain survives.
    | Other of exn

module DbError =
    /// Like `std::io::Error::other`: wraps any exception, or a message.
    let other (message: string) : DbError = Other(Exception message)

    let display (error: DbError) : string =
        match error with
        | Sqlite e -> e.Message
        | RecordNotFound model -> $"Couldn't find {model}"
        | RecordInvalid errors -> $"Validation failed: {Errors.display errors}"
        | WriterGone -> "database writer is gone"
        | Other e -> e.Message

    [<Literal>]
    let private SqliteConstraintUnique = 2067

    [<Literal>]
    let private SqliteConstraintPrimaryKey = 1555

    /// `ActiveRecord::RecordNotUnique`: a unique index refused the write.
    let isRecordNotUnique (error: DbError) : bool =
        match error with
        | Sqlite e -> e.SqliteExtendedErrorCode = SqliteConstraintUnique || e.SqliteExtendedErrorCode = SqliteConstraintPrimaryKey
        | _ -> false

/// How a model raises one of its `Error`s inside a read or a write (the Rust `?`). `Database`
/// returns it as an `Error` result at the boundary, and a write that raises one rolls back.
exception DbException of DbError with
    override this.Message = DbError.display this.Data0

module Err =
    let fail (error: DbError) : 'a = raise (DbException error)

    /// `Option#or_not_found`
    let orNotFound (model: string) (value: 'a option) : 'a =
        match value with
        | Some v -> v
        | None -> fail (RecordNotFound model)

    /// `Errors::into_result`: raises `RecordInvalid` unless there are no errors.
    let validate (errors: Errors) : unit =
        if not (Errors.isEmpty errors) then fail (RecordInvalid errors)
