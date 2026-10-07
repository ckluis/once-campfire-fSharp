// Port of rust/crates/db/src/fixtures.rs
/// Loads `reference/test/fixtures/*.yml` the way `ActiveRecord::FixtureSet` does, so tests run
/// against the same rows as the Ruby tests:
///
/// - ids are `Zlib.crc32(label) % (2**30 - 1)` unless given;
/// - `belongs_to` values are labels (`creator: :david`), polymorphic ones name the class
///   (`record: first (Message)`);
/// - enum names become their stored values (`role: administrator` is 1);
/// - `created_at`/`updated_at` default to one `now` per fixture file;
/// - columns a fixture leaves out get the column default;
/// - each table is emptied, then filled, with foreign keys checked at commit.
///
/// ERB is evaluated for the forms the fixtures use: `<%= N.<unit>.ago %>`,
/// `BCrypt::Password.create("...")` assigned to a local, and `User.generate_bot_token`.
module Campfire.Db.Fixtures

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Text
open System.Text.RegularExpressions

/// `ActiveRecord::FixtureSet::MAX_ID`
[<Literal>]
let MaxId = 1073741823u // (1 << 30) - 1

let private crcTable : uint32[] =
    Array.init 256 (fun n ->
        let mutable c = uint32 n
        for _ in 0..7 do
            c <- if c &&& 1u <> 0u then 0xEDB88320u ^^^ (c >>> 1) else c >>> 1
        c)

/// `Zlib.crc32`
let crc32 (bytes: byte[]) : uint32 =
    let mutable c = 0xFFFFFFFFu
    for b in bytes do
        c <- crcTable[int ((c ^^^ uint32 b) &&& 0xFFu)] ^^^ (c >>> 8)
    c ^^^ 0xFFFFFFFFu

/// `ActiveRecord::FixtureSet.identify(label)`
let identify (label: string) : int64 = int64 (crc32 (Encoding.UTF8.GetBytes label) % MaxId)

/// The fixture directory in the reference checkout, found from the repository root.
let referenceDir () : string =
    let rec up (dir: DirectoryInfo | null) =
        match dir with
        | null -> failwith "reference/test/fixtures not found above the working directory"
        | dir ->
            let candidate = Path.Combine(dir.FullName, "reference", "test", "fixtures")
            if Directory.Exists candidate then candidate else up dir.Parent
    up (DirectoryInfo AppContext.BaseDirectory)

type Options =
    {
        /// Base time for `N.minutes.ago` and the default timestamps.
        Now: Timestamp
        /// BCrypt cost for `BCrypt::Password.create` (bcrypt-ruby's default is 12).
        BcryptCost: int
    }

/// What was loaded: table -> label -> id.
type Loaded = { Ids: Map<string, Map<string, int64>> }

module Loaded =
    let id (table: string) (label: string) (loaded: Loaded) : int64 option =
        loaded.Ids |> Map.tryFind table |> Option.bind (Map.tryFind label)

// The sliver of YAML the fixtures use: a mapping of labels to mappings of scalars.

type internal Yaml =
    | YNull
    | YBool of bool
    | YInt of int64
    | YFloat of float
    | YString of string

module internal YamlParser =
    let private unquoteDouble (text: string) : string =
        let out = StringBuilder()
        let mutable i = 1
        while i < text.Length - 1 do
            let c = text[i]
            if c = '\\' && i + 1 < text.Length - 1 then
                i <- i + 1
                match text[i] with
                | 'n' -> out.Append '\n' |> ignore
                | 't' -> out.Append '\t' |> ignore
                | 'r' -> out.Append '\r' |> ignore
                | '0' -> out.Append '\000' |> ignore
                | 'u' when i + 4 < text.Length ->
                    out.Append(char (Convert.ToInt32(text.Substring(i + 1, 4), 16))) |> ignore
                    i <- i + 4
                | other -> out.Append other |> ignore
            else
                out.Append c |> ignore
            i <- i + 1
        out.ToString()

    let private intPattern = Regex(@"^[-+]?[0-9]+$", RegexOptions.CultureInvariant)
    let private floatPattern = Regex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$", RegexOptions.CultureInvariant)

    let private scalar (raw: string) : Yaml =
        let text = raw.Trim()
        if text.Length >= 2 && text[0] = '"' && text[text.Length - 1] = '"' then
            YString(unquoteDouble text)
        elif text.Length >= 2 && text[0] = '\'' && text[text.Length - 1] = '\'' then
            YString(text.Substring(1, text.Length - 2).Replace("''", "'"))
        else
            // A comment starts at ` #`.
            let text =
                match text.IndexOf " #" with
                | -1 -> text
                | i -> text.Substring(0, i).TrimEnd()
            match text with
            | ""
            | "~"
            | "null"
            | "Null"
            | "NULL" -> YNull
            | "true"
            | "True"
            | "TRUE" -> YBool true
            | "false"
            | "False"
            | "FALSE" -> YBool false
            | _ when intPattern.IsMatch text ->
                match Int64.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
                | true, i -> YInt i
                | _ -> YString text
            | _ when floatPattern.IsMatch text -> YFloat(Double.Parse(text, CultureInfo.InvariantCulture))
            | _ -> YString text

    /// A file's rows, in order: label, then the attributes of the fixture in order.
    let parse (source: string) : (string * (string * Yaml) list) list =
        let rows = ResizeArray<string * ResizeArray<string * Yaml>>()
        for line in source.Split '\n' do
            let line = line.TrimEnd '\r'
            if line.Trim() = "" || line.TrimStart().StartsWith "#" then
                ()
            else
                let indented = Char.IsWhiteSpace line[0]
                let body = line.Trim()
                let key, value =
                    match body.IndexOf ": " with
                    | -1 ->
                        if body.EndsWith ":" then body.Substring(0, body.Length - 1), "" else failwith $"unsupported YAML line: {line}"
                    | i -> body.Substring(0, i), body.Substring(i + 2)
                let key =
                    if key.Length >= 2 && (key[0] = '"' || key[0] = '\'') then key.Substring(1, key.Length - 2) else key
                if indented then
                    if rows.Count = 0 then failwith $"attribute before a label: {line}"
                    (snd rows[rows.Count - 1]).Add(key, scalar value)
                else
                    rows.Add((key, ResizeArray()))
        [ for label, attributes in rows -> label, List.ofSeq attributes ]

/// A `belongs_to` in a fixture: the key, and the column it sets (or, for polymorphic
/// ones, the `_id`/`_type` pair).
type private Association =
    | BelongsTo of key: string * column: string
    | Polymorphic of key: string

let private associations (table: string) : Association list =
    match table with
    | "boosts" -> [ BelongsTo("message", "message_id"); BelongsTo("booster", "booster_id") ]
    | "memberships" -> [ BelongsTo("room", "room_id"); BelongsTo("user", "user_id") ]
    | "messages" -> [ BelongsTo("room", "room_id"); BelongsTo("creator", "creator_id") ]
    | "rooms" -> [ BelongsTo("creator", "creator_id") ]
    | "bans"
    | "searches"
    | "sessions"
    | "webhooks"
    | "push_subscriptions" -> [ BelongsTo("user", "user_id") ]
    | "action_text_rich_texts" -> [ Polymorphic "record" ]
    | "active_storage_attachments" -> [ Polymorphic "record"; BelongsTo("blob", "blob_id") ]
    | "active_storage_variant_records" -> [ BelongsTo("blob", "blob_id") ]
    | _ -> []

/// Enum attributes: name -> stored value.
let private resolveEnum (table: string) (column: string) (value: string) : SqlArg option =
    match table, column with
    | "users", "role" -> Role.fromName value |> Option.map Role.toSql
    | "users", "status" -> Status.fromName value |> Option.map Status.toSql
    | "memberships", "involvement" -> Involvement.fromName value |> Option.map (fun i -> S(Involvement.name i))
    | _ -> None

let private isDatetimeColumn (column: string) : bool = column.EndsWith "_at"

let private scalarString (value: Yaml) : string option =
    match value with
    | YString s -> Some s
    | YInt i -> Some(string i)
    | YBool b -> Some(if b then "true" else "false")
    | YFloat f -> Some(f.ToString(CultureInfo.InvariantCulture))
    | YNull -> None

let private yamlToSql (table: string) (column: string) (value: Yaml) : SqlArg =
    match value with
    | YNull -> Null
    | YBool b -> I(if b then 1L else 0L)
    | YInt i -> I i
    | YFloat f -> R f
    | YString s ->
        match resolveEnum table column s with
        | Some v -> v
        | None ->
            if isDatetimeColumn column then
                // Time columns cast the text (e.g. ERB's "2026-09-26 11:24:38 UTC").
                match Timestamp.ParseDb s with
                | Some ts -> S(ts.ToDb())
                | None -> S s
            else
                S s

/// `1.hour.ago`, `36.minutes.ago`, `2.days.ago`...
let private parseAgo (code: string) : TimeSpan option =
    match code.Split '.' with
    | [| n; unit; "ago" |] ->
        match Int64.TryParse(n.Trim()) with
        | true, n ->
            let seconds =
                match unit.TrimEnd 's' with
                | "second" -> Some 1L
                | "minute" -> Some 60L
                | "hour" -> Some 3600L
                | "day" -> Some 86_400L
                | "week" -> Some 604_800L
                | _ -> None
            seconds |> Option.map (fun s -> TimeSpan.FromSeconds(float (n * s)))
        | _ -> None
    | _ -> None

/// The sliver of ERB the fixtures use.
type internal Erb(options: Options) =
    let locals = Dictionary<string, string>()
    let digests = Dictionary<string, string>()

    member this.Evaluate(code: string) : string =
        match code.IndexOf " = " with
        | at when at >= 0 ->
            let name = code.Substring(0, at).Trim()
            locals[name] <- this.Evaluate(code.Substring(at + 3).Trim())
            ""
        | _ ->
            match locals.TryGetValue code with
            | true, value -> value
            | _ ->
                if code.StartsWith "BCrypt::Password.create(" && code.EndsWith ")" then
                    let password = code.Substring(24, code.Length - 25).Trim().Trim('"')
                    match digests.TryGetValue password with
                    | true, digest -> digest
                    | _ ->
                        let digest = PasswordDigest.digest password options.BcryptCost
                        digests[password] <- digest
                        digest
                elif code = "User.generate_bot_token" then
                    User.generateBotToken ()
                else
                    match parseAgo code with
                    | Some duration ->
                        // `TimeWithZone#to_s` in UTC: whole seconds.
                        let at = (Timestamp.FromSecond options.Now.AsSecond).Ago duration
                        $"{at.ToDb()} UTC"
                    | None -> Err.fail (DbError.other $"unsupported ERB in fixture: {code}")

    member this.Render(source: string) : string =
        let out = StringBuilder()
        let mutable rest = source
        let mutable go = true
        while go do
            match rest.IndexOf "<%" with
            | -1 -> go <- false
            | start ->
                out.Append(rest.Substring(0, start)) |> ignore
                let after = rest.Substring(start + 2)
                match after.IndexOf "%>" with
                | -1 -> Err.fail (DbError.other "unterminated ERB tag")
                | stop ->
                    let output, code =
                        if after.StartsWith "=" then true, after.Substring(1, stop - 1) else false, after.Substring(0, stop)
                    let value = this.Evaluate(code.Trim())
                    if output then out.Append value |> ignore
                    rest <- after.Substring(stop + 2)
                    // `<% %>` on its own line leaves the newline, as ERB without trim mode does.
        out.Append rest |> ignore
        out.ToString()

/// `action_text/rich_texts.yml` -> `action_text_rich_texts`, `push/subscriptions.yml` ->
/// `push_subscriptions` (the model's table name).
let tableName (dir: string) (file: string) : string =
    let relative = Path.GetRelativePath(dir, file)
    let withoutExtension =
        match Path.GetExtension relative with
        | null -> relative
        | extension -> relative.Substring(0, relative.Length - extension.Length)
    withoutExtension.Replace('/', '_').Replace('\\', '_')

let private collectYamlFiles (dir: string) : string list =
    let rec collect (dir: string) : string list =
        [ for entry in Directory.GetFileSystemEntries dir do
              if Directory.Exists entry then
                  if Path.GetFileName entry <> "files" then yield! collect entry
              elif Path.GetExtension entry = ".yml" then
                  yield entry ]
    collect dir

let private tableColumns (conn: Conn) (table: string) : string list =
    let columns = conn.QueryAll($"PRAGMA table_info(\"{table}\")", [||], fun r -> r.Text 1)
    if List.isEmpty columns then Err.fail (DbError.other $"no table {table}")
    columns

let private fixtureRow (table: string) (label: string) (attributes: (string * Yaml) list) (columns: string list) (now: Timestamp) : SortedDictionary<string, SqlArg> =
    let values = SortedDictionary<string, SqlArg>(StringComparer.Ordinal)
    for key, value in attributes do
        let association =
            associations table
            |> List.tryFind (fun a ->
                match a with
                | BelongsTo(k, _)
                | Polymorphic k -> k = key)
        match association with
        | Some(BelongsTo(_, column)) ->
            let target = scalarString value |> Option.defaultWith (fun () -> failwith $"{table}.{label}.{key}")
            values[column] <- I(identify (target.TrimStart ':'))
        | Some(Polymorphic key) ->
            let target = scalarString value |> Option.defaultWith (fun () -> failwith $"{table}.{label}.{key}")
            let target = target.Trim()
            if not (target.EndsWith ")") || target.LastIndexOf " (" < 0 then
                Err.fail (DbError.other $"{table}.{label}.{key} needs a (Class)")
            let at = target.LastIndexOf " ("
            let targetLabel = target.Substring(0, at)
            let className = target.Substring(at + 2, target.Length - at - 3)
            values[$"{key}_id"] <- I(identify (targetLabel.Trim().TrimStart ':'))
            values[$"{key}_type"] <- S className
        | None -> values[key] <- yamlToSql table key value
    if not (values.ContainsKey "id") then values["id"] <- I(identify label)
    for column in [ "created_at"; "updated_at" ] do
        if List.contains column columns && not (values.ContainsKey column) then values[column] <- S(now.ToDb())
    values

/// Loads every `*.yml` under `dir` into `conn`, inside the caller's transaction (a
/// `Database.Write`), which checks the foreign keys when it commits.
let load (conn: Conn) (dir: string) (options: Options) : Loaded =
    let files =
        collectYamlFiles dir
        |> List.sortWith (fun a b ->
            let parts (p: string) = Path.GetRelativePath(dir, p).Split([| '/'; '\\' |])
            let pa, pb = parts a, parts b
            let rec compareParts i =
                if i >= pa.Length || i >= pb.Length then compare pa.Length pb.Length
                else
                    match String.CompareOrdinal(pa[i], pb[i]) with
                    | 0 -> compareParts (i + 1)
                    | c -> c
            compareParts 0)

    conn.ExecuteBatch "PRAGMA defer_foreign_keys = ON"
    let erb = Erb options
    let mutable ids = Map.empty
    for file in files do
        let table = tableName dir file
        let source =
            try
                File.ReadAllText file
            with e ->
                Err.fail (DbError.other $"{file}: {e.Message}")
        let rows = YamlParser.parse (erb.Render source)

        conn.Execute($"DELETE FROM \"{table}\"", [||]) |> ignore
        let columns = tableColumns conn table
        let now = Timestamp.FromMicrosecond options.Now.AsMicrosecond
        let mutable labels = ids |> Map.tryFind table |> Option.defaultValue Map.empty
        for label, attributes in rows do
            if label <> "DEFAULTS" && label <> "_fixture" then
                let row = fixtureRow table label attributes columns now
                let id =
                    match row.TryGetValue "id" with
                    | true, I id -> id
                    | _ -> identify label
                labels <- labels |> Map.add label id
                let names = row.Keys |> Seq.map (fun c -> $"\"{c}\"") |> String.concat ", "
                let sql = $"INSERT INTO \"{table}\" ({names}) VALUES ({Sql.placeholders row.Count})"
                conn.ExecuteUncached(sql, row.Values |> Seq.toArray) |> ignore
        ids <- ids |> Map.add table labels
    { Ids = ids }
