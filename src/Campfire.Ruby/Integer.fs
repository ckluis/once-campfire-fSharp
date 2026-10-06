// Port of rust/crates/ruby/src/integer.rs
module Campfire.Ruby.Integer

open System
open Campfire.Ruby.RubyString

let private int128Max = Int128.MaxValue
let private ten = Int128.CreateChecked 10

/// The common case of `String#to_i` without the 128-bit arithmetic (whose comparisons F# makes by boxing, an
/// allocation for every digit): optional whitespace, an optional sign and up to 18 digits that end the string
/// or are followed by anything but an underscore. A `0d` prefix, an underscore or a longer number is for `toI128Slowly`.
let private tryFastToI (s: string) (result: byref<int64>) : bool =
    let mutable i = 0
    while i < s.Length && isSpace s[i] do
        i <- i + 1
    let mutable negative = false
    if i < s.Length then
        match s[i] with
        | '-' ->
            negative <- true
            i <- i + 1
        | '+' -> i <- i + 1
        | _ -> ()
    let start = i
    let mutable number = 0L
    while i < s.Length && s[i] >= '0' && s[i] <= '9' do
        number <- number * 10L + int64 (int s[i] - int '0')
        i <- i + 1
    let digits = i - start
    if digits = 0 || digits > 18 then
        false
    elif i < s.Length && s[i] = '_' then
        false
    elif digits = 1 && s[start] = '0' && i < s.Length && (s[i] = 'd' || s[i] = 'D') then
        false
    else
        result <- (if negative then -number else number)
        true

/// `String#to_i`: optional leading whitespace and sign, an optional `0d`, then digits (an
/// underscore allowed between two). Saturating, and wide enough to tell every i64, and every file
/// size, from what's past it.
let internal toI128Slowly (s: string) : Int128 =
    let mutable i = 0
    while i < s.Length && isSpace s[i] do
        i <- i + 1
    let mutable negative = false
    if i < s.Length then
        match s[i] with
        | '-' ->
            negative <- true
            i <- i + 1
        | '+' -> i <- i + 1
        | _ -> ()
    if i + 1 < s.Length && s[i] = '0' && (s[i + 1] = 'd' || s[i + 1] = 'D') then
        i <- i + 2
    let mutable number = Int128.Zero
    let mutable previousDigit = false
    let mutable going = true
    while going && i < s.Length do
        let c = s[i]
        if c >= '0' && c <= '9' then
            let digit = Int128.CreateChecked(int c - int '0')
            number <- if number > (int128Max - digit) / ten then int128Max else number * ten + digit
            previousDigit <- true
            i <- i + 1
        elif c = '_' && previousDigit then
            previousDigit <- false
            i <- i + 1
        else
            going <- false
    if negative then -number else number

let private int64Min = Int128.CreateChecked Int64.MinValue
let private int64Max = Int128.CreateChecked Int64.MaxValue

let internal toI128 (s: string) : Int128 =
    let mutable quick = 0L
    if tryFastToI s &quick then Int128.op_Implicit quick else toI128Slowly s

/// `String#to_i`, saturating at the i64 bounds where Ruby goes on to a Bignum.
let toI (s: string) : int64 =
    let mutable quick = 0L
    if tryFastToI s &quick then
        quick
    else
        let n = toI128Slowly s
        if n < int64Min then Int64.MinValue
        elif n > int64Max then Int64.MaxValue
        else Int64.CreateChecked n

/// `String#to_i`, or `None` where Ruby's answer doesn't fit in an i64.
let toIChecked (s: string) : int64 option =
    let mutable quick = 0L
    if tryFastToI s &quick then
        Some quick
    else
        let n = toI128Slowly s
        if n < int64Min || n > int64Max then None else Some(Int64.CreateChecked n)

/// A string as Active Record binds it for an integer column (`find`, `find_by(id:)`, `where`):
/// `ActiveModel::Type::Integer#serialize`, with the SQLite adapter's 8-byte limit. None unless it
/// starts like a number (`/\A\s*[+-]?\d/`), then `to_i`, and None out of range, where Rails raises
/// (activemodel's `type/integer.rb`).
let integerCast (s: string) : int64 option =
    let mutable i = 0
    while i < s.Length && isSpace s[i] do
        i <- i + 1
    if i < s.Length && (s[i] = '+' || s[i] = '-') then
        i <- i + 1
    if i < s.Length && s[i] >= '0' && s[i] <= '9' then toIChecked s else None
