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
            @"^([0-9]{4})-([0-9]{2})-([0-9]{2})[Tt ]([0-9]{2}):([0-9]{2})(?::([0-9]{2})(?:[.,]([0-9]{1,9}))?)?(Z|z|[+-][0-9]{2}(?::?[0-9]{2})?)$",
            RegexOptions.CultureInvariant
        )

    /// `str::parse::<Timestamp>()`: an RFC 3339 date and time that carries an offset. None when
    /// it doesn't parse.
    let tryParse (s: string) : Timestamp option =
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
