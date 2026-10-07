// Port of `Error` in rust/crates/richtext/src/lib.rs
namespace Campfire.RichText

/// Something Ruby would have raised while rendering. Callers mirror what the Rails code does with
/// the exception (`message_presentation` rescues everything and renders "").
type RenderError =
    | Parse of ParseError
    | Raised of string
    /// Raised with a message that isn't valid UTF-8, so the rescue's own logging raises too.
    | Unrenderable of string

    member this.Message =
        match this with
        | Parse e -> e.Message
        | Raised what -> "raised " + what
        | Unrenderable what -> "raised " + what + " with an unloggable message"

/// `?` for `Result`: a computation that stops at the first `Error`.
[<AutoOpen>]
module ResultCe =
    type ResultBuilder() =
        member _.Bind(m: Result<'a, 'e>, f: 'a -> Result<'b, 'e>) : Result<'b, 'e> = Result.bind f m
        member _.Return(x: 'a) : Result<'a, 'e> = Ok x
        member _.ReturnFrom(m: Result<'a, 'e>) : Result<'a, 'e> = m
        member _.Zero() : Result<unit, 'e> = Ok()
        member _.Delay(f: unit -> Result<'a, 'e>) : unit -> Result<'a, 'e> = f
        member _.Run(f: unit -> Result<'a, 'e>) : Result<'a, 'e> = f ()

        member _.Combine(a: Result<unit, 'e>, b: unit -> Result<'a, 'e>) : Result<'a, 'e> =
            match a with
            | Ok() -> b ()
            | Error e -> Error e

        member this.For(items: seq<'t>, body: 't -> Result<unit, 'e>) : Result<unit, 'e> =
            use e = items.GetEnumerator()
            let mutable result = Ok()
            while (match result with Ok() -> true | Error _ -> false) && e.MoveNext() do
                result <- body e.Current
            result

        member this.While(guard: unit -> bool, body: unit -> Result<unit, 'e>) : Result<unit, 'e> =
            let mutable result = Ok()
            while (match result with Ok() -> true | Error _ -> false) && guard () do
                result <- body ()
            result

    let result = ResultBuilder()

    /// Lifts a parse failure into a render failure, as `.map_err(Error::Parse)`.
    let parseErr (r: Result<'a, ParseError>) : Result<'a, RenderError> = r |> Result.mapError Parse
