// Port of rust/crates/kit/src/clock.rs
//
// The process clock, and the HTTP layer's date arithmetic and formats. (Named `KitClock` because
// `Campfire.RailsCompat.Clock`, which holds `Clock`, `SystemClock` and `TestClock` as Rust re-exports
// them here, is a module of its own.)
//
// Set `CAMPFIRE_FROZEN_TIME` (an RFC 3339 timestamp such as `2024-06-01T12:00:00Z`) to pin
// `Now()` for the whole process; `fromEnv` reads it at boot.
module Campfire.Kit.KitClock

open System
open System.Globalization
open Campfire.RailsCompat
open Campfire.RailsCompat.Clock

[<Literal>]
let FrozenTimeEnv = "CAMPFIRE_FROZEN_TIME"

/// The process clock: frozen at `CAMPFIRE_FROZEN_TIME` when set, the system clock otherwise.
let fromEnv () : Result<SharedClock, string> =
    match Environment.GetEnvironmentVariable FrozenTimeEnv with
    | null -> Ok(SystemClock())
    | value when String.IsNullOrWhiteSpace value -> Ok(SystemClock())
    | value ->
        match Timestamps.tryParse (value.Trim()) with
        | Some now -> Ok(TestClock.FrozenAt now)
        | None -> Error $"{FrozenTimeEnv}={value} is not an RFC 3339 timestamp"

/// `n.years.from_now` as ActiveSupport computes it: calendar years in UTC.
let yearsFrom (now: Timestamp) (years: int) : Timestamp =
    try
        now.ToUniversalTime().AddYears years
    with :? ArgumentOutOfRangeException ->
        now

/// An HTTP date (`Time#httpdate`): `Thu, 01 Jan 1970 00:00:00 GMT`.
let httpdate (at: Timestamp) : string = at.UtcDateTime.ToString("R", CultureInfo.InvariantCulture)

let private weekdays = [| "sun"; "mon"; "tue"; "wed"; "thu"; "fri"; "sat" |]

let private months = [| "jan"; "feb"; "mar"; "apr"; "may"; "jun"; "jul"; "aug"; "sep"; "oct"; "nov"; "dec" |]

let private isSpace (c: char) = c = ' ' || c = '\t' || c = '\r' || c = '\n'

/// The zone's offset in minutes: `+hhmm`/`-hhmm`, or one of RFC 2822's names (other alphabetic zones
/// are "-0000", which reads as UTC).
let private zoneMinutes (zone: string) : int voption =
    let digits (s: string) = s.Length = 4 && Seq.forall Char.IsAsciiDigit s
    if zone.Length = 5 && (zone[0] = '+' || zone[0] = '-') && digits (zone.Substring 1) then
        let hours = Int32.Parse(zone.AsSpan(1, 2), CultureInfo.InvariantCulture)
        let minutes = Int32.Parse(zone.AsSpan(3, 2), CultureInfo.InvariantCulture)
        if minutes > 59 || hours > 99 then
            ValueNone
        else
            let total = hours * 60 + minutes
            ValueSome(if zone[0] = '-' then -total else total)
    elif zone.Length > 0 && Seq.forall Char.IsAsciiLetter zone then
        match zone.ToUpperInvariant() with
        | "EDT" -> ValueSome -240
        | "EST"
        | "CDT" -> ValueSome -300
        | "CST"
        | "MDT" -> ValueSome -360
        | "MST"
        | "PDT" -> ValueSome -420
        | "PST" -> ValueSome -480
        | _ -> ValueSome 0
    else
        ValueNone

/// Parse an HTTP date (`Time.httpdate` / `Time.rfc2822`), `None` when malformed.
let parseHttpdate (value: string) : Timestamp option =
    let text = value.Trim()
    let tokens = text.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
    let mutable i = 0
    let mutable weekday = -1
    let mutable ok = true
    // An optional weekday, possibly glued to its comma: "Thu," or "Thu ,".
    let weekdayToken = if tokens.Length > 0 then tokens[0] else ""
    let isWeekday (t: string) = t.Length >= 3 && Seq.forall Char.IsAsciiLetter (t.Substring(0, 3)) && Array.contains (t.Substring(0, 3).ToLowerInvariant()) weekdays
    if tokens.Length > 0 && isWeekday weekdayToken then
        let rest = weekdayToken.Substring 3
        if rest = "," then
            weekday <- Array.IndexOf(weekdays, weekdayToken.Substring(0, 3).ToLowerInvariant())
            i <- 1
        elif rest = "" && tokens.Length > 1 && tokens[1].StartsWith(",", StringComparison.Ordinal) then
            weekday <- Array.IndexOf(weekdays, weekdayToken.Substring(0, 3).ToLowerInvariant())
            // "Thu , 01 ..." or "Thu ,01 ...": the comma may carry the day.
            if tokens[1] = "," then
                i <- 2
            else
                tokens[1] <- tokens[1].Substring 1
                i <- 1
        elif rest.StartsWith(",", StringComparison.Ordinal) then
            weekday <- Array.IndexOf(weekdays, weekdayToken.Substring(0, 3).ToLowerInvariant())
            tokens[0] <- rest.Substring 1
            i <- 0
        else
            ok <- false
    if not ok || tokens.Length - i <> 5 then
        None
    else
        let day = tokens[i]
        let month = Array.IndexOf(months, tokens[i + 1].ToLowerInvariant())
        let year = tokens[i + 2]
        let clock = tokens[i + 3].Split ':'
        let zone = tokens[i + 4]
        let allDigits (s: string) = s.Length > 0 && Seq.forall Char.IsAsciiDigit s
        let two (s: string) = s.Length = 2 && allDigits s
        if not (allDigits day && day.Length <= 2 && month >= 0 && allDigits year && year.Length >= 2 && year.Length <= 9)
           || not (clock.Length = 2 || clock.Length = 3)
           || not (Array.forall two clock) then
            None
        else
            let n (s: string) = Int32.Parse(s, CultureInfo.InvariantCulture)
            // Two- and three-digit years are RFC 2822's obsolete forms.
            let year =
                let y = n year
                if year.Length = 2 then (if y >= 50 then 1900 + y else 2000 + y)
                elif year.Length = 3 then 1900 + y
                else y
            let hour, minute = n clock[0], n clock[1]
            let second = if clock.Length = 3 then n clock[2] else 0
            match zoneMinutes zone with
            | ValueNone -> None
            | ValueSome offset ->
                if hour > 23 || minute > 59 || second > 60 || year < 1 || year > 9999 || n day < 1 || n day > DateTime.DaysInMonth(year, month + 1) then
                    None
                else
                    let local = DateTime(year, month + 1, n day, hour, minute, min second 59, DateTimeKind.Unspecified)
                    if weekday >= 0 && int local.DayOfWeek <> weekday then
                        None
                    else
                        try
                            Some(DateTimeOffset(local, TimeSpan.FromMinutes(float offset)).ToUniversalTime())
                        with :? ArgumentOutOfRangeException ->
                            None
