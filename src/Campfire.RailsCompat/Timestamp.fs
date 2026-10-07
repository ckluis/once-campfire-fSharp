// Stands in for jiff's `Timestamp`, which rails_compat's API is written against.
namespace Campfire.RailsCompat

open System
open System.Globalization
open System.Text.RegularExpressions

/// An instant. Always compared and formatted in UTC; 100ns resolution.
type Timestamp = DateTimeOffset

module Timestamps =
    let unixEpoch: Timestamp = DateTimeOffset.UnixEpoch

    let private pattern =
        Regex(
            @"^([0-9]{4})-([0-9]{2})-([0-9]{2})[Tt ]([0-9]{2}):([0-9]{2})(?::([0-9]{2})(?:[.,]([0-9]{1,9}))?)?(Z|z|[+-][0-9]{2}(?::?[0-9]{2})?)\z",
            RegexOptions.CultureInvariant
        )

    let inline private digit (s: string) (i: int) : int = int s[i] - int '0'

    let inline private isDigit (s: string) (i: int) : bool = s[i] >= '0' && s[i] <= '9'

    /// The one shape `Metadata.iso8601Millis` writes (`2046-01-01T12:00:00.000Z`), which every cookie and
    /// token with an expiry carries: parsed without the regex. Anything else, including a date the calendar
    /// refuses, is left to `parseWithPattern`, so the two agree on every string (a test compares them).
    let private tryParseMillis (s: string) : Timestamp voption =
        if s.Length <> 24
           || s[4] <> '-' || s[7] <> '-' || s[10] <> 'T' || s[13] <> ':' || s[16] <> ':' || s[19] <> '.' || s[23] <> 'Z'
           || not (isDigit s 0 && isDigit s 1 && isDigit s 2 && isDigit s 3 && isDigit s 5 && isDigit s 6 && isDigit s 8 && isDigit s 9)
           || not (isDigit s 11 && isDigit s 12 && isDigit s 14 && isDigit s 15 && isDigit s 17 && isDigit s 18)
           || not (isDigit s 20 && isDigit s 21 && isDigit s 22) then
            ValueNone
        else
            let year = digit s 0 * 1000 + digit s 1 * 100 + digit s 2 * 10 + digit s 3
            let month = digit s 5 * 10 + digit s 6
            let day = digit s 8 * 10 + digit s 9
            let hour = digit s 11 * 10 + digit s 12
            let minute = digit s 14 * 10 + digit s 15
            let second = min (digit s 17 * 10 + digit s 18) 59
            if year < 1 || month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 then
                ValueNone
            else
                let millis = digit s 20 * 100 + digit s 21 * 10 + digit s 22
                let local = DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc).AddTicks(int64 millis * TimeSpan.TicksPerMillisecond)
                ValueSome(DateTimeOffset(local, TimeSpan.Zero))

    let internal parseWithPattern (s: string) : Timestamp option =
        let m = pattern.Match s
        if not m.Success then
            None
        else
            let int (i: int) = Int32.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture)
            try
                let second = if m.Groups[6].Success then min (int 6) 59 else 0
                let ticks =
                    if m.Groups[7].Success then
                        Int64.Parse(m.Groups[7].Value.PadRight(9, '0').Substring(0, 7), CultureInfo.InvariantCulture)
                    else
                        0L
                let local = DateTime(int 1, int 2, int 3, int 4, int 5, second, DateTimeKind.Unspecified).AddTicks ticks
                let offset =
                    match m.Groups[8].Value with
                    | "Z"
                    | "z" -> TimeSpan.Zero
                    | text ->
                        let sign = if text[0] = '-' then -1.0 else 1.0
                        let digits = text.Substring(1).Replace(":", "")
                        let hours = Int32.Parse(digits.Substring(0, 2), CultureInfo.InvariantCulture)
                        let minutes = if digits.Length > 2 then Int32.Parse(digits.Substring 2, CultureInfo.InvariantCulture) else 0
                        TimeSpan.FromMinutes(sign * float (hours * 60 + minutes))
                Some(DateTimeOffset(local, offset).ToUniversalTime())
            with :? ArgumentException ->
                None

    /// `str::parse::<Timestamp>()`: an RFC 3339 date and time that carries an offset. None when
    /// it doesn't parse.
    let tryParse (s: string) : Timestamp option =
        match tryParseMillis s with
        | ValueSome t -> Some t
        | ValueNone -> parseWithPattern s
