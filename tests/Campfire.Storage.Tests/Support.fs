namespace Campfire.Storage.Tests

open Xunit

// The timing tests measure the process they run in, so the assembly's tests run one at a time.
[<assembly: CollectionBehavior(DisableTestParallelization = true)>]
do ()

[<AutoOpen>]
module Support =
    type Result<'a, 'e> with
        /// The `Ok` value (Rust's `unwrap`); anything else fails the test with the error.
        member this.Value : 'a =
            match this with
            | Ok value -> value
            | Error error -> failwith $"{error}"
