// Port of rust/crates/storage/src/process.rs
/// Producing variant and preview bytes: `ActiveStorage::Transformers::Vips` (image_processing
/// 1.14) and `ActiveStorage::Previewer::VideoPreviewer`.
module Campfire.Storage.Process

open System
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open Campfire.Storage.ContentTypes
open Campfire.Storage.Marshal

/// How long ffmpeg may take to draw a preview frame before it's killed. Rails sets no limit, but
/// a crafted or hour-long video shouldn't hold a processing thread indefinitely.
let ffmpegTimeout : TimeSpan = TimeSpan.FromSeconds 60.0

/// `std::process::Output`.
type Output =
    { ExitCode: int
      Stdout: byte[]
      Stderr: byte[] }

    member this.Success : bool = this.ExitCode = 0

/// The `io::ErrorKind`s `output_within` tells apart.
[<RequireQualifiedAccess>]
type ProcessError =
    /// The program doesn't exist.
    | NotFound of exn
    /// "timed out after {timeout}"
    | TimedOut of string
    | Failed of exn

module ProcessError =
    let message (error: ProcessError) : string =
        match error with
        | ProcessError.NotFound e
        | ProcessError.Failed e -> e.Message
        | ProcessError.TimedOut message -> message

    let toStorageError (error: ProcessError) : StorageError =
        match error with
        | ProcessError.NotFound e
        | ProcessError.Failed e -> StorageError.Io e
        | ProcessError.TimedOut message -> StorageError.Io(TimeoutException message)

/// Rust's `{:?}` of a `Duration`: "60s", "300ms".
let private describe (timeout: TimeSpan) : string =
    if timeout.Ticks % TimeSpan.TicksPerSecond = 0L then $"{int64 timeout.TotalSeconds}s"
    elif timeout.Ticks % TimeSpan.TicksPerMillisecond = 0L then $"{int64 timeout.TotalMilliseconds}ms"
    else $"{timeout.TotalMilliseconds}ms"

/// Drains a pipe on a thread of its own while the child runs, so a chatty child can't stall on a
/// full pipe.
let private drain (pipe: Stream) : Thread * MemoryStream =
    let buffer = new MemoryStream()
    let thread =
        Thread(
            (fun () ->
                try
                    pipe.CopyTo buffer
                with :? IOException ->
                    ()),
            IsBackground = true
        )
    thread.Start()
    thread, buffer

let private finish (thread: Thread, buffer: MemoryStream) : byte[] =
    thread.Join()
    buffer.ToArray()

/// `command.output()`, except that the child is killed (and reaped) once `timeout` passes, which
/// is a `TimedOut` error. Stdin is closed and stdout captured; stderr is captured only when the
/// caller redirects it (`RedirectStandardError`), and otherwise goes where ours does.
let outputWithin (info: ProcessStartInfo) (timeout: TimeSpan) : Result<Output, ProcessError> =
    info.UseShellExecute <- false
    info.RedirectStandardInput <- true
    info.RedirectStandardOutput <- true
    try
        use child = nonNull (Process.Start info)
        child.StandardInput.Close()
        let stdout = drain child.StandardOutput.BaseStream
        let stderr = if info.RedirectStandardError then Some(drain child.StandardError.BaseStream) else None
        let milliseconds = if timeout = Timeout.InfiniteTimeSpan then -1 else int (min timeout.TotalMilliseconds (float Int32.MaxValue))
        if child.WaitForExit milliseconds then
            Ok
                { ExitCode = child.ExitCode
                  Stdout = finish stdout
                  Stderr = stderr |> Option.map finish |> Option.defaultValue [||] }
        else
            (try
                child.Kill()
             with :? InvalidOperationException ->
                 ())
            child.WaitForExit()
            Error(ProcessError.TimedOut $"timed out after {describe timeout}")
    with
    | :? Win32Exception as e when e.NativeErrorCode = 2 -> Error(ProcessError.NotFound e)
    | :? Win32Exception as e -> Error(ProcessError.Failed e)
    | :? IOException as e -> Error(ProcessError.Failed e)

let private startInfo (program: string) (arguments: string seq) : ProcessStartInfo =
    let info = ProcessStartInfo(program)
    for argument in arguments do
        info.ArgumentList.Add argument
    info

/// `Object#present?` negated, for the values a transformation can hold.
let private blank (value: Value) : bool =
    match value with
    | Value.Nil
    | Value.Bool false -> true
    | Value.Str s -> s.Trim().Length = 0
    | Value.Array items -> List.isEmpty items
    | Value.Hash entries -> List.isEmpty entries
    | _ -> false

/// `ImageProcessingTransformer#operations`: every transformation except `format`, skipping blank
/// arguments. Only `resize_to_limit` is implemented: it's the only one Campfire defines.
let internal operations (variation: Variation) : StorageResult<(int option * int option) list> =
    let dimension (v: Value option) : StorageResult<int option> =
        match v with
        | None
        | Some Value.Nil -> Ok None
        | Some(Value.Int n) ->
            if n >= int64 Int32.MinValue && n <= int64 Int32.MaxValue then
                Ok(Some(int n))
            else
                Error(StorageError.InvalidVariation $"resize_to_limit argument {n}")
        | Some other -> Error(StorageError.InvalidVariation $"resize_to_limit argument {other}")
    let rec go (remaining: (string * Value) list) (acc: (int option * int option) list) =
        match remaining with
        | [] -> Ok(List.rev acc)
        | (name, argument) :: rest ->
            match name, argument with
            | "format", _ -> go rest acc
            | "combine_options", _ -> Error(StorageError.InvalidVariation "combine_options is not supported")
            | _, argument when blank argument -> go rest acc
            | "resize_to_limit", Value.Array [ w; h ] ->
                match dimension (Some w), dimension (Some h) with
                | Error e, _
                | _, Error e -> Error e
                | Ok None, Ok None -> Error(StorageError.InvalidVariation "either width or height must be specified")
                | Ok width, Ok height -> go rest ((width, height) :: acc)
            | name, argument -> Error(StorageError.InvalidVariation $"unsupported transformation {name}: {argument}")
    go (Variation.transformations variation) []

/// `variation.transform(file)`: `ImageProcessing::Vips.source(file).loader(page: 0)
/// .convert(format).apply(operations).call`, saved to a tempfile named for the format.
let transform (input: string) (variation: Variation) : StorageResult<TempFile> =
    result {
        let! format = Variation.format variation
        let! operations = operations variation
        let! loaded = Vips.Image.LoadForProcessing input
        let rec resize (image: Vips.Image) (remaining: (int option * int option) list) : StorageResult<Vips.Image> =
            match remaining with
            | [] -> Ok image
            | (width, height) :: rest ->
                let next = image.ResizeToLimit(width, height)
                (image :> IDisposable).Dispose()
                match next with
                | Ok next -> resize next rest
                | Error e -> Error e
        let! image = resize loaded operations
        use image = image
        let! output = Io.attempt (fun () -> TempFile.Create("image_processing", $".{format}"))
        match image.WriteToFile output.Path with
        | Ok() -> return output
        | Error e ->
            (output :> IDisposable).Dispose()
            return! Error e
    }

let private ffmpegExistsCheck =
    lazy
        (let info = startInfo (ffmpegPath ()) [ "-version" ]
         info.RedirectStandardError <- true
         match outputWithin info Timeout.InfiniteTimeSpan with
         | Ok output -> output.Success
         | Error _ -> false)

/// `VideoPreviewer.accept?`: `system(ffmpeg, "-version")`, memoized.
let ffmpegExists () : bool = ffmpegExistsCheck.Value

/// `draw_relevant_frame_from`: `ffmpeg -i <input> <video_preview_arguments> -`, capturing stdout.
let videoPreview (input: string) : StorageResult<byte[]> =
    let info = startInfo (ffmpegPath ()) (Seq.concat [ Seq.ofList [ "-i"; input ]; Seq.ofArray videoPreviewArguments; Seq.singleton "-" ])
    info.RedirectStandardError <- true
    match outputWithin info ffmpegTimeout with
    | Error(ProcessError.TimedOut message) -> Error(StorageError.Preview $"{ffmpegPath ()} {message}")
    | Error error -> Error(ProcessError.toStorageError error)
    | Ok output when not output.Success ->
        Error(
            StorageError.Preview
                $"{ffmpegPath ()} failed (status {output.ExitCode}): {(Encoding.UTF8.GetString output.Stderr).TrimEnd()}"
        )
    | Ok output -> Ok output.Stdout
