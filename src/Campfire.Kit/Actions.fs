// The F# counterpart of Rust's `?` inside `async fn action(c: &mut Ctx) -> Result<Response>`.
namespace Campfire.Kit

open System
open System.Threading.Tasks

/// An action: any `Ctx -> Task<Result<Response, Error>>`. Before-actions are ordered steps at the
/// top of it that return early with `halt`.
type ActionFn = Ctx -> Task<Result<Response, Error>>

/// `act { ... }` builds a `Task<Result<_, Error>>`: `let!` and `do!` take a `Result`, a `Task` or a
/// `Task<Result>` and stop at the first `Error`, as `?` does.
[<Sealed>]
type ActionBuilder() =
    member _.Return(value: 'a) : Task<Result<'a, Error>> = Task.FromResult(Ok value)

    member _.ReturnFrom(value: Task<Result<'a, Error>>) : Task<Result<'a, Error>> = value

    member _.ReturnFrom(value: Result<'a, Error>) : Task<Result<'a, Error>> = Task.FromResult value

    member _.Zero() : Task<Result<unit, Error>> = Task.FromResult(Ok())

    member _.Bind(value: Result<'a, Error>, f: 'a -> Task<Result<'b, Error>>) : Task<Result<'b, Error>> =
        match value with
        | Ok a -> f a
        | Error e -> Task.FromResult(Error e)

    member _.Bind(value: Task<Result<'a, Error>>, f: 'a -> Task<Result<'b, Error>>) : Task<Result<'b, Error>> =
        task {
            match! value with
            | Ok a -> return! f a
            | Error e -> return Error e
        }

    member _.Bind(value: Task<'a>, f: 'a -> Task<Result<'b, Error>>) : Task<Result<'b, Error>> =
        task {
            let! a = value
            return! f a
        }

    member _.Bind(value: Task, f: unit -> Task<Result<'b, Error>>) : Task<Result<'b, Error>> =
        task {
            do! value
            return! f ()
        }

    member _.Delay(f: unit -> Task<Result<'a, Error>>) : unit -> Task<Result<'a, Error>> = f

    member _.Run(f: unit -> Task<Result<'a, Error>>) : Task<Result<'a, Error>> = f ()

    member this.Combine(first: Task<Result<unit, Error>>, second: unit -> Task<Result<'b, Error>>) : Task<Result<'b, Error>> =
        this.Bind(first, second)

    member this.While(guard: unit -> bool, body: unit -> Task<Result<unit, Error>>) : Task<Result<unit, Error>> =
        if guard () then
            this.Bind(body (), (fun () -> this.While(guard, body)))
        else
            this.Zero()

    member _.TryWith(body: unit -> Task<Result<'a, Error>>, handler: exn -> Task<Result<'a, Error>>) : Task<Result<'a, Error>> =
        task {
            try
                return! body ()
            with e ->
                return! handler e
        }

    member _.TryFinally(body: unit -> Task<Result<'a, Error>>, compensation: unit -> unit) : Task<Result<'a, Error>> =
        task {
            try
                return! body ()
            finally
                compensation ()
        }

    member this.Using(resource: 'r :> IDisposable, body: 'r -> Task<Result<'a, Error>>) : Task<Result<'a, Error>> =
        this.TryFinally((fun () -> body resource), (fun () -> if not (isNull (box resource)) then resource.Dispose()))

[<AutoOpen>]
module ActionBuilders =
    /// `act { ... }`: an action's body, with `?` as `let!`.
    let act = ActionBuilder()
