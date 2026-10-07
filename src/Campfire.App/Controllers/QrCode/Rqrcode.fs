// Port of rust/crates/campfire/src/controllers/qr_code/rqrcode.rs
//
// A port of rqrcode_core 2.1.0 and rqrcode 3.2.0's `as_svg` (Rect output), for
// `QrCodeController#show` (reference/app/controllers/qr_code_controller.rb):
// `RQRCode::QRCode.new(url).as_svg(viewbox: true, fill: :white, color: :black)`.
//
// The output has to be byte-identical to the gem's, so this follows its algorithms rather than
// the QR spec's optimal choices: a single segment whose mode is numeric, alphanumeric or 8-bit
// byte (in that order of preference), error correction level H, the smallest version whose
// capacity is *strictly* greater than the segment's bits, and the mask with the fewest "lost
// points" as the gem scores them (including its floating-point dark-ratio term).
// Golden vectors: `reference-tools/campfire/rqrcode.rb`.
namespace Campfire.App.Controllers

open System
open System.Text

module Rqrcode =
    type Mode =
        | Number = 1
        | AlphaNumeric = 2
        | Byte = 4

    let private alphanumeric = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:"B

    /// `QRERRORCORRECTLEVEL[:h]`
    [<Literal>]
    let private LevelH = 2

    /// `QRMAXBITS[:h]`
    let private maxBitsH =
        [| 72; 128; 208; 288; 368; 480; 528; 688; 800; 976; 1120; 1264; 1440; 1576; 1784; 2024; 2264; 2504; 2728; 3080; 3248; 3536; 3712; 4112
           4304; 4768; 5024; 5288; 5608; 5960; 6344; 6760; 7208; 7688; 7888; 8432; 8768; 9136; 9776; 10208 |]

    /// The H rows of `QRRSBlock::RS_BLOCK_TABLE`: (count, total, data) groups.
    let private rsBlocksH: int[][] =
        [| [| 1; 26; 9 |]
           [| 1; 44; 16 |]
           [| 2; 35; 13 |]
           [| 4; 25; 9 |]
           [| 2; 33; 11; 2; 34; 12 |]
           [| 4; 43; 15 |]
           [| 4; 39; 13; 1; 40; 14 |]
           [| 4; 40; 14; 2; 41; 15 |]
           [| 4; 36; 12; 4; 37; 13 |]
           [| 6; 43; 15; 2; 44; 16 |]
           [| 3; 36; 12; 8; 37; 13 |]
           [| 7; 42; 14; 4; 43; 15 |]
           [| 12; 33; 11; 4; 34; 12 |]
           [| 11; 36; 12; 5; 37; 13 |]
           [| 11; 36; 12; 7; 37; 13 |]
           [| 3; 45; 15; 13; 46; 16 |]
           [| 2; 42; 14; 17; 43; 15 |]
           [| 2; 42; 14; 19; 43; 15 |]
           [| 9; 39; 13; 16; 40; 14 |]
           [| 15; 43; 15; 10; 44; 16 |]
           [| 19; 46; 16; 6; 47; 17 |]
           [| 34; 37; 13 |]
           [| 16; 45; 15; 14; 46; 16 |]
           [| 30; 46; 16; 2; 47; 17 |]
           [| 22; 45; 15; 13; 46; 16 |]
           [| 33; 46; 16; 4; 47; 17 |]
           [| 12; 45; 15; 28; 46; 16 |]
           [| 11; 45; 15; 31; 46; 16 |]
           [| 19; 45; 15; 26; 46; 16 |]
           [| 23; 45; 15; 25; 46; 16 |]
           [| 23; 45; 15; 28; 46; 16 |]
           [| 19; 45; 15; 35; 46; 16 |]
           [| 11; 45; 15; 46; 46; 16 |]
           [| 59; 46; 16; 1; 47; 17 |]
           [| 22; 45; 15; 41; 46; 16 |]
           [| 2; 45; 15; 64; 46; 16 |]
           [| 24; 45; 15; 46; 46; 16 |]
           [| 42; 45; 15; 32; 46; 16 |]
           [| 10; 45; 15; 67; 46; 16 |]
           [| 20; 45; 15; 61; 46; 16 |] |]

    /// `QRUtil::PATTERN_POSITION_TABLE`
    let private patternPositions: int[][] =
        [| [||]
           [| 6; 18 |]
           [| 6; 22 |]
           [| 6; 26 |]
           [| 6; 30 |]
           [| 6; 34 |]
           [| 6; 22; 38 |]
           [| 6; 24; 42 |]
           [| 6; 26; 46 |]
           [| 6; 28; 50 |]
           [| 6; 30; 54 |]
           [| 6; 32; 58 |]
           [| 6; 34; 62 |]
           [| 6; 26; 46; 66 |]
           [| 6; 26; 48; 70 |]
           [| 6; 26; 50; 74 |]
           [| 6; 30; 54; 78 |]
           [| 6; 30; 56; 82 |]
           [| 6; 30; 58; 86 |]
           [| 6; 34; 62; 90 |]
           [| 6; 28; 50; 72; 94 |]
           [| 6; 26; 50; 74; 98 |]
           [| 6; 30; 54; 78; 102 |]
           [| 6; 28; 54; 80; 106 |]
           [| 6; 32; 58; 84; 110 |]
           [| 6; 30; 58; 86; 114 |]
           [| 6; 34; 62; 90; 118 |]
           [| 6; 26; 50; 74; 98; 122 |]
           [| 6; 30; 54; 78; 102; 126 |]
           [| 6; 26; 52; 78; 104; 130 |]
           [| 6; 30; 56; 82; 108; 134 |]
           [| 6; 34; 60; 86; 112; 138 |]
           [| 6; 30; 58; 86; 114; 142 |]
           [| 6; 34; 62; 90; 118; 146 |]
           [| 6; 30; 54; 78; 102; 126; 150 |]
           [| 6; 24; 50; 76; 102; 128; 154 |]
           [| 6; 28; 54; 80; 106; 132; 158 |]
           [| 6; 32; 58; 84; 110; 136; 162 |]
           [| 6; 26; 54; 82; 110; 138; 166 |]
           [| 6; 30; 58; 86; 114; 142; 170 |] |]

    [<Literal>]
    let private G15 = 0b10100110111u // (1<<10)|(1<<8)|(1<<5)|(1<<4)|(1<<2)|(1<<1)|1

    [<Literal>]
    let private G18 = 0b1111100100101u // (1<<12)|(1<<11)|(1<<10)|(1<<9)|(1<<8)|(1<<5)|(1<<2)|1

    [<Literal>]
    let private G15Mask = 0b101010000010010u // (1<<14)|(1<<12)|(1<<10)|(1<<4)|(1<<1)

    /// `QRUtil.get_length_in_bits`
    let private lengthInBits (mode: Mode) (version: int) : int =
        let macroVersion =
            if version >= 1 && version <= 9 then 0
            elif version >= 10 && version <= 26 then 1
            else 2
        match mode with
        | Mode.Number -> [| 10; 12; 14 |][macroVersion]
        | Mode.AlphaNumeric -> [| 9; 11; 13 |][macroVersion]
        | _ -> [| 8; 16; 16 |][macroVersion]

    /// `QRBitBuffer`
    [<Sealed>]
    type BitBuffer(version: int) =
        let buffer = ResizeArray<byte>()
        let mutable length = 0

        member _.Version = version
        member _.Buffer = buffer
        member _.Length = length

        member _.PutBit(bit: bool) : unit =
            let index = length / 8
            if buffer.Count <= index then buffer.Add 0uy
            if bit then buffer[index] <- buffer[index] ||| byte (0x80 >>> (length % 8))
            length <- length + 1

        member this.Put(num: uint32, count: int) : unit =
            for i in 0 .. count - 1 do
                this.PutBit(((num >>> (count - i - 1)) &&& 1u) = 1u)

        member this.EndOfMessage(maxDataBits: int) : unit =
            if length + 4 <= maxDataBits then this.Put(0u, 4)

        member this.PadUntil(preferredSize: int) : unit =
            while length % 8 <> 0 do
                this.PutBit false
            while length < preferredSize do
                this.Put(0xECu, 8)
                if length < preferredSize then this.Put(0x11u, 8)

    /// `QRSegment`: the whole input in one mode.
    type Segment = { Data: byte[]; Mode: Mode }

    module Segment =
        /// `QRSegment.new(data:)` without a mode: numeric, then alphanumeric, then 8-bit byte. The
        /// gem checks `data.chars`; every valid character is ASCII, so checking bytes is the same.
        let create (data: byte[]) : Segment =
            let mode =
                if data |> Array.forall (fun b -> b >= byte '0' && b <= byte '9') then Mode.Number
                elif data |> Array.forall (fun b -> Array.contains b alphanumeric) then Mode.AlphaNumeric
                else Mode.Byte
            { Data = data; Mode = mode }

        let private contentSize (segment: Segment) : int =
            let length = segment.Data.Length
            let chunk, bits, extra =
                match segment.Mode with
                | Mode.Number -> 3, 10, [| 0; 4; 7 |][length % 3]
                | Mode.AlphaNumeric -> 2, 11, 6
                | _ -> 1, 8, 0
            (length / chunk) * bits + (if length % chunk = 0 then 0 else extra)

        /// `QRSegment#size(version)`: bits needed, including the mode indicator and length.
        let size (segment: Segment) (version: int) : int = 4 + lengthInBits segment.Mode version + contentSize segment

        let write (segment: Segment) (buffer: BitBuffer) : unit =
            buffer.Put(uint32 segment.Mode, 4)
            buffer.Put(uint32 segment.Data.Length, lengthInBits segment.Mode buffer.Version)
            match segment.Mode with
            | Mode.Number ->
                for chunk in Array.chunkBySize 3 segment.Data do
                    let code = chunk |> Array.fold (fun acc b -> acc * 10u + uint32 (b - byte '0')) 0u
                    buffer.Put(code, [| 0; 4; 7; 10 |][chunk.Length])
            | Mode.AlphaNumeric ->
                let index (b: byte) = uint32 (Array.IndexOf(alphanumeric, b))
                for pair in Array.chunkBySize 2 segment.Data do
                    if pair.Length = 2 then buffer.Put(index pair[0] * 45u + index pair[1], 11) else buffer.Put(index pair[0], 6)
            | _ ->
                for b in segment.Data do
                    buffer.Put(uint32 b, 8)

    /// `QRMath`: GF(256) exp/log tables.
    type Galois =
        { Exp: uint32[]
          Log: uint32[] }

    module Galois =
        let create () : Galois =
            let exp = Array.zeroCreate<uint32> 256
            let log = Array.zeroCreate<uint32> 256
            for i in 0..7 do
                exp[i] <- 1u <<< i
            for i in 8..255 do
                exp[i] <- exp[i - 4] ^^^ exp[i - 5] ^^^ exp[i - 6] ^^^ exp[i - 8]
            for i in 0..254 do
                log[int exp[i]] <- uint32 i
            { Exp = exp; Log = log }

        let glog (gf: Galois) (n: uint32) : int64 =
            if n < 1u then failwith $"glog({n})"
            int64 gf.Log[int n]

        let gexp (gf: Galois) (n: int64) : uint32 =
            let mutable n = n
            while n < 0L do
                n <- n + 255L
            while n >= 256L do
                n <- n - 255L
            gf.Exp[int n]

    /// `QRPolynomial`. The gem pads with `nil`s where this pads with zeros; they only differ in
    /// inputs where the gem raises.
    type Polynomial = Polynomial of uint32[]

    module Polynomial =
        let create (num: uint32[]) (shift: int) : Polynomial =
            let offset = num |> Array.takeWhile (fun n -> n = 0u) |> Array.length
            let values = Array.zeroCreate<uint32> (num.Length - offset + shift)
            Array.blit num offset values 0 (num.Length - offset)
            Polynomial values

        let values (Polynomial v) = v

        let multiply (Polynomial a) (Polynomial b) (gf: Galois) : Polynomial =
            let num = Array.zeroCreate<uint32> (a.Length + b.Length - 1)
            for i in 0 .. a.Length - 1 do
                for j in 0 .. b.Length - 1 do
                    num[i + j] <- num[i + j] ^^^ Galois.gexp gf (Galois.glog gf a[i] + Galois.glog gf b[j])
            create num 0

        let modulo (self: Polynomial) (Polynomial other) (gf: Galois) : Polynomial =
            let mutable current = self
            let mutable result = None
            while result.IsNone do
                let (Polynomial c) = current
                if c.Length < other.Length then
                    result <- Some current
                else
                    let ratio = Galois.glog gf c[0] - Galois.glog gf other[0]
                    let num = Array.copy c
                    for i in 0 .. other.Length - 1 do
                        num[i] <- num[i] ^^^ Galois.gexp gf (Galois.glog gf other[i] + ratio)
                    current <- create num 0
            result.Value

    /// `QRUtil.get_error_correct_polynomial`
    let private errorCorrectPolynomial (length: int) (gf: Galois) : Polynomial =
        let mutable a = Polynomial.create [| 1u |] 0
        for i in 0 .. length - 1 do
            a <- Polynomial.multiply a (Polynomial.create [| 1u; Galois.gexp gf (int64 i) |] 0) gf
        a

    /// `QRCode.create_data`: the data and error correction codewords, interleaved.
    let private createData (version: int) (segment: Segment) : byte[] =
        let blocks =
            rsBlocksH[version - 1]
            |> Array.chunkBySize 3
            |> Array.collect (fun group -> Array.create group[0] (group[1], group[2]))
        let maxDataBits = (blocks |> Array.sumBy snd) * 8

        let buffer = BitBuffer version
        Segment.write segment buffer
        buffer.EndOfMessage maxDataBits
        if buffer.Length > maxDataBits then failwith "code length overflow"
        buffer.PadUntil maxDataBits

        let gf = Galois.create ()
        let mutable offset = 0
        let dcData = ResizeArray<uint32[]>()
        let ecData = ResizeArray<uint32[]>()
        for (total, dataCount) in blocks do
            let ecCount = total - dataCount
            let dc = [| for i in offset .. offset + dataCount - 1 -> uint32 buffer.Buffer[i] |]
            offset <- offset + dataCount
            let rsPoly = errorCorrectPolynomial ecCount gf
            let ecLength = (Polynomial.values rsPoly).Length - 1
            let modPoly = Polynomial.modulo (Polynomial.create dc ecLength) rsPoly gf |> Polynomial.values
            let ec =
                [| for i in 0 .. ecLength - 1 ->
                       let index = i + modPoly.Length - ecLength
                       if index >= 0 then modPoly[index] else 0u |]
            dcData.Add dc
            ecData.Add ec

        let data = ResizeArray<byte>()
        for codewords in [ dcData; ecData ] do
            let longest = if codewords.Count = 0 then 0 else codewords |> Seq.map Array.length |> Seq.max
            for i in 0 .. longest - 1 do
                for block in codewords do
                    if i < block.Length then data.Add(byte block[i])
        data.ToArray()

    /// `QRUtil.get_bch_digit`
    let private bchDigit (data: uint32) : int =
        let mutable digit = 0
        let mutable data = data
        while data <> 0u do
            digit <- digit + 1
            data <- data >>> 1
        digit

    let private bchFormatInfo (data: uint32) : uint32 =
        let mutable d = data <<< 10
        while bchDigit d - bchDigit G15 >= 0 do
            d <- d ^^^ (G15 <<< (bchDigit d - bchDigit G15))
        ((data <<< 10) ||| d) ^^^ G15Mask

    let private bchVersion (data: uint32) : uint32 =
        let mutable d = data <<< 12
        while bchDigit d - bchDigit G18 >= 0 do
            d <- d ^^^ (G18 <<< (bchDigit d - bchDigit G18))
        (data <<< 12) ||| d

    /// `QRMASKCOMPUTATIONS`
    let private mask (pattern: int) (i: int) (j: int) : bool =
        match pattern with
        | 0 -> (i + j) % 2 = 0
        | 1 -> i % 2 = 0
        | 2 -> j % 3 = 0
        | 3 -> (i + j) % 3 = 0
        | 4 -> (i / 2 + j / 3) % 2 = 0
        | 5 -> ((i * j) % 2 + (i * j) % 3) = 0
        | 6 -> ((i * j) % 2 + (i * j) % 3) % 2 = 0
        | 7 -> ((i * j) % 3 + (i + j) % 2) % 2 = 0
        | _ -> failwith "unreachable"

    /// A module not yet placed is `-1`; placed ones are `0` (light) or `1` (dark).
    type private Grid = sbyte[][]

    let private cell (dark: bool) : sbyte = if dark then 1y else 0y

    /// `minimum_version`: the first version whose capacity is strictly greater than the bits needed,
    /// or `None` where rqrcode raises "Data length exceed maximum capacity of version 40".
    let minimumVersion (segment: Segment) : int option =
        seq { 1..40 } |> Seq.tryFind (fun version -> Segment.size segment version < maxBitsH[version - 1])

    let private placePositionProbePattern (grid: Grid) (row: int) (col: int) : unit =
        let count = grid.Length
        for r in -1..7 do
            let y = row + r
            if y >= 0 && y < count then
                for c in -1..7 do
                    let x = col + c
                    if x >= 0 && x < count then
                        let vertical = r >= 0 && r <= 6 && (c = 0 || c = 6)
                        let horizontal = c >= 0 && c <= 6 && (r = 0 || r = 6)
                        let square = r >= 2 && r <= 4 && c >= 2 && c <= 4
                        grid[y][x] <- cell (vertical || horizontal || square)

    let private placePositionAdjustPattern (grid: Grid) (version: int) : unit =
        let positions = patternPositions[version - 1]
        for row in positions do
            for col in positions do
                if grid[row][col] = -1y then
                    for r in -2..2 do
                        for c in -2..2 do
                            let part = abs r = 2 || abs c = 2 || (r = 0 && c = 0)
                            grid[row + r][col + c] <- cell part

    let private placeTimingPattern (grid: Grid) : unit =
        let count = grid.Length
        for i in 8 .. count - 9 do
            grid[i][6] <- cell (i % 2 = 0)
        for i in 8 .. count - 9 do
            grid[6][i] <- cell (i % 2 = 0)

    let private placeVersionInfo (grid: Grid) (version: int) (test: bool) : unit =
        let count = grid.Length
        let bits = bchVersion (uint32 version)
        for i in 0..17 do
            let dark = not test && ((bits >>> i) &&& 1u) = 1u
            grid[i / 3][i % 3 + count - 8 - 3] <- cell dark
            grid[i % 3 + count - 8 - 3][i / 3] <- cell dark

    let private placeFormatInfo (grid: Grid) (test: bool) (pattern: int) : unit =
        let count = grid.Length
        let bits = bchFormatInfo (uint32 ((LevelH <<< 3) ||| pattern))
        for i in 0..14 do
            let dark = not test && ((bits >>> i) &&& 1u) = 1u
            let row =
                if i < 6 then i
                elif i < 8 then i + 1
                else count - 15 + i
            grid[row][8] <- cell dark
            let col =
                if i < 8 then count - i - 1
                elif i < 9 then 15 - i
                else 15 - i - 1
            grid[8][col] <- cell dark
        grid[count - 8][8] <- cell (not test)

    let private mapData (grid: Grid) (data: byte[]) (pattern: int) : unit =
        let count = grid.Length
        let mutable inc = -1
        let mutable row = count - 1
        let mutable bitIndex = 7
        let mutable byteIndex = 0
        let mutable col = count - 1
        while col >= 1 do
            let c0 = if col <= 6 then col - 1 else col
            let mutable going = true
            while going do
                for c in 0..1 do
                    let x = c0 - c
                    let y = row
                    if grid[y][x] = -1y then
                        let mutable dark = byteIndex < data.Length && ((data[byteIndex] >>> bitIndex) &&& 1uy) = 1uy
                        if mask pattern y x then dark <- not dark
                        grid[y][x] <- cell dark
                        bitIndex <- bitIndex - 1
                        if bitIndex = -1 then
                            byteIndex <- byteIndex + 1
                            bitIndex <- 7
                row <- row + inc
                if row < 0 || count <= row then
                    row <- row - inc
                    inc <- -inc
                    going <- false
            col <- col - 2

    /// `QRUtil.get_lost_points`. The dark-ratio term is a Float in Ruby, so the total is too.
    let private lostPoints (modules: bool[][]) : float =
        let count = modules.Length
        let max = count - 1
        let mutable points = 0L
        let one (b: bool) = if b then 1L else 0L

        // Same-color neighbours.
        for row in 0..max do
            for col in 0..max do
                let dark = modules[row][col]
                let mutable same = 0L
                if row > 0 then
                    let above = modules[row - 1]
                    same <- same + one (col > 0 && dark = above[col - 1])
                    same <- same + one (dark = above[col])
                    same <- same + one (col < max && dark = above[col + 1])
                same <- same + one (col > 0 && dark = modules[row][col - 1])
                same <- same + one (col < max && dark = modules[row][col + 1])
                if row < max then
                    let below = modules[row + 1]
                    same <- same + one (col > 0 && dark = below[col - 1])
                    same <- same + one (dark = below[col])
                    same <- same + one (col < max && dark = below[col + 1])
                if same > 5L then points <- points + 3L + same - 5L

        // 2x2 blocks.
        for row in 0 .. max - 1 do
            for col in 0 .. max - 1 do
                let value = modules[row][col]
                if value = modules[row + 1][col] && value = modules[row][col + 1] && value = modules[row + 1][col + 1] then
                    points <- points + 3L

        // 1:1:3:1:1 patterns, in rows then columns.
        let finder (cell: int -> bool) =
            cell 0 && not (cell 1) && cell 2 && cell 3 && cell 4 && not (cell 5) && cell 6
        for start in 0 .. (Math.Max(count - 6, 0)) - 1 do
            for line in 0 .. count - 1 do
                let row = modules[line]
                if finder (fun k -> row[start + k]) then points <- points + 40L
                if finder (fun k -> modules[start + k][line]) then points <- points + 40L

        // Dark ratio.
        let dark = modules |> Array.sumBy (fun row -> row |> Array.sumBy (fun m -> if m then 1 else 0))
        let ratio = float dark / float (count * count)
        let delta = Math.Abs(100.0 * ratio - 50.0) / 5.0
        float points + delta * 10.0

    /// `RQRCodeCore::QRCode`: the modules of the code for `data`, or `None` when it doesn't fit a
    /// version 40 code.
    let modules (data: byte[]) : bool[][] option =
        let segment = Segment.create data
        match minimumVersion segment with
        | None -> None
        | Some version ->
            let count = version * 4 + 17
            let common: Grid = Array.init count (fun _ -> Array.create count -1y)
            placePositionProbePattern common 0 0
            placePositionProbePattern common (count - 7) 0
            placePositionProbePattern common 0 (count - 7)
            placePositionAdjustPattern common version
            placeTimingPattern common

            let codewords = createData version segment
            let make (test: bool) (pattern: int) : bool[][] =
                let grid: Grid = common |> Array.map Array.copy
                placeFormatInfo grid test pattern
                if version >= 7 then placeVersionInfo grid version test
                mapData grid codewords pattern
                grid |> Array.map (fun row -> row |> Array.map (fun m -> m = 1y))

            // `get_best_mask_pattern`: the first pattern with the fewest lost points.
            let mutable best = 0, Double.MaxValue
            for pattern in 0..7 do
                let points = lostPoints (make true pattern)
                if pattern = 0 || snd best > points then best <- pattern, points
            Some(make false (fst best))

    /// `RQRCode::QRCode.new(data).as_svg(viewbox: true, fill: :white, color: :black)`, for the binary
    /// string `Base64.urlsafe_decode64` returns; `None` when it doesn't fit a version 40 code.
    let svgBytes (data: byte[]) : string option =
        match modules data with
        | None -> None
        | Some modules ->
            let moduleSize = 11
            let dimension = modules.Length * moduleSize
            let out = StringBuilder(256 + modules.Length * modules.Length * 30)
            out.Append("""<?xml version="1.0" standalone="yes"?>""") |> ignore
            out.Append(
                $"""<svg version="1.1" xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" xmlns:ev="http://www.w3.org/2001/xml-events" viewBox="0 0 {dimension} {dimension}" shape-rendering="crispEdges">"""
            )
            |> ignore
            out.Append($"""<rect width="{dimension}" height="{dimension}" x="0" y="0" fill="white"/>""") |> ignore
            modules
            |> Array.iteri (fun row cells ->
                cells
                |> Array.iteri (fun col dark ->
                    if dark then
                        let x, y = col * moduleSize, row * moduleSize
                        out.Append($"""<rect width="{moduleSize}" height="{moduleSize}" x="{x}" y="{y}" fill="black"/>""") |> ignore))
            out.Append "</svg>" |> ignore
            Some(out.ToString())
