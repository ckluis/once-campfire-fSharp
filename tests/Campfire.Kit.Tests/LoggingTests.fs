// Port of rust/crates/kit/tests/logging.rs
//
// What the HTTP layer logs. (Rust keeps this in a test binary of its own because tracing caches whether
// anything listens at each log statement; the logger here is the kit's own, so a test class will do.)
module Campfire.Kit.Tests.LoggingTests

open System
open System.Text
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Xunit
open Campfire.RailsCompat
open Campfire.Kit
open Campfire.Kit.Tests.Helpers
open Campfire.Kit.Tests.Harness

/// Collects the lines logged to it.
type CapturingLogger() =
    let lines = ResizeArray<string>()

    member _.Text: string = lock lines (fun () -> String.Join("\n", lines))

    interface ILogger with
        member _.BeginScope(_: 'state) : IDisposable | null = null
        member _.IsEnabled(_: LogLevel) = true

        member _.Log(level: LogLevel, _: EventId, state: 'state, error: exn | null, formatter: Func<'state, exn | null, string>) =
            lock lines (fun () -> lines.Add $"{level}: {formatter.Invoke(state, error)}")

let private echo (c: Ctx) = act { return c.Head Status.Ok }

let private fail (_: Ctx) : Task<Result<Response, Error>> =
    Task.FromResult(Error(Internal(Exception("saving the upload", Exception("disk full")))))

[<Fact>]
let ``errors are logged as they were raised`` () =
    task {
        let logs = CapturingLogger()
        let kit =
            Kit(KitConfig.Default, secrets.Value, Campfire.RailsCompat.Clock.TestClock.FrozenAt(ts "2024-06-01T12:00:00Z"), null, logs)
        let route = Adapter.route kit
        use! app = start kit [ route "/echo/{id}" [ "GET", echo; "POST", echo ]; route "/fail" [ "GET", fail ] ]
        let! query = app.Send(get "/echo/1?a=%")
        let! form = app.Send(formPost "/echo/1" "b=%")
        Assert.Equal(400, query.Status)
        Assert.Equal(400, form.Status)
        let! failing = app.Send(get "/fail")
        Assert.Equal(500, failing.Status)

        let text = logs.Text
        // Malformed params, as they were rejected.
        let rejected = "request rejected error=bad request: invalid %-encoding (%)"
        let count = (text.Split(rejected).Length) - 1
        Assert.True((count = 2), text)
        Assert.DoesNotContain("bad request: bad request", text)
        // A failure, with what caused it.
        Assert.Contains("request failed error=saving the upload: disk full path=\"/fail\"", text)
    }
