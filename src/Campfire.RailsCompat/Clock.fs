// Port of rust/crates/rails_compat/src/clock.rs
/// `Time.current` behind an interface, and `ActiveSupport::Testing::TimeHelpers` as [`TestClock`].
///
/// The models, cookies and sessions read the time from a [`SharedClock`] as they go; what needs
/// it once (verifiers, storage) takes a `now` argument instead. Parity runs freeze the whole app
/// with a [`TestClock`] (`CAMPFIRE_FROZEN_TIME`), and tests move one.
module Campfire.RailsCompat.Clock

open System

type Clock =
    abstract Now: unit -> Timestamp

type SharedClock = Clock

type SystemClock() =
    interface Clock with
        member _.Now() = DateTimeOffset.UtcNow

/// Real time shifted by `Travel`, or frozen with `TravelTo`.
type TestClock() =
    let gate = obj ()
    let mutable offset = TimeSpan.Zero
    let mutable frozen: Timestamp option = None

    static member FrozenAt(at: Timestamp) : TestClock =
        let clock = TestClock()
        clock.TravelTo at
        clock

    /// `travel_to`: freezes time at `at`.
    member _.TravelTo(at: Timestamp) : unit = lock gate (fun () -> frozen <- Some at)

    /// `travel`: moves the clock forward by `by`, keeping it frozen if it was.
    member _.Travel(by: TimeSpan) : unit =
        lock gate (fun () ->
            match frozen with
            | Some at -> frozen <- Some(at + by)
            | None -> offset <- offset + by)

    member _.TravelBack() : unit =
        lock gate (fun () ->
            offset <- TimeSpan.Zero
            frozen <- None)

    member _.Now() : Timestamp =
        lock gate (fun () ->
            match frozen with
            | Some at -> at
            | None -> DateTimeOffset.UtcNow + offset)

    interface Clock with
        member this.Now() = this.Now()
