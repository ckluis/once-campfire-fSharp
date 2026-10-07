// Unit 7.3: the request log line written as bytes (`RequestLog.writeLine`) is the line the console logger wrote for the same entry
// (`RequestLogEntry` through `AddSimpleConsole` with the options `Boot.createLoggerFactory` sets), compared on the real logger.
module Campfire.Kit.Tests.RequestLogTests

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Xunit
open Campfire.Kit
open Campfire.Kit.Tests.FrontHarness

let private capture (path: string) (query: string | null) (meth: string) (length: int64) (contentType: string) (forwardedFor: string) (ip: IPAddress | null) (agent: string) =
    RequestLogCapture(Stopwatch.GetTimestamp(), path, query, meth, "HTTP/1.1", length, contentType, forwardedFor, ip, 51234, agent)

/// What `FrontHandler` hands the `ILogger` for a capture, in its field order.
let private entry (c: RequestLogCapture) (status: int) (dur: int64) (sent: int64) (respType: string) (xCache: string) : RequestLogEntry =
    let remote =
        if c.ForwardedFor <> "" then c.ForwardedFor
        else
            match c.RemoteIp with
            | null -> ""
            | ip -> $"{(if ip.IsIPv4MappedToIPv6 then ip.MapToIPv4() else ip)}:{c.RemotePort}"
    let field (name: string) (value: obj | null) = KeyValuePair<string, obj | null>(name, value)
    RequestLogEntry(
        [| field "path" c.Path
           field "status" (box status)
           field "dur" (box dur)
           field "method" c.Method
           field "req_content_length" (box c.ContentLength)
           field "req_content_type" c.ContentType
           field "resp_content_length" (box sent)
           field "resp_content_type" respType
           field "remote_addr" remote
           field "user_agent" c.UserAgent
           field "cache" xCache
           field "query" c.Query
           field "proto" c.Proto |]
    )

let private samples =
    [ capture "/rooms/1/@42" "page=2&x=%C3%A9" "GET" 0L "" "" (IPAddress.Parse "::ffff:172.17.0.1") "Mozilla/5.0 (X11; Linux x86_64) probe/1"
      capture "/" "" "GET" 0L "" "" (IPAddress.Parse "127.0.0.1") ""
      capture "/messages" null "POST" 512L "multipart/form-data; boundary=x" "" (IPAddress.Parse "2001:db8::1") "agent"
      capture "/caf\u00e9/\u65e5\u672c" "q=\u00e9" "GET" -1L "text/plain" "203.0.113.9, 10.0.0.1" null "ua \u00fc\u00f1"
      capture "/a\nb" "c\nd" "GET" 5L "t\ne" "f\ng" (IPAddress.Parse "fe80::1%3") "u\na"
      capture "/emoji/\U0001F600" "x" "PATCH" 9_999_999_999L "application/json" "" (IPAddress.Parse "10.1.2.3") "bot/\U0001F600"
      capture ("/" + String('p', 5000)) (String('q', 3000)) "GET" 0L "" "" (IPAddress.Parse "192.0.2.255") (String('u', 2000)) ]

[<Fact>]
let ``the line written as bytes is the line the console logger writes`` () =
    let real = Console.Out
    let sw = new StringWriter()
    Console.SetOut sw
    let expected =
        try
            let factory =
                LoggerFactory.Create(fun builder ->
                    builder.AddSimpleConsole(fun options ->
                        options.SingleLine <- true
                        options.UseUtcTimestamp <- true
                        options.TimestampFormat <- "yyyy-MM-ddTHH:mm:ss.ffffffZ "
                        // as `Boot.configureConsole` sets them (`AppTests` checks that it does): colour is the one option that
                        // changes the line's bytes, and only when stdout is a terminal
                        options.ColorBehavior <- Microsoft.Extensions.Logging.Console.LoggerColorBehavior.Disabled)
                    |> ignore)
            let logger = factory.CreateLogger "thruster"
            for c in samples do
                logger.Log(LogLevel.Information, EventId 0, entry c 200 7L 1234L "text/html; charset=utf-8" "miss", null, (fun (state: RequestLogEntry) _ -> state.ToString()))
            // the console logger writes from a thread of its own; disposing the factory drains it
            factory.Dispose()
            sw.ToString()
        finally
            Console.SetOut real
    let consoleLines = expected.Split('\n') |> Array.filter (fun l -> l <> "")
    Assert.Equal(samples.Length, consoleLines.Length)
    let writer = LineWriter.Current
    let shape = RegularExpressions.Regex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{6}Z ")
    for (c, consoleLine) in List.zip samples (List.ofArray consoleLines) do
        RequestLog.writeLine writer c 200 7L 1234L "text/html; charset=utf-8" "miss"
        let mine = Encoding.UTF8.GetString(writer.Span.ToArray())
        Assert.EndsWith("\n", mine)
        Assert.Matches(shape, mine)
        // everything after the timestamp is the same, byte for byte
        Assert.Equal(consoleLine.Substring 27, mine.TrimEnd('\n').Substring 27)
        Assert.Equal(consoleLine.Substring(0, 27).Length, mine.Substring(0, 27).Length)

[<Fact>]
let ``the timestamp is the console logger's format and the current time`` () =
    let writer = LineWriter.Current
    let c = samples.Head
    for _ in 1..3 do
        let before = DateTime.UtcNow
        RequestLog.writeLine writer c 200 1L 1L "" ""
        let after = DateTime.UtcNow
        let line = Encoding.UTF8.GetString(writer.Span.ToArray())
        let stamp = DateTime.ParseExact(line.Substring(0, 26), "yyyy-MM-ddTHH:mm:ss.ffffff", Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AssumeUniversal ||| Globalization.DateTimeStyles.AdjustToUniversal)
        Assert.InRange(stamp, before.AddMilliseconds -1.0, after.AddMilliseconds 1.0)
        Assert.Equal('Z', line[26])
        Assert.Equal(' ', line[27])
        Thread.Sleep 1100

[<Fact>]
let ``batched lines arrive whole, in chunks of whole lines, and every one of them`` () =
    let output = new CollectingStream()
    let lines = RequestLines(output, TimeSpan.FromMilliseconds 3.0)
    let threads = 8
    let each = 4000
    let tasks =
        [ for t in 0 .. threads - 1 ->
              Task.Run(fun () ->
                  for i in 0 .. each - 1 do
                      // some lines longer than a chunk, some short
                      let body = if i % 97 = 0 then String('x', 5000) else "payload"
                      lines.Append(ReadOnlySpan(Encoding.UTF8.GetBytes $"line t={t} i={i} {body}\n"))
                      if i % 500 = 0 then Thread.Sleep 5) ]
    Task.WaitAll(tasks |> Array.ofList)
    lines.Stop()
    let all = output.Text.Split('\n') |> Array.filter (fun l -> l <> "")
    Assert.Equal(threads * each, all.Length)
    // each thread's lines are in the order it wrote them, and none is cut or mixed with another's
    for t in 0 .. threads - 1 do
        let index (l: string) =
            let parts = l.Split ' '
            int (parts[2].Substring 2)
        let mine = all |> Array.filter (fun l -> l.StartsWith $"line t={t} ") |> Array.map index
        Assert.Equal<int[]>([| 0 .. each - 1 |], mine)
    for line in all do
        Assert.StartsWith("line t=", line)
    // a write is at most a chunk unless it is one line longer than a chunk
    for size in output.Sizes do
        Assert.True(size <= 4096 || size > 5000, $"a write of {size} bytes")

[<Fact>]
let ``a full batch is written by the request that fills it`` () =
    let output = new CollectingStream()
    // a writer thread that never wakes in the test's time: only the backlog rule can write
    let lines = RequestLines(output, TimeSpan.FromHours 1.0)
    let line = Encoding.UTF8.GetBytes(String('y', 1000) + "\n")
    for _ in 1..5000 do
        lines.Append(ReadOnlySpan line)
    Assert.True(output.Sizes.Length > 0, "nothing was written before the stop")
    lines.Stop()
    let written = output.Text.Split('\n') |> Array.filter (fun l -> l <> "")
    Assert.Equal(5000, written.Length)
