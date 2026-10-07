// Port of the #[cfg(test)] modules of rust/crates/storage/src/process.rs and vips.rs.
module Campfire.Storage.Tests.ProcessTests

open System
open System.Diagnostics
open System.IO
open System.Text
open Xunit
open Campfire.Storage
open Campfire.Storage.Process
open Campfire.Tests

let private fixture (name: string) : string = Repo.path $"reference/test/fixtures/files/{name}"

let private thumbnail (input: string) (size: int64) (format: string) : StorageResult<TempFile> =
    transform input (Variation.resizeToLimit size size (Some format))

let private sh (script: string) : ProcessStartInfo =
    let info = ProcessStartInfo("sh")
    info.ArgumentList.Add "-c"
    info.ArgumentList.Add script
    info

[<Fact>]
let ``a failing variant reports its own error after many variants`` () =
    // libvips' error buffer is process-wide and holds 10 KB. Probing JPEG and PNG loaders for
    // `page` used to append "no property named `page'" to it on every variant, until it was
    // full and the next real error was cut off.
    use jpeg = (thumbnail (fixture "moon.jpg") 8L "jpg").Value
    use png = (thumbnail jpeg.Path 8L "png").Value
    for _ in 1..200 do
        use _ = (thumbnail jpeg.Path 4L "webp").Value
        use _ = (thumbnail png.Path 4L "webp").Value
        ()

    use corrupt = TempFile.Create(".tmp", ".jpg")
    File.WriteAllBytes(corrupt.Path, Array.append [| 0xFFuy; 0xD8uy; 0xFFuy; 0xE0uy |] "not really a JPEG"B)
    match thumbnail corrupt.Path 4L "webp" with
    | Error(StorageError.Vips message) ->
        Assert.True(message.Contains "JPEG datastream contains no image", message)
        Assert.False(message.Contains "no property named", message)
    | Ok _ -> failwith "a corrupt JPEG made a variant"
    | Error other -> failwith $"{other}"

[<Fact>]
let ``resize arguments must fit libvips`` () =
    match operations (Variation.resizeToLimit (int64 Int32.MaxValue + 1L) 100L None) with
    | Error(StorageError.InvalidVariation _) -> ()
    | other -> failwith $"{other}"
    Assert.Equal<(int option * int option) list>([ (Some Int32.MaxValue, Some 100) ], (operations (Variation.resizeToLimit (int64 Int32.MaxValue) 100L None)).Value)

[<Fact>]
let ``output_within captures a quick child`` () =
    let info = sh "echo out; echo err >&2"
    info.RedirectStandardError <- true
    let output = (outputWithin info (TimeSpan.FromSeconds 10.0)).Value
    Assert.True output.Success
    Assert.Equal<byte[]>("out\n"B, output.Stdout)
    Assert.Equal<byte[]>("err\n"B, output.Stderr)

[<Fact>]
let ``output_within reports a program that is not there`` () =
    match outputWithin (ProcessStartInfo "no-such-program-for-campfire") (TimeSpan.FromSeconds 1.0) with
    | Error(ProcessError.NotFound _) -> ()
    | other -> failwith $"{other}"

/// `command.output()`: no timeout, so no polling either.
let private plainOutput (info: ProcessStartInfo) : int =
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    use child = nonNull (Process.Start info)
    child.StandardOutput.ReadToEnd() |> ignore
    child.WaitForExit()
    child.ExitCode

[<Fact>]
let ``output_within returns soon after the child exits`` () =
    // The wait is the process's own exit notification, not a poll, so a child that exits is
    // noticed at once (Rust polls, with pauses that once grew to 50 ms: a child that exited at
    // 35 ms was noticed at 63 ms).
    let run (within: bool) : TimeSpan =
        let info = ProcessStartInfo("sleep")
        info.ArgumentList.Add "0.035"
        let started = Stopwatch.GetTimestamp()
        if within then
            Assert.True((outputWithin info (TimeSpan.FromSeconds 10.0)).Value.Success)
        else
            Assert.Equal(0, plainOutput info)
        Stopwatch.GetElapsedTime started
    // The fastest of a few runs each, so a busy machine doesn't fail it.
    let mutable plain = TimeSpan.MaxValue
    let mutable within = TimeSpan.MaxValue
    for _ in 1..5 do
        plain <- min plain (run false)
        within <- min within (run true)
    Assert.True(within < plain + TimeSpan.FromMilliseconds 15.0, $"output() {plain}, output_within {within}")

[<Fact>]
let ``output_within kills a child that overruns`` () =
    let started = Stopwatch.GetTimestamp()
    use pidFile = TempFile.Create()
    let info = sh $"echo $$ > {pidFile.Path}; exec sleep 30"
    match outputWithin info (TimeSpan.FromMilliseconds 300.0) with
    | Error(ProcessError.TimedOut _) -> ()
    | other -> failwith $"{other}"
    Assert.True((Stopwatch.GetElapsedTime started < TimeSpan.FromSeconds 5.0), $"took {Stopwatch.GetElapsedTime started}")
    let pid = int ((File.ReadAllText pidFile.Path).Trim())
    let running =
        try
            Process.GetProcessById pid |> ignore
            true
        with :? ArgumentException ->
            false
    Assert.False(running, "the child is still running")

// --- vips.rs -------------------------------------------------------------------------------------

[<Fact>]
let ``page goes only to loaders that take it`` () =
    use dir = TempDir.Create()
    use moon = (Vips.Image.LoadForProcessing(fixture "moon.jpg")).Value
    use image = (moon.ResizeToLimit(Some 8, Some 8)).Value
    for format, accepts in [ ("jpg", false); ("png", false); ("gif", true); ("webp", true) ] do
        let path = Path.Combine(dir.Path, $"moon.{format}")
        Assert.Equal(Ok(), image.WriteToFile path)
        Assert.True((Vips.loaderAcceptsPage path = accepts), format)
