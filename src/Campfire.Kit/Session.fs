// Port of rust/crates/kit/src/session.rs
//
// The Rails cookie-store session (`ActionDispatch::Session::CookieStore` over
// `Rack::Session::Abstract::PersistedSecure`), as configured in
// `reference/config/initializers/session_store.rb`: key `_campfire_session`, encrypted, and
// `expire_after: 20.years`.
//
// Loading is lazy. Unlike Rails, the cookie is only written when the session changed during the
// request, and deleted when that left it empty. Rails' `commit_session` rewrites it on every
// request that loads or carries one (`expire_after` forces the update), which made a session
// cookie, re-encrypted, part of nearly every response. Without CSRF tokens the session holds only
// the flash and a return-to URL, so an unchanged session needs no cookie traffic and an empty one
// no cookie. Cookies Rails wrote are read the same way.
namespace Campfire.Kit

open System
open System.Collections.Generic
open System.Security.Cryptography
open Campfire.RailsCompat
open Campfire.Kit

module SessionConstants =
    [<Literal>]
    let SessionKey = "_campfire_session"

    [<Literal>]
    let ExpireAfterYears = 20

type SessionConfig =
    { Key: string
      ExpireAfterYears: int voption
      HttpOnly: bool }

    static member Default: SessionConfig =
        { Key = SessionConstants.SessionKey
          ExpireAfterYears = ValueSome SessionConstants.ExpireAfterYears
          HttpOnly = true }

[<Sealed>]
type Session(config: SessionConfig) =
    let mutable loaded = false

    /// Whether the data changed during this request, and so the cookie needs writing.
    let mutable changed = false

    let mutable data: ResizeArray<KeyValuePair<string, Value>> = ResizeArray<KeyValuePair<string, Value>>(0)

    /// The cookie's decoded contents, read once (`action_dispatch.request.unsigned_session_cookie`).
    let mutable cookieData: ResizeArray<KeyValuePair<string, Value>> | null = null

    /// `ActionDispatch::Session::Compatibility#generate_sid`: `SecureRandom.hex(16)`.
    static member GenerateSid() : string = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes 16)

    member _.IsLoaded = loaded

    static member private Find(items: ResizeArray<KeyValuePair<string, Value>>, key: string) : int =
        let mutable found = -1
        let mutable i = 0
        while found < 0 && i < items.Count do
            if String.Equals(items[i].Key, key, StringComparison.Ordinal) then found <- i
            i <- i + 1
        found

    static member private SetIn(items: ResizeArray<KeyValuePair<string, Value>>, key: string, value: Value) : unit =
        match Session.Find(items, key) with
        | -1 -> items.Add(KeyValuePair(key, value))
        | i -> items[i] <- KeyValuePair(key, value)

    member private _.CookieData(jar: CookieJar) : ResizeArray<KeyValuePair<string, Value>> =
        match cookieData with
        | null ->
            let read = ResizeArray<KeyValuePair<string, Value>>()
            match jar.Encrypted config.Key with
            | ValueSome(Value.Object entries) ->
                for (k, v) in entries do
                    read.Add(KeyValuePair(k, v))
            | _ -> ()
            cookieData <- read
            read
        | existing -> existing

    /// Load from the cookie if not yet loaded (`load_for_read!`/`load_for_write!`).
    member this.Load(jar: CookieJar) : Session =
        if not loaded then
            let fromCookie = this.CookieData jar
            let copy = ResizeArray<KeyValuePair<string, Value>>(fromCookie)
            let missing =
                match Session.Find(copy, "session_id") with
                | -1 -> true
                | i -> copy[i].Value = Value.Null
            if missing then Session.SetIn(copy, "session_id", Value.String(Session.GenerateSid()))
            data <- copy
            loaded <- true
        this

    member private _.AssertLoaded() =
        System.Diagnostics.Debug.Assert(loaded, "session used before Load(); go through Ctx.Session()")

    member this.Id: string | null =
        this.AssertLoaded()
        match Session.Find(data, "session_id") with
        | -1 -> null
        | i ->
            match data[i].Value with
            | Value.String s -> s
            | _ -> null

    /// The value for `key`, ValueNone when it is missing or null.
    member this.Get(key: string) : Value voption =
        this.AssertLoaded()
        match Session.Find(data, key) with
        | -1 -> ValueNone
        | i ->
            match data[i].Value with
            | Value.Null -> ValueNone
            | value -> ValueSome value

    member this.GetStr(key: string) : string | null =
        match this.Get key with
        | ValueSome(Value.String s) -> s
        | _ -> null

    member this.ContainsKey(key: string) : bool =
        this.AssertLoaded()
        Session.Find(data, key) >= 0

    member this.Insert(key: string, value: Value) : unit =
        this.AssertLoaded()
        match Session.Find(data, key) with
        | i when i >= 0 && data[i].Value = value -> ()
        | _ ->
            Session.SetIn(data, key, value)
            changed <- true

    member this.Insert(key: string, value: string) : unit = this.Insert(key, Value.String value)

    member this.Remove(key: string) : Value voption =
        this.AssertLoaded()
        match Session.Find(data, key) with
        | -1 -> ValueNone
        | i ->
            let removed = data[i].Value
            data.RemoveAt i
            changed <- true
            ValueSome removed

    /// `reset_session`: drop everything and start a new session id.
    member _.Reset() : unit =
        let fresh = ResizeArray<KeyValuePair<string, Value>>()
        fresh.Add(KeyValuePair("session_id", Value.String(Session.GenerateSid())))
        data <- fresh
        cookieData <- ResizeArray fresh
        loaded <- true
        changed <- true

    /// Writes the cookie into `jar` if the session changed, or deletes it if that left nothing but
    /// the session id.
    member _.Commit(jar: CookieJar, now: Timestamp) : Result<unit, Error> =
        if not changed then
            Ok()
        else
            let kept = [ for kv in data do if kv.Value <> Value.Null then kv.Key, kv.Value ]
            if kept |> List.forall (fun (key, _) -> key = "session_id") then
                jar.Delete config.Key
                Ok()
            else
                let cookie =
                    let cookie = { Cookie.New "" with HttpOnly = config.HttpOnly }
                    match config.ExpireAfterYears with
                    | ValueSome years -> cookie.WithExpires(KitClock.yearsFrom now years)
                    | ValueNone -> cookie
                jar.SetEncrypted(config.Key, Value.Object kept, cookie)

/// `ActionDispatch::Flash::FlashHash`, stored in the session under `"flash"` as
/// `{ "discard" => [], "flashes" => { ... } }`.
[<Sealed>]
type Flash() =
    let flashes = ResizeArray<KeyValuePair<string, Value>>()
    let discarded = ResizeArray<string>()

    /// `FlashHash.from_session_value`: everything loaded is marked for discard at the end of this
    /// request, minus what the previous request had already discarded.
    static member FromSessionValue(value: Value voption) : Flash =
        let flash = Flash()
        match value with
        | ValueSome(Value.Object _ as stored) ->
            let alreadyDiscarded =
                match stored.TryGet "discard" with
                | Some(Value.Array items) -> [ for item in items do match item with Value.String s -> s | _ -> () ]
                | _ -> []
            match stored.TryGet "flashes" with
            | Some(Value.Object entries) ->
                for (k, v) in entries do
                    if not (List.contains k alreadyDiscarded) then
                        flash.AddLoaded(k, v)
            | _ -> ()
        | _ -> ()
        flash

    member private _.AddLoaded(key: string, value: Value) =
        flashes.Add(KeyValuePair(key, value))
        discarded.Add key

    /// `FlashHash#to_session_value`: ValueNone when nothing survives.
    member _.ToSessionValue() : Value voption =
        let keep = [ for kv in flashes do if not (discarded.Contains kv.Key) then kv.Key, kv.Value ]
        if keep.IsEmpty then
            ValueNone
        else
            ValueSome(Value.Object [ "discard", Value.Array []; "flashes", Value.Object keep ])

    member _.Get(key: string) : Value voption =
        match flashes.FindIndex(fun kv -> kv.Key = key) with
        | -1 -> ValueNone
        | i -> ValueSome flashes[i].Value

    member this.GetStr(key: string) : string | null =
        match this.Get key with
        | ValueSome(Value.String s) -> s
        | _ -> null

    /// `flash[key] = value`: shown on the next request.
    member _.Set(key: string, value: Value) : unit =
        discarded.RemoveAll(fun k -> k = key) |> ignore
        match flashes.FindIndex(fun kv -> kv.Key = key) with
        | -1 -> flashes.Add(KeyValuePair(key, value))
        | i -> flashes[i] <- KeyValuePair(key, value)

    member this.Set(key: string, value: string) : unit = this.Set(key, Value.String value)

    /// `flash.now[key] = value`: shown on this request only.
    member this.Now(key: string, value: Value) : unit =
        this.Set(key, value)
        this.Discard(ValueSome key)

    member this.Now(key: string, value: string) : unit = this.Now(key, Value.String value)

    member _.Keep(key: string voption) : unit =
        match key with
        | ValueSome key -> discarded.RemoveAll(fun k -> k = key) |> ignore
        | ValueNone -> discarded.Clear()

    member _.Discard(key: string voption) : unit =
        let keys =
            match key with
            | ValueSome key -> [ key ]
            | ValueNone -> [ for kv in flashes -> kv.Key ]
        for key in keys do
            if not (discarded.Contains key) then discarded.Add key

    member _.Delete(key: string) : unit =
        discarded.RemoveAll(fun k -> k = key) |> ignore
        flashes.RemoveAll(fun kv -> kv.Key = key) |> ignore

    member _.IsEmpty: bool = flashes.Count = 0

    member _.Keys: string list = [ for kv in flashes -> kv.Key ]

    member this.Notice: string | null = this.GetStr "notice"
    member this.Alert: string | null = this.GetStr "alert"
    member this.SetNotice(message: string) : unit = this.Set("notice", message)
    member this.SetAlert(message: string) : unit = this.Set("alert", message)
