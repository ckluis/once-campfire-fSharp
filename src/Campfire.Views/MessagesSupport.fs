// Port of rust/crates/views/src/messages/support.rs
/// Small Ruby/Rails behaviors the message and room views depend on: time formats and numbers as
/// Ruby prints them.
module Campfire.Views.MessagesSupport

open System
open System.Globalization
open Campfire.RailsCompat
open Campfire.Ruby

let private epochTicks = DateTimeOffset.UnixEpoch.UtcTicks

/// `time.iso8601` for a UTC `ActiveSupport::TimeWithZone`: seconds precision, `Z` suffix.
let iso8601 (time: Timestamp) : string =
    time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)

/// `time.to_fs(:epoch)`, defined in `reference/config/initializers/time_formats.rb` as
/// `(time.to_f * 1000).to_i`. The float round trip is deliberate: it truncates some
/// millisecond values down by one, and the client compares these numbers.
let epochMs (time: Timestamp) : int64 =
    // `Time#to_f` is the nearest double to the exact rational, which parsing the decimal
    // representation gives us. A timestamp is whole seconds and a sub-second part of the same
    // sign (jiff's representation), at 100 ns here.
    let sinceEpoch = time.UtcTicks - epochTicks
    let seconds = sinceEpoch / 10_000_000L
    let nanos = int ((sinceEpoch % 10_000_000L) * 100L)
    let decimalText =
        if seconds < 0L && nanos <> 0 then
            let whole = seconds + 1L
            let frac = 1_000_000_000 - nanos
            let sign = if whole = 0L then "-" else ""
            $"{sign}{whole}.{frac:D9}"
        else
            $"{seconds}.{nanos:D9}"
    let toF =
        match Double.TryParse(decimalText, NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, value when not (decimalText.Contains(".-")) -> value
        | _ -> float seconds
    int64 (toF * 1000.0)

/// `time.as_json` with Active Support's default precision: `2026-09-26T12:26:46.848Z`.
let jsonTime (time: Timestamp) : string =
    let millis = int (((time.UtcTicks - epochTicks) % 10_000_000L) / 10_000L)
    let seconds = time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
    $"{seconds}.{millis:D3}Z"

/// A number as Ruby prints it: integers bare, floats as `Float#to_s` writes them, always with a
/// fractional part (`600.0`) and in exponent form outside `1e-4..1e15` (`1.0e+15`).
[<Struct>]
type RubyNumber =
    | Int of int: int64
    | Float of float: float

    member this.ToF: float =
        match this with
        | Int value -> float value
        | Float value -> value

    /// `number / 2`: integer division for integers.
    member this.Half: RubyNumber =
        match this with
        | Int value ->
            let quotient = value / 2L
            Int(if value % 2L < 0L then quotient - 1L else quotient)
        | Float value -> Float(value / 2.0)

    override this.ToString() =
        match this with
        | Int value -> value.ToString(CultureInfo.InvariantCulture)
        | Float value -> Ruby.floatToS value
