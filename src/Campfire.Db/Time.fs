// Port of rust/crates/db/src/time.rs
namespace Campfire.Db

open System
open System.Buffers
open System.Globalization

/// A UTC instant with microsecond precision, stored the way Active Record stores it.
///
/// Rails writes `datetime(6)` columns as UTC text, `"%Y-%m-%d %H:%M:%S"` followed by
/// `".%06d"` microseconds only when the microseconds are non-zero
/// (`ActiveRecord::ConnectionAdapters::Quoting#quoted_date`). Values are truncated, not
/// rounded, to microseconds on assignment (`ActiveModel::Type::Helpers::TimeValue`).
/// `insert_all` instead lets SQLite stamp rows with `STRFTIME('%Y-%m-%d %H:%M:%f', 'NOW')`,
/// which has millisecond precision; see `Time.SqliteNow`.
[<Struct; CustomComparison; CustomEquality>]
type Timestamp =
    { Microsecond: int64 }

    static member private EpochTicks = DateTime.UnixEpoch.Ticks

    static member private DigitAt(text: ReadOnlySpan<byte>, i: int) : bool = uint (int text[i] - 48) <= 9u

    static member private Two(text: ReadOnlySpan<byte>, i: int) : int = (int text[i] - 48) * 10 + (int text[i + 1] - 48)

    /// Truncates (toward negative infinity) to the microseconds a `datetime(6)` column keeps.
    static member FromDateTimeOffset(at: DateTimeOffset) : Timestamp =
        let ticks = at.UtcTicks - Timestamp.EpochTicks
        { Microsecond = (if ticks >= 0L then ticks / 10L else (ticks - 9L) / 10L) }

    static member FromMicrosecond(us: int64) : Timestamp = { Microsecond = us }

    static member FromSecond(s: int64) : Timestamp = { Microsecond = s * 1_000_000L }

    member this.ToDateTimeOffset() : DateTimeOffset =
        DateTimeOffset(Timestamp.EpochTicks + this.Microsecond * 10L, TimeSpan.Zero)

    member this.AsMicrosecond = this.Microsecond

    /// Whole seconds since the epoch, rounded toward negative infinity (as jiff's `as_second`).
    member this.AsSecond : int64 =
        let q = this.Microsecond / 1_000_000L
        if this.Microsecond % 1_000_000L < 0L then q - 1L else q

    /// The microseconds past the whole second (negative before the epoch, as jiff's).
    member this.SubsecMicrosecond : int = int (this.Microsecond % 1_000_000L)

    /// `1.hour.ago`-style arithmetic.
    member this.Ago(duration: TimeSpan) : Timestamp =
        { Microsecond = this.Microsecond - duration.Ticks / 10L }

    /// The exact text Active Record writes to SQLite.
    member this.ToDb() : string =
        let at = DateTime(Timestamp.EpochTicks + this.Microsecond * 10L, DateTimeKind.Utc)
        let us = this.SubsecMicrosecond
        let length = if us = 0 then 19 else 26
        String.Create(
            length,
            struct (at, us),
            SpanAction<char, struct (DateTime * int)>(fun span (struct (at, us)) ->
                let year = at.Year
                let month, day, hour, minute, second = at.Month, at.Day, at.Hour, at.Minute, at.Second
                span[0] <- char (48 + year / 1000)
                span[1] <- char (48 + year / 100 % 10)
                span[2] <- char (48 + year / 10 % 10)
                span[3] <- char (48 + year % 10)
                span[4] <- '-'
                span[5] <- char (48 + month / 10)
                span[6] <- char (48 + month % 10)
                span[7] <- '-'
                span[8] <- char (48 + day / 10)
                span[9] <- char (48 + day % 10)
                span[10] <- ' '
                span[11] <- char (48 + hour / 10)
                span[12] <- char (48 + hour % 10)
                span[13] <- ':'
                span[14] <- char (48 + minute / 10)
                span[15] <- char (48 + minute % 10)
                span[16] <- ':'
                span[17] <- char (48 + second / 10)
                span[18] <- char (48 + second % 10)
                if us <> 0 then
                    span[19] <- '.'
                    let mutable rest = us
                    for i in 25 .. -1 .. 20 do
                        span[i] <- char (48 + rest % 10)
                        rest <- rest / 10)
        )

    /// `YYYY-MM-DD HH:MM:SS` and up to six fractional digits, the form Rails and SQLite write, read
    /// without allocating; anything else is None here and goes the long way.
    static member private ParseDbFast(text: string) : Timestamp voption =
        let n = text.Length
        let digit (i: int) = int text[i] - 48
        let isDigit (i: int) = uint (int text[i] - 48) <= 9u
        if n < 19 || n > 26 || text[4] <> '-' || text[7] <> '-' || text[10] <> ' ' || text[13] <> ':' || text[16] <> ':' then
            ValueNone
        elif not (isDigit 0 && isDigit 1 && isDigit 2 && isDigit 3 && isDigit 5 && isDigit 6 && isDigit 8 && isDigit 9 && isDigit 11 && isDigit 12 && isDigit 14 && isDigit 15 && isDigit 17 && isDigit 18) then
            ValueNone
        else
            let y = digit 0 * 1000 + digit 1 * 100 + digit 2 * 10 + digit 3
            let mo = digit 5 * 10 + digit 6
            let d = digit 8 * 10 + digit 9
            let h = digit 11 * 10 + digit 12
            let mi = digit 14 * 10 + digit 15
            let s = digit 17 * 10 + digit 18
            if y < 1 || mo < 1 || mo > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo) || h > 23 || mi > 59 || s > 59 then
                ValueNone
            else
                let mutable micros = 0
                let mutable ok = true
                if n > 19 then
                    if n = 20 || text[19] <> '.' then
                        ok <- false
                    else
                        let mutable scale = 100_000
                        for i in 20 .. n - 1 do
                            if isDigit i then
                                micros <- micros + digit i * scale
                                scale <- scale / 10
                            else
                                ok <- false
                if ok then
                    let at = DateTime(y, mo, d, h, mi, s, DateTimeKind.Utc)
                    ValueSome { Microsecond = (at.Ticks - Timestamp.EpochTicks) / 10L + int64 micros }
                else
                    ValueNone

    /// `ParseDbFast` on the UTF-8 bytes SQLite holds, without making a string of them: the same form, the same checks. Anything
    /// else is None here and goes the long way, through the string.
    static member ParseDbUtf8(text: ReadOnlySpan<byte>) : Timestamp voption =
        let n = text.Length
        if n < 19 || n > 26 || text[4] <> byte '-' || text[7] <> byte '-' || text[10] <> byte ' ' || text[13] <> byte ':' || text[16] <> byte ':' then
            ValueNone
        else
            let mutable ok =
                Timestamp.DigitAt(text, 0) && Timestamp.DigitAt(text, 1) && Timestamp.DigitAt(text, 2) && Timestamp.DigitAt(text, 3)
                && Timestamp.DigitAt(text, 5) && Timestamp.DigitAt(text, 6) && Timestamp.DigitAt(text, 8) && Timestamp.DigitAt(text, 9)
                && Timestamp.DigitAt(text, 11) && Timestamp.DigitAt(text, 12) && Timestamp.DigitAt(text, 14) && Timestamp.DigitAt(text, 15)
                && Timestamp.DigitAt(text, 17) && Timestamp.DigitAt(text, 18)
            if ok && n > 19 then
                if n = 20 || text[19] <> byte '.' then ok <- false
                for i in 20 .. n - 1 do
                    if not (Timestamp.DigitAt(text, i)) then ok <- false
            if not ok then
                ValueNone
            else
                let y = Timestamp.Two(text, 0) * 100 + Timestamp.Two(text, 2)
                let mo, d, h, mi, s = Timestamp.Two(text, 5), Timestamp.Two(text, 8), Timestamp.Two(text, 11), Timestamp.Two(text, 14), Timestamp.Two(text, 17)
                if y < 1 || mo < 1 || mo > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo) || h > 23 || mi > 59 || s > 59 then
                    ValueNone
                else
                    let mutable micros = 0
                    let mutable scale = 100_000
                    for i in 20 .. n - 1 do
                        micros <- micros + (int text[i] - 48) * scale
                        scale <- scale / 10
                    let at = DateTime(y, mo, d, h, mi, s, DateTimeKind.Utc)
                    ValueSome { Microsecond = (at.Ticks - Timestamp.EpochTicks) / 10L + int64 micros }

    /// Parses what Rails (or SQLite's `STRFTIME`) wrote: `YYYY-MM-DD HH:MM:SS[.fraction]`,
    /// also tolerating a `T` separator and a trailing `Z` or ` UTC`.
    static member ParseDb(text: string) : Timestamp option =
        match Timestamp.ParseDbFast text with
        | ValueSome ts -> Some ts
        | ValueNone -> Timestamp.ParseDbSlow text

    /// `ParseDb` without allocating an option for the common case.
    static member ParseDbValue(text: string) : Timestamp voption =
        match Timestamp.ParseDbFast text with
        | ValueSome ts -> ValueSome ts
        | ValueNone ->
            match Timestamp.ParseDbSlow text with
            | Some ts -> ValueSome ts
            | None -> ValueNone

    static member private ParseDbSlow(text: string) : Timestamp option =
        let text = text.Trim()
        let text =
            if text.EndsWith(" UTC", StringComparison.Ordinal) then text.Substring(0, text.Length - 4)
            elif text.EndsWith("Z", StringComparison.Ordinal) then text.Substring(0, text.Length - 1)
            else text
        if text.Length < 19 then
            None
        else
            let whole = text.Substring(0, 19)
            let fraction = text.Substring 19
            let isDigits (s: string) = s.Length > 0 && s |> Seq.forall Char.IsAsciiDigit
            let num (start: int) (length: int) =
                let s = whole.Substring(start, length)
                if isDigits s then Some(Int32.Parse(s, CultureInfo.InvariantCulture)) else None
            if whole[4] <> '-' || whole[7] <> '-' || not (whole[10] = ' ' || whole[10] = 'T') || whole[13] <> ':' || whole[16] <> ':' then
                None
            else
                let micros =
                    if fraction.Length = 0 then
                        Some 0
                    elif fraction[0] = '.' && fraction.Length > 1 && isDigits (fraction.Substring 1) then
                        let digits = fraction.Substring(1, min 6 (fraction.Length - 1))
                        Some(Int32.Parse(digits.PadRight(6, '0'), CultureInfo.InvariantCulture))
                    else
                        None
                match num 0 4, num 5 2, num 8 2, num 11 2, num 14 2, num 17 2, micros with
                | Some y, Some mo, Some d, Some h, Some mi, Some s, Some us ->
                    if y < 1 || mo < 1 || mo > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo) || h > 23 || mi > 59 || s > 59 then
                        None
                    else
                        let at = DateTime(y, mo, d, h, mi, s, DateTimeKind.Utc)
                        Some { Microsecond = (at.Ticks - Timestamp.EpochTicks) / 10L + int64 us }
                | _ -> None

    override this.ToString() = this.ToDb()

    override this.Equals(other: obj) =
        match other with
        | :? Timestamp as t -> t.Microsecond = this.Microsecond
        | _ -> false

    override this.GetHashCode() = this.Microsecond.GetHashCode()

    interface IComparable with
        member this.CompareTo(other: obj) =
            match other with
            | :? Timestamp as t -> compare this.Microsecond t.Microsecond
            | _ -> invalidArg "other" "not a Timestamp"

    interface IComparable<Timestamp> with
        member this.CompareTo(other: Timestamp) = compare this.Microsecond other.Microsecond

    interface IEquatable<Timestamp> with
        member this.Equals(other: Timestamp) = this.Microsecond = other.Microsecond

module Time =
    /// The timestamp expression Rails' `insert_all` uses on SQLite for `created_at`/`updated_at`.
    [<Literal>]
    let SqliteNow = "STRFTIME('%Y-%m-%d %H:%M:%f', 'NOW')"
