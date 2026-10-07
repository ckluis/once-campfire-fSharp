// Port of rust/crates/storage/src/analyze.rs
/// The Active Storage analyzers Campfire runs, in `config.active_storage.analyzers` order:
/// `ImageAnalyzer::Vips`, `VideoAnalyzer` and `AudioAnalyzer` (ImageMagick never accepts,
/// because the variant processor is `:vips`), falling back to `NullAnalyzer`.
module Campfire.Storage.Analyze

open System
open System.Diagnostics
open System.Globalization
open System.Text
open Campfire.RailsCompat
open Campfire.Storage.Process

/// How long ffprobe may take to read a file's streams before it's killed. Rails sets no limit.
let ffprobeTimeout : TimeSpan = TimeSpan.FromSeconds 30.0

type Analyzer =
    | Image
    | Video
    | Audio
    | Null

let private analyzeError (message: string) : StorageError = StorageError.Analyze message

/// `ImageAnalyzer::Vips#metadata`: dimensions, swapped for EXIF orientations that rotate by 90 degrees.
/// Files libvips can't read yield `{}`.
let private imageMetadata (path: string) : Value =
    match Vips.Image.OpenSequential path with
    | Error _ -> JsonValue.object
    | Ok image ->
        use image = image
        let rotated =
            image.GetString "exif-ifd0-Orientation"
            |> Option.exists (fun o ->
                [ "Right-top"; "Left-bottom"; "Top-right"; "Bottom-left" ] |> List.exists (fun r -> o.Contains(r, StringComparison.Ordinal)))
        let width, height = if rotated then image.Height, image.Width else image.Width, image.Height
        Value.Object [ "width", Value.Int(int64 width); "height", Value.Int(int64 height) ]

/// `ffprobe -print_format json -show_streams -show_format -v error <path>`; `{}` without ffprobe.
let private probe (path: string) : StorageResult<Value> =
    let info = ProcessStartInfo(ContentTypes.ffprobePath ())
    for argument in [ "-print_format"; "json"; "-show_streams"; "-show_format"; "-v"; "error"; path ] do
        info.ArgumentList.Add argument
    match outputWithin info ffprobeTimeout with
    | Error(ProcessError.NotFound _) -> Ok JsonValue.object
    | Error(ProcessError.TimedOut message) -> Error(analyzeError $"ffprobe {message}")
    | Error error -> Error(ProcessError.toStorageError error)
    | Ok output ->
        match JsonValue.parse (Encoding.UTF8.GetString output.Stdout) with
        | Some json -> Ok json
        | None -> Error(analyzeError "ffprobe output: invalid JSON")

let private streams (probe: Value) : Value list =
    match probe.TryGet "streams" with
    | Some(Value.Array streams) -> streams
    | _ -> []

let private stream (probe: Value) (codecType: string) : Value option =
    streams probe |> List.tryFind (fun s -> (s.TryGet "codec_type" |> Option.bind (fun v -> v.AsString)) = Some codecType)

let private present (stream: Value option) : bool =
    match stream with
    | Some(Value.Object entries) -> not (List.isEmpty entries)
    | _ -> false

/// Ruby's `Float(value)` for the numbers and numeric strings ffprobe emits.
let private rubyFloat (value: Value) : StorageResult<float> =
    match value with
    | Value.Int i -> Ok(float i)
    | Value.UInt u -> Ok(float u)
    | Value.Float f -> Ok f
    | Value.String s ->
        match Double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, f -> Ok f
        | _ -> Error(analyzeError $"invalid value for Float(): \"{s}\"")
    | other -> Error(analyzeError $"can't convert {other} into Float")

/// Ruby's `Integer(value)`.
let private rubyInteger (value: Value) : StorageResult<int64> =
    match value with
    | Value.Int i -> Ok i
    | Value.UInt u -> Ok(if u > uint64 Int64.MaxValue then Int64.MaxValue else int64 u)
    | Value.Float f when Double.IsFinite f ->
        // `f.trunc() as i64` saturates.
        let t = Math.Truncate f
        Ok(if t >= 9.2233720368547758e18 then Int64.MaxValue elif t <= -9.2233720368547758e18 then Int64.MinValue else int64 t)
    | Value.String s ->
        match Int64.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
        | true, i -> Ok i
        | _ -> Error(analyzeError $"invalid value for Integer(): \"{s}\"")
    | other -> Error(analyzeError $"can't convert {other} into Integer")

let private optionally (f: 'a -> StorageResult<'b>) (value: 'a option) : StorageResult<'b option> =
    match value with
    | None -> Ok None
    | Some v -> f v |> Result.map Some

/// `VideoAnalyzer#metadata`: `{ width:, height:, duration:, angle:, display_aspect_ratio:,
/// audio:, video: }.compact`.
let internal videoMetadata (probe: Value) : StorageResult<Value> =
    result {
        let video = stream probe "video"
        let audio = stream probe "audio"
        let field (name: string) : Value option =
            video |> Option.bind (fun s -> s.TryGet name) |> Option.filter (fun v -> v <> Value.Null)

        let! angle =
            match video |> Option.bind (fun s -> s.TryGet "tags") |> Option.bind (fun t -> t.TryGet "rotate") with
            | Some rotate -> rubyInteger rotate |> Result.map Some
            | None ->
                let displayMatrix =
                    match field "side_data_list" with
                    | Some(Value.Array list) ->
                        list
                        |> List.tryFind (fun d -> (d.TryGet "side_data_type" |> Option.bind (fun v -> v.AsString)) = Some "Display Matrix")
                    | _ -> None
                displayMatrix
                |> Option.bind (fun d -> d.TryGet "rotation")
                |> Option.filter (fun r -> r <> Value.Null)
                |> optionally rubyInteger

        let! displayAspectRatio =
            match field "display_aspect_ratio" with
            | Some descriptor ->
                result {
                    let! descriptor =
                        match descriptor.AsString with
                        | Some s -> Ok s
                        | None -> Error(analyzeError "display_aspect_ratio")
                    let terms = descriptor.Split(':', 2)
                    let! numerator = rubyInteger (Value.String terms[0])
                    let! denominator =
                        if terms.Length > 1 then rubyInteger (Value.String terms[1])
                        else Error(analyzeError "can't convert nil into Integer")
                    return (if numerator <> 0L then Some(numerator, denominator) else None)
                }
            | None -> Ok None

        let! encodedWidth = field "width" |> optionally rubyFloat
        let! encodedHeight = field "height" |> optionally rubyFloat
        let displayHeightScale = displayAspectRatio |> Option.map (fun (n, d) -> float d / float n)
        let computedHeight =
            match encodedWidth, displayHeightScale with
            | Some width, Some scale -> Some(width * scale)
            | _ -> None
        let rotated =
            match angle with
            | Some 90L | Some 270L | Some(-90L) | Some(-270L) -> true
            | _ -> false
        let width, height =
            if rotated then (Option.orElse encodedHeight computedHeight, encodedWidth)
            else (encodedWidth, Option.orElse encodedHeight computedHeight)

        let! duration =
            match field "duration" |> Option.orElse (probe.TryGet "format" |> Option.bind (fun f -> f.TryGet "duration")) with
            | Some d when d <> Value.Null -> rubyFloat d |> Result.map Some
            | _ -> Ok None

        return
            Value.Object(
                List.concat
                    [ width |> Option.map (fun w -> "width", Value.Float w) |> Option.toList
                      height |> Option.map (fun h -> "height", Value.Float h) |> Option.toList
                      duration |> Option.map (fun d -> "duration", Value.Float d) |> Option.toList
                      angle |> Option.map (fun a -> "angle", Value.Int a) |> Option.toList
                      displayAspectRatio
                      |> Option.map (fun (n, d) -> "display_aspect_ratio", Value.Array [ Value.Int n; Value.Int d ])
                      |> Option.toList
                      [ "audio", Value.Bool(present audio); "video", Value.Bool(present video) ] ]
            )
    }

/// `AudioAnalyzer#metadata`: `{ duration:, bit_rate:, sample_rate:, tags: }.compact`.
let internal audioMetadata (probe: Value) : StorageResult<Value> =
    result {
        let audio = stream probe "audio"
        let field (name: string) : Value option =
            audio |> Option.bind (fun s -> s.TryGet name) |> Option.filter (fun v -> v <> Value.Null)
        let! duration = field "duration" |> optionally rubyFloat
        let! bitRate = field "bit_rate" |> optionally rubyInteger
        let! sampleRate = field "sample_rate" |> optionally rubyInteger
        return
            Value.Object(
                List.concat
                    [ duration |> Option.map (fun d -> "duration", Value.Float d) |> Option.toList
                      bitRate |> Option.map (fun b -> "bit_rate", Value.Int b) |> Option.toList
                      sampleRate |> Option.map (fun s -> "sample_rate", Value.Int s) |> Option.toList
                      field "tags" |> Option.map (fun t -> "tags", t) |> Option.toList ]
            )
    }

module Analyzer =
    let forContentType (contentType: string) : Analyzer =
        if contentType.StartsWith("image", StringComparison.Ordinal) then Image
        elif contentType.StartsWith("video", StringComparison.Ordinal) then Video
        elif contentType.StartsWith("audio", StringComparison.Ordinal) then Audio
        else Null

    /// `analyze_later?`: only the null analyzer runs inline from `analyze_later`.
    let analyzeLater (analyzer: Analyzer) : bool = analyzer <> Null

    /// `analyzer.metadata` for a local copy of the blob.
    let metadata (analyzer: Analyzer) (path: string) : StorageResult<Value> =
        match analyzer with
        | Image -> Ok(imageMetadata path)
        | Video -> probe path |> Result.bind videoMetadata
        | Audio -> probe path |> Result.bind audioMetadata
        | Null -> Ok JsonValue.object
