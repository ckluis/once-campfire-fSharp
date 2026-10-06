/// Reads the cases of `bin/views-differential` (one JSON object per line on stdin) and writes what
/// `Campfire.Views` renders for each, one JSON line per case, in the format of the Rust tool
/// (`src/main.rs`). `bench N` renders every case N times instead and prints the time per render.
module Campfire.Views.Differential.Program

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open Campfire.Views
open Campfire.Views.Differential

let private write (output: Stream) (id: JsonElement) (answer: Result<Answer.Answer, string>) =
    use writer = new Utf8JsonWriter(output)
    writer.WriteStartObject()
    writer.WritePropertyName "id"
    id.WriteTo writer
    match answer with
    | Ok answer ->
        writer.WriteString("out", answer.Out)
        answer.Text |> Option.iter (fun text -> writer.WriteString("text", text))
        answer.Fragments
        |> Option.iter (fun fragments ->
            writer.WriteStartArray "fragments"
            for (offset, length) in fragments do
                writer.WriteStartArray()
                writer.WriteNumberValue offset
                writer.WriteNumberValue length
                writer.WriteEndArray()
            writer.WriteEndArray())
    | Error message -> writer.WriteString("error", message)
    writer.WriteEndObject()
    writer.Flush()
    output.WriteByte(byte '\n')

[<EntryPoint>]
let main argv =
    let mutable shared = JsonElement()
    let cases = List<JsonElement>()
    let mutable line = Console.In.ReadLine()
    while not (isNull line) do
        let text = nonNull line
        if text.Trim() <> "" then
            let element = JsonDocument.Parse(text).RootElement
            match element.TryGetProperty "shared" with
            | true, value -> shared <- value
            | _ -> cases.Add element
        line <- Console.In.ReadLine()
    if argv.Length >= 2 && argv[0] = "bench" then
        let rounds = int argv[1]
        let warmup = rounds / 4
        let totals = SortedDictionary<string, struct (int64 * int * int64)>()
        for round in 1 .. rounds + warmup do
            for case in cases do
                let op = Inputs.str (Inputs.get case "op")
                let allocated = GC.GetAllocatedBytesForCurrentThread()
                let started = Stopwatch.GetTimestamp()
                Operations.run case shared |> ignore
                let elapsed = Stopwatch.GetElapsedTime(started).Ticks * 100L
                let bytes = GC.GetAllocatedBytesForCurrentThread() - allocated
                if round > warmup then
                    let struct (nanos, count, total) =
                        match totals.TryGetValue op with
                        | true, total -> total
                        | _ -> struct (0L, 0, 0L)
                    totals[op] <- struct (nanos + elapsed, count + 1, total + bytes)
        // The page alone: the context built once, the page rendered into the pooled buffer.
        let page = cases |> Seq.find (fun case -> Inputs.str (Inputs.get case "op") = "welcome/show")
        let ctx = Inputs.viewContext (Inputs.get page "ctx") shared
        let name = Inputs.str (Inputs.get (Inputs.get page "args") "current_user_name")
        let render () = Render.page 0 (fun w -> Templates.Welcome.Show.render w ctx name)
        let length = (render ()).Length
        let pageRounds = rounds * 50
        for _ in 1 .. pageRounds / 4 do
            render () |> ignore
        let allocated = GC.GetAllocatedBytesForCurrentThread()
        let started = Stopwatch.GetTimestamp()
        for _ in 1..pageRounds do
            render () |> ignore
        let nanos = Stopwatch.GetElapsedTime(started).Ticks * 100L / int64 pageRounds
        let bytes = (GC.GetAllocatedBytesForCurrentThread() - allocated) / int64 pageRounds
        // The two pages the benchmarks fetch most (see `bench_hot_pages` of the Rust tool): recorded, into a buffer sized from
        // the last render, with the fragment cache warm and cold.
        let busiest (op: string) (count: JsonElement -> int) =
            cases
            |> Seq.filter (fun case ->
                Inputs.str (Inputs.get case "op") = op
                && Inputs.str (Inputs.get (Inputs.get case "args") "mode") = "view"
                && not (Inputs.bool (Inputs.get (Inputs.get case "args") "frame")))
            |> Seq.sortByDescending (fun case -> count (Inputs.get case "args"))
            |> Seq.tryHead
        let measure (name: string) (render: unit -> struct (int * int)) (clear: unit -> unit) (rounds: int) =
            let struct (bytes, fragments) = render ()
            for _ in 1 .. rounds / 4 do
                render () |> ignore
            let mutable total = 0L
            let allocated = GC.GetAllocatedBytesForCurrentThread()
            for _ in 1..rounds do
                clear ()
                let started = Stopwatch.GetTimestamp()
                render () |> ignore
                total <- total + Stopwatch.GetElapsedTime(started).Ticks * 100L
            let allocatedPer = (GC.GetAllocatedBytesForCurrentThread() - allocated) / int64 rounds
            printfn "{\"op\":\"%s\",\"bytes\":%d,\"fragments\":%d,\"ns_per_render\":%d,\"bytes_allocated_per_render\":%d}" name bytes fragments (total / int64 rounds) allocatedPer
        let hotRounds = rounds * 50
        let sized (page: RecordedPage) =
            let mutable bytes = page.Text.Length
            for struct (_, fragment) in page.Fragments do
                bytes <- bytes + fragment.Length
            struct (bytes, page.Fragments.Length)
        match busiest "rooms/show" (fun args -> Inputs.arr (Inputs.get (Inputs.get args "show") "messages") |> List.length) with
        | Some case ->
            let ctx = Inputs.viewContext (Inputs.get case "ctx") shared
            let show = Inputs.showView (Inputs.get (Inputs.get case "args") "show")
            let cache = FragmentCache(FragmentCacheLimits.DefaultMaxBytes)
            let size = RenderSize()
            FragmentCache.withCache cache (fun () ->
                let render () = sized (size.Render(fun w -> Templates.Rooms.ShowPage.render w ctx show))
                measure "rooms/show (busy room page, warm cache)" render ignore hotRounds
                measure "rooms/show (busy room page, cold cache)" render (fun () -> cache.Clear()) (hotRounds / 10))
        | None -> ()
        match busiest "messages/index" (fun args -> Inputs.arr (Inputs.get args "messages") |> List.length) with
        | Some case ->
            let ctx = Inputs.viewContext (Inputs.get case "ctx") shared
            let items = Inputs.arr (Inputs.get (Inputs.get case "args") "messages") |> List.map Inputs.messageItem
            let cache = FragmentCache(FragmentCacheLimits.DefaultMaxBytes)
            let size = RenderSize()
            FragmentCache.withCache cache (fun () ->
                let render () = sized (size.Render(fun w -> Templates.Messages.Index.render w ctx items))
                measure "messages/index (messages page, warm cache)" render ignore hotRounds
                measure "messages/index (messages page, cold cache)" render (fun () -> cache.Clear()) (hotRounds / 10))
        | None -> ()
        // The fragment cache's hit, as a room page makes it for each of its messages.
        let cache = FragmentCache(FragmentCacheLimits.DefaultMaxBytes)
        let at = (Campfire.RailsCompat.Timestamps.tryParse "2026-09-26T12:23:46.483521Z").Value
        let digest = FragmentCache.digest [| "messages/_message"; "messages/_actions" |]
        let key (id: int64) (buffer: KeyBuf) =
            FragmentCache.pushRecordFragmentKey buffer "messages/_message" digest "messages" id at
        FragmentCache.withCache cache (fun () ->
            for id in 0L .. 39L do
                FragmentCache.fetch (key (1000L + id)) (fun w -> w.Raw(String('x', 4096))) |> ignore
            let hit () =
                for id in 0L .. 39L do
                    FragmentCache.fetch (key (1000L + id)) (fun _ -> failwith "cached") |> ignore
            let cacheRounds = rounds * 500
            for _ in 1 .. cacheRounds / 4 do
                hit ()
            let allocated = GC.GetAllocatedBytesForCurrentThread()
            let started = Stopwatch.GetTimestamp()
            for _ in 1..cacheRounds do
                hit ()
            let perHit = Stopwatch.GetElapsedTime(started).Ticks * 100L / int64 (cacheRounds * 40)
            let bytes = (GC.GetAllocatedBytesForCurrentThread() - allocated) / int64 (cacheRounds * 40)
            printfn "{\"op\":\"fragment_cache hit\",\"ns_per_hit\":%d,\"bytes_allocated_per_hit\":%d}" perHit bytes)
        for KeyValue(op, struct (nanos, count, bytes)) in totals do
            printfn "{\"op\":\"%s\",\"renders\":%d,\"ns_per_render\":%d,\"bytes_allocated_per_render\":%d}" op count (nanos / int64 count) (bytes / int64 count)
        printfn "{\"op\":\"welcome/show (page only)\",\"bytes\":%d,\"ns_per_render\":%d,\"bytes_allocated_per_render\":%d}" length nanos bytes
        0
    else
        use output = Console.OpenStandardOutput()
        use buffered = new BufferedStream(output, 1 <<< 16)
        for case in cases do
            let answer =
                try
                    Ok(Operations.run case shared)
                with e ->
                    Error(e.Message)
            write buffered (Inputs.get case "id") answer
        0
