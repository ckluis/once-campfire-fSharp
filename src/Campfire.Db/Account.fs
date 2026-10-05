// Port of rust/crates/db/src/models/account.rs
// `reference/app/models/account.rb` and `account/joinable.rb`
namespace Campfire.Db

open Campfire.RailsCompat
// RailsCompat has a `Timestamp` of its own (an alias of DateTimeOffset); ours is the one models use.
open Campfire.Db

type Account =
    { Id: int64
      Name: string
      JoinCode: string
      CustomStyles: string option
      /// The raw `settings` JSON column; read it through `Account.settings`.
      SettingsJson: string option
      SingletonGuard: int64
      CreatedAt: Timestamp
      UpdatedAt: Timestamp }

/// `has_json :settings, restrict_room_creation_to_administrators: false`
/// (`ActiveModel::SchematizedJson`): the stored hash with the schema defaults merged in.
type AccountSettings = { Data: (string * Value) list }

module AccountSettings =
    [<Literal>]
    let private RestrictRoomCreation = "restrict_room_creation_to_administrators"

    let private set (key: string) (value: Value) (settings: AccountSettings) : AccountSettings =
        if settings.Data |> List.exists (fun (k, _) -> k = key) then
            { Data = settings.Data |> List.map (fun (k, v) -> if k = key then k, value else k, v) }
        else
            { Data = settings.Data @ [ key, value ] }

    let internal fromColumn (raw: string option) : AccountSettings =
        let data =
            raw
            |> Option.bind (fun r -> Json.parse (System.Text.Encoding.UTF8.GetBytes r))
            |> Option.bind (fun v -> v.AsObject)
            |> Option.defaultValue []
        if data |> List.exists (fun (k, _) -> k = RestrictRoomCreation) then
            { Data = data }
        else
            { Data = data @ [ RestrictRoomCreation, Value.Bool false ] }

    let private isPresent (value: Value option) : bool =
        match value with
        | None
        | Some Value.Null
        | Some(Value.Bool false) -> false
        | Some(Value.String s) -> not (System.String.IsNullOrWhiteSpace s)
        | Some(Value.Array a) -> not (List.isEmpty a)
        | Some(Value.Object o) -> not (List.isEmpty o)
        | Some _ -> true

    /// `ActiveModel::Type::Boolean#cast` of a form value: blank is nil, the `FALSE_VALUES` are
    /// false, anything else is true.
    let castBoolean (value: string) : bool option =
        let falseValues = [ "0"; "f"; "F"; "false"; "FALSE"; "off"; "OFF" ]
        if value = "" then None else Some(not (List.contains value falseValues))

    /// `restrict_room_creation_to_administrators?`: `present?` of the stored value.
    let restrictRoomCreationToAdministrators (settings: AccountSettings) : bool =
        isPresent (settings.Data |> List.tryFind (fun (k, _) -> k = RestrictRoomCreation) |> Option.map snd)

    /// `restrict_room_creation_to_administrators = value`, cast as a boolean.
    let setRestrictRoomCreationToAdministrators (value: string) (settings: AccountSettings) : AccountSettings =
        let cast =
            match castBoolean value with
            | Some b -> Value.Bool b
            | None -> Value.Null
        set RestrictRoomCreation cast settings

    /// `assign_data_with_type_casting`: every key must be in the schema.
    let assign (values: (string * string) list) (settings: AccountSettings) : AccountSettings =
        values
        |> List.fold
            (fun settings (key, value) ->
                match key with
                | RestrictRoomCreation -> setRestrictRoomCreationToAdministrators value settings
                | other -> Err.fail (DbError.other $"undefined method '{other}=' for account settings"))
            settings

    let get (key: string) (settings: AccountSettings) : Value option =
        settings.Data |> List.tryFind (fun (k, _) -> k = key) |> Option.map snd

    let toJson (settings: AccountSettings) : string = Json.generate (Value.Object settings.Data)

module Account =
    [<Literal>]
    let private select = "SELECT " + Columns.Account + " FROM \"accounts\""

    let private fromRow (r: Row) : Account =
        { Id = r.Int64 0
          Name = r.Text 1
          JoinCode = r.Text 2
          CustomStyles = r.OptText 3
          SettingsJson = r.OptText 4
          SingletonGuard = r.Int64 5
          CreatedAt = r.Timestamp 6
          UpdatedAt = r.Timestamp 7 }

    /// `Account.first` (`Current.account`).
    let first (conn: Conn) : Account option =
        conn.QueryOne(select + " ORDER BY \"accounts\".\"id\" ASC LIMIT 1", [||], fromRow)

    let find (conn: Conn) (id: int64) : Account =
        conn.QueryOne(select + " WHERE \"accounts\".\"id\" = ? LIMIT 1", [| I id |], fromRow) |> Err.orNotFound "Account"

    let count (conn: Conn) : int64 = conn.Count("""SELECT COUNT(*) FROM "accounts" """, [||])

    let settings (account: Account) : AccountSettings = AccountSettings.fromColumn account.SettingsJson

    /// `SecureRandom.alphanumeric(12).scan(/.{4}/).join("-")`
    let generateJoinCode () : string =
        let code = Sql.alphanumeric 12
        $"{code.Substring(0, 4)}-{code.Substring(4, 4)}-{code.Substring(8, 4)}"

    /// `Account.create!(name:)`: a fresh join code, and the settings defaults written out.
    let create (tx: Tx) (name: string) : Account =
        let now = tx.Now()
        let joinCode = generateJoinCode ()
        let settings = AccountSettings.toJson (AccountSettings.fromColumn None)
        let id =
            tx.Conn.QueryRow(
                """INSERT INTO "accounts" ("created_at", "custom_styles", "join_code", "name", "settings", "singleton_guard", "updated_at") VALUES (?, ?, ?, ?, ?, ?, ?) RETURNING "id" """,
                [| T now; Null; S joinCode; S name; S settings; I 0L; T now |],
                fun r -> r.Int64 0
            )
        find tx.Conn id

    /// `reset_join_code`
    let resetJoinCode (tx: Tx) (account: Account) : Account =
        let joinCode = generateJoinCode ()
        let now = tx.Now()
        tx.Conn.Execute(
            """UPDATE "accounts" SET "join_code" = ?, "updated_at" = ? WHERE "accounts"."id" = ?""",
            [| S joinCode; T now; I account.Id |]
        )
        |> ignore
        { account with JoinCode = joinCode; UpdatedAt = now }

    /// `update!(name:, custom_styles:, settings:)`. Only changed attributes are written; the
    /// settings column is written when its (defaulted) hash changes.
    let update
        (tx: Tx)
        (account: Account)
        (name: string option)
        (customStyles: string option option)
        (settings: (string * string) list option)
        : Account =
        let mutable updated = account
        let sets = ResizeArray<string * SqlArg>()
        match name with
        | Some n when n <> updated.Name ->
            updated <- { updated with Name = n }
            sets.Add("name", S n)
        | _ -> ()
        match customStyles with
        | Some styles when styles <> updated.CustomStyles ->
            updated <- { updated with CustomStyles = styles }
            sets.Add("custom_styles", Sql.optS styles)
        | _ -> ()
        match settings with
        | Some values ->
            let original = AccountSettings.fromColumn updated.SettingsJson
            let assigned = AccountSettings.assign values original
            if updated.SettingsJson.IsNone || assigned <> original then
                let json = AccountSettings.toJson assigned
                updated <- { updated with SettingsJson = Some json }
                sets.Add("settings", S json)
        | None -> ()
        if sets.Count = 0 then
            account
        else
            let now = tx.Now()
            updated <- { updated with UpdatedAt = now }
            sets.Add("updated_at", T now)
            let assignments = sets |> Seq.map (fun (c, _) -> $"\"{c}\" = ?") |> String.concat ", "
            let sql = $"UPDATE \"accounts\" SET {assignments} WHERE \"accounts\".\"id\" = ?"
            tx.Conn.Execute(sql, Array.append (sets |> Seq.map snd |> Array.ofSeq) [| I account.Id |]) |> ignore
            updated
