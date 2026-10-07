// Port of rust/crates/ruby/src/float.rs
module Campfire.Ruby.Float

open System
open System.Globalization
open System.Text
open Campfire.Ruby.RubyString

let inline private at (s: byte[]) (i: int) : byte = if i < s.Length then s[i] else 0uy

let private isHex (s: byte[]) =
    s.Length > 0 && s[0] = byte '0' && s.Length > 1 && (s[1] = byte 'x' || s[1] = byte 'X')

let private isAsciiDigit (b: byte) = b >= byte '0' && b <= byte '9'

/// `char::from(byte).is_digit(base)`, for base 10 or 16.
let private isDigit (b: byte) (radix: int) =
    isAsciiDigit b
    || (radix = 16 && ((b >= byte 'a' && b <= byte 'f') || (b >= byte 'A' && b <= byte 'F')))

let private hexValue (b: byte) : float voption =
    if isAsciiDigit b then ValueSome(float (b - byte '0'))
    elif b >= byte 'a' && b <= byte 'f' then ValueSome(float (b - byte 'a' + 10uy))
    elif b >= byte 'A' && b <= byte 'F' then ValueSome(float (b - byte 'A' + 10uy))
    else ValueNone

/// A number's significand fills at most this much of `rb_cstr_to_dbl`'s buffer (`DBL_DIG * 4`),
/// and it all at most this much (`sizeof(buf) - 1`).
[<Literal>]
let private SignificandWidth = 60

[<Literal>]
let private BufferWidth = 69

/// What `rb_cstr_to_dbl` hands `strtod` a second time when the first read stopped short at `end`:
/// the number copied into a fixed buffer with the underscores between digits left out, up to
/// anything else that can't continue it. Significand characters past the buffer's first 60 are
/// dropped, not scaled, so `"1" + "0" * 70 + "x"` is 1e59.
let private withoutUnderscores (s: byte[]) (``end``: int) : byte[] =
    let out = ResizeArray<byte>(BufferWidth)
    let mutable width = SignificandWidth
    let mutable radix = 10
    let mutable exponentLetter = byte 'e'
    let mutable dotSeen = false
    let mutable previous = 0uy
    let mutable p = 0
    if at s p = byte '+' || at s p = byte '-' then
        previous <- at s p
        out.Add previous
        p <- p + 1
    if at s p = byte '0' then
        previous <- byte '0'
        out.Add(byte '0')
        p <- p + 1
        if at s p = byte 'x' || at s p = byte 'X' then
            previous <- byte 'x'
            out.Add(byte 'x')
            radix <- 16
            exponentLetter <- byte 'p'
            p <- p + 1
        // Successive zeros are squeezed into the one.
        while at s p = byte '0' do
            p <- p + 1
    while p < ``end`` && out.Count < width do
        previous <- s[p]
        out.Add previous
        p <- p + 1
    let mutable go = true
    while go && p < s.Length do
        let mutable skip = false
        if s[p] = byte '_' then
            p <- p + 1
            if out.Count = 0 || not (isDigit previous radix) || not (isDigit (at s p) radix) then
                go <- false
        if go then
            previous <- s[p]
            p <- p + 1
            let lower = if previous >= byte 'A' && previous <= byte 'Z' then previous + 32uy else previous
            if width = SignificandWidth && lower = exponentLetter then
                width <- BufferWidth
                out.Add previous
                if at s p = byte '+' || at s p = byte '-' then
                    previous <- at s p
                    out.Add previous
                    p <- p + 1
                if at s p = byte '0' then
                    previous <- byte '0'
                    out.Add(byte '0')
                    while at s p = byte '0' do
                        p <- p + 1
                radix <- 10
                skip <- true
            elif isSpaceByte previous then
                while isSpaceByte (at s p) do
                    p <- p + 1
                if p < s.Length then
                    go <- false
            elif previous = byte '.' then
                if dotSeen then go <- false else dotSeen <- true
            elif not (isDigit previous radix) then
                go <- false
            if go && not skip && out.Count < width then
                out.Add previous
    out.ToArray()

/// The most significant digits `ruby_strtod` reads of a fraction (`DBL_DIG * 4`): past them
/// the rest is ignored. Zeros count once a digit follows them.
[<Literal>]
let private FractionDigits = 60

/// `ldexp(value, exponent)`: `value` times 2 to the `exponent`, rounded once (musl's `scalbn`).
let private scaleByTwo (value: float) (exponent: int64) : float =
    let powerOfTwo (n: int64) = BitConverter.Int64BitsToDouble((0x3ffL + n) <<< 52)
    let mutable value = value
    let mutable n = exponent
    if n > 1023L then
        value <- value * powerOfTwo 1023L
        n <- n - 1023L
        if n > 1023L then
            value <- value * powerOfTwo 1023L
            n <- min (n - 1023L) 1023L
    elif n < -1022L then
        // Scaled so that the last step, into the subnormals, is the only one that rounds.
        value <- value * (powerOfTwo -1022L * powerOfTwo 53L)
        n <- n + 1022L - 53L
        if n < -1022L then
            value <- value * (powerOfTwo -1022L * powerOfTwo 53L)
            n <- max (n + 1022L - 53L) -1022L
    value * powerOfTwo n

/// The exponent at `s[e]` (the `e`), and where it ends; `s[e]` itself when no digit follows. Past
/// 19999 it's 19999, as in Ruby.
let private strtodExponent (s: byte[]) (e: int) : int * int =
    let mutable i = e + 1
    let negative = at s i = byte '-'
    if at s i = byte '+' || at s i = byte '-' then
        i <- i + 1
    if not (isAsciiDigit (at s i)) then
        (0, e)
    else
        while at s i = byte '0' do
            i <- i + 1
        let start = i
        let mutable exponent = 0L
        while isAsciiDigit (at s i) do
            exponent <- min (exponent * 10L + int64 (at s i - byte '0')) (Int64.MaxValue / 20L)
            i <- i + 1
        let exponent = if i - start > 8 || exponent > 19999L then 19999 else int exponent
        ((if negative then -exponent else exponent), i)

/// `ruby_strtod`'s hexadecimal branch, from the digits after `0x` at `start`: hex digits, a
/// fraction and a binary exponent (`p`), added up as doubles the way Ruby does, then scaled.
let private hexStrtod (s: byte[]) (start: int) (negative: bool) : float * int =
    let signed (value: float) = if negative then -value else value
    let mutable i = start
    if (hexValue (at s i)).IsNone && at s i <> byte '.' then
        (0.0, 0)
    else
        let mutable sum = 0.0
        let mutable weight = 1.0
        let mutable exponent = -4L
        while at s i = byte '0' do
            i <- i + 1
        if i = s.Length then
            (signed 0.0, i)
        else
            let mutable reading = true
            while reading do
                match hexValue (at s i) with
                | ValueSome digit ->
                    sum <- sum + weight * digit
                    exponent <- exponent + 4L
                    weight <- weight / 16.0
                    i <- i + 1
                | ValueNone -> reading <- false
            if at s i = byte '.' then
                i <- i + 1
                if (hexValue (at s i)).IsSome then
                    if exponent < 0L then
                        while at s i = byte '0' do
                            i <- i + 1
                            exponent <- exponent - 4L
                    let mutable more = true
                    while more do
                        match hexValue (at s i) with
                        | ValueSome digit ->
                            sum <- sum + weight * digit
                            i <- i + 1
                            weight <- weight / 16.0
                            if weight = 0.0 then
                                while (hexValue (at s i)).IsSome do
                                    i <- i + 1
                                more <- false
                        | ValueNone -> more <- false
            let mutable failed = false
            if at s i = byte 'p' || at s i = byte 'P' then
                i <- i + 1
                let sign =
                    match at s i with
                    | b when b = byte '-' -> -1L
                    | b when b = byte '+' -> 1L
                    | _ -> 0L
                if sign <> 0L then
                    i <- i + 1
                let sign = if sign = 0L then 1L else sign
                if not (isAsciiDigit (at s i)) then
                    failed <- true
                else
                    let mutable power = 0L
                    let mutable reading = true
                    while reading && isAsciiDigit (at s i) do
                        power <- power * 10L + int64 (at s i - byte '0')
                        i <- i + 1
                        // Ruby stops reading the exponent past this, where any significand overflows.
                        if power + sign * exponent > 2095L then
                            while isAsciiDigit (at s i) do
                                i <- i + 1
                            reading <- false
                    exponent <- exponent + power * sign
            if failed then (0.0, 0) else (signed (scaleByTwo sum exponent), i)

/// `ruby_strtod` (David Gay's, in Ruby's missing/dtoa.c): the number at the start of `s`, and how
/// much of `s` it takes up (0 for none). .NET parses the digits it reads; both round correctly.
let private strtod (s: byte[]) : float * int =
    let mutable i = 0
    while isSpaceByte (at s i) do
        i <- i + 1
    let negative = at s i = byte '-'
    if at s i = byte '+' || at s i = byte '-' then
        i <- i + 1
    if i = s.Length then
        (0.0, 0)
    elif at s i = byte '0' && (at s (i + 1) = byte 'x' || at s (i + 1) = byte 'X') then
        hexStrtod s (i + 2) negative
    else
        let signedZero = if negative then -0.0 else 0.0
        let leadingZero = at s i = byte '0'
        while at s i = byte '0' do
            i <- i + 1
        if leadingZero && i = s.Length then
            (signedZero, i)
        else
            let integerStart = i
            while isAsciiDigit (at s i) do
                i <- i + 1
            let integer = s[integerStart .. i - 1]
            let mutable digits = integer.Length
            let fraction = ResizeArray<byte>()
            let mutable fractionZeros = false
            if at s i = byte '.' && isAsciiDigit (at s (i + 1)) then
                i <- i + 1
                let mutable zeros = 0
                while isAsciiDigit (at s i) do
                    let digit = at s i
                    i <- i + 1
                    if digits > FractionDigits then
                        ()
                    elif digit = byte '0' then
                        zeros <- zeros + 1
                        fractionZeros <- true
                    else
                        for _ in 1..zeros do
                            fraction.Add(byte '0')
                        fraction.Add digit
                        digits <- digits + (if digits = 0 then 1 else zeros + 1)
                        zeros <- 0
            elif at s i = byte '.' then
                i <- i + 1
            let anyDigits = digits > 0 || fractionZeros || leadingZero

            let mutable exponent = 0
            let mutable early = false
            if at s i = byte 'e' || at s i = byte 'E' then
                if not anyDigits then
                    early <- true
                else
                    let (e, next) = strtodExponent s i
                    exponent <- e
                    i <- next
            if early then
                (0.0, 0)
            elif digits = 0 then
                if anyDigits then (signedZero, i) else (0.0, 0)
            else
                let number = StringBuilder(integer.Length + fraction.Count + 8)
                if negative then
                    number.Append '-' |> ignore
                if integer.Length = 0 then
                    number.Append '0' |> ignore
                else
                    for b in integer do
                        number.Append(char b) |> ignore
                if fraction.Count > 0 then
                    number.Append '.' |> ignore
                    for b in fraction do
                        number.Append(char b) |> ignore
                number.Append('e').Append(exponent) |> ignore
                match Double.TryParse(number.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture) with
                | true, v -> (v, i)
                | _ -> (signedZero, i)

/// `String#to_f` (`rb_cstr_to_dbl` in Ruby 3.4's object.c, which reads up to the first NUL): the
/// number after any leading whitespace, and 0.0 when there's none. An underscore between two
/// digits is skipped, so `"1_000.5"` is 1000.5; `"1.2.3"` is 1.2 and `"1e2"` 100.0. Hexadecimal
/// is read only after a sign: `"-0x1A"` is -26.0, and `"0x1A"` 0.0.
let toF (str: string) : float =
    let nul = str.IndexOf '\000'
    let head = if nul >= 0 then str.Substring(0, nul) else str
    let all = Encoding.UTF8.GetBytes head
    let mutable skip = 0
    while skip < all.Length && isSpaceByte all[skip] do
        skip <- skip + 1
    let s = if skip = 0 then all else all[skip..]
    if isHex s then
        0.0
    else
        let (value, ``end``) = strtod s
        if ``end`` = 0 || ``end`` = s.Length then
            value
        else
            let number = withoutUnderscores s ``end``
            if isHex number then 0.0 else fst (strtod number)

/// The digits of `1.2345e6` and where the decimal point goes in them (7).
let private scientificDigits (formatted: string) : string * int =
    let e = formatted.IndexOf 'E'
    let mantissa = formatted.Substring(0, e)
    let exponent = Int32.Parse(formatted.Substring(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
    let digits = String(mantissa.ToCharArray() |> Array.filter Char.IsAsciiDigit)
    (digits, exponent + 1)

/// The shortest digits that round-trip, as `{:e}` writes them in Rust, and where the decimal
/// point goes in them.
let private shortestRoundTrip (magnitude: float) : string * int =
    let r = magnitude.ToString("R", CultureInfo.InvariantCulture)
    let roundTrips (text: string) =
        Double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture) = magnitude
    if roundTrips r then
        let e = r.IndexOf 'E'
        let mantissa, exponent =
            if e < 0 then r, 0
            else r.Substring(0, e), Int32.Parse(r.Substring(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
        let dot = mantissa.IndexOf '.'
        let integerLength = if dot < 0 then mantissa.Length else dot
        let raw = mantissa.Replace(".", "")
        let trimmed = raw.TrimStart '0'
        let leadingZeros = raw.Length - trimmed.Length
        (trimmed.TrimEnd '0', integerLength - leadingZeros + exponent)
    else
        // .NET's "R" can write a form that reads back as the neighbour below at a power of two,
        // where the interval is lopsided (2^-25 as 2.980232238769531e-08). Take the first
        // correctly rounded length that does read back.
        let mantissa = (r.Split 'E').[0]
        let significand = mantissa.Replace(".", "").TrimStart '0'
        let candidates =
            seq {
                for n in significand.Length + 1 .. 17 do
                    yield magnitude.ToString("E" + string (n - 1), CultureInfo.InvariantCulture)
            }
        let (d, point) = scientificDigits (candidates |> Seq.find roundTrips)
        (d.TrimEnd '0', point)

/// The shortest digits that read back as `magnitude`, and where the decimal point goes in them.
/// When two such forms are equally close, Ruby's dtoa takes the even one and Rust the upper one:
/// `667020902720176.25.to_s` is "667020902720176.2". Those ties take 16 or 17 digits, and
/// fixed-precision formatting breaks them to even.
let private shortestDigits (magnitude: float) : string * int =
    let (digits, _) as shortest = shortestRoundTrip magnitude
    if digits.Length >= 16 && "13579".Contains digits[digits.Length - 1] then
        let even = magnitude.ToString("E" + string (digits.Length - 1), CultureInfo.InvariantCulture)
        if Double.Parse(even, NumberStyles.Float, CultureInfo.InvariantCulture) = magnitude then
            scientificDigits even
        else
            shortest
    else
        shortest

/// `Float#to_s`: plain decimals from 1e-4 up to (not including) 1e15, and above that while the
/// shortest digits still reach past the decimal point (`1000000000000000.1`); the exponent form
/// otherwise (`flo_to_s` in Ruby 3.4's numeric.c).
let floatToS (f: float) : string =
    if Double.IsNaN f then "NaN"
    elif Double.IsInfinity f then (if f > 0.0 then "Infinity" else "-Infinity")
    elif f = 0.0 then (if Double.IsNegative f then "-0.0" else "0.0")
    else
        let (digits, decpt) = shortestDigits (abs f)
        let sign = if f < 0.0 then "-" else ""
        if decpt < -3 || (decpt > 15 && digits.Length <= decpt) then
            let first = digits.Substring(0, 1)
            let rest = if digits.Length = 1 then "0" else digits.Substring 1
            let e = decpt - 1
            let esign = if e < 0 then '-' else '+'
            $"{sign}{first}.{rest}e{esign}{(abs e):D2}"
        elif decpt <= 0 then
            $"{sign}0.{String('0', -decpt)}{digits}"
        elif decpt >= digits.Length then
            $"{sign}{digits}{String('0', decpt - digits.Length)}.0"
        else
            $"{sign}{digits.Substring(0, decpt)}.{digits.Substring decpt}"
