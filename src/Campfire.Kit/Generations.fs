// Port of `Generations` in rust/crates/kit/src/deflater/splice.rs
//
// The bounded cache the gzip output (and, in the response cache, page parts) are kept in.
namespace Campfire.Kit

open System
open System.Collections.Generic

/// What each stored entry costs beyond its bytes: the map's slot, its key and the allocation.
module CacheCosts =
    [<Literal>]
    let EntryOverhead = 128

/// A map in two generations, bounded by what its entries cost: reading an old entry promotes it,
/// and when the young generation costs more than half the budget it becomes the old one (dropping
/// the previous old one). Bounded, and what pages keep using stays. Not thread-safe: callers lock.
[<Sealed>]
type Generations<'K, 'V when 'K: equality and 'K: not null>(budget: int, cost: 'K -> 'V -> int, comparer: IEqualityComparer<'K>) =
    let mutable young = Dictionary<'K, 'V>(comparer)
    let mutable old = Dictionary<'K, 'V>(comparer)
    let mutable youngCost = 0

    new(budget: int, cost: 'K -> 'V -> int) = Generations<'K, 'V>(budget, cost, EqualityComparer<'K>.Default)

    /// What the young generation's entries cost.
    member _.YoungCost = youngCost

    /// How many entries are held, both generations together.
    member _.Count = young.Count + old.Count

    member private _.RotateWhenFull() =
        if youngCost > budget / 2 then
            old <- young
            young <- Dictionary<'K, 'V>(comparer)
            youngCost <- 0

    member this.Insert(key: 'K, value: 'V) : unit =
        youngCost <- youngCost + cost key value
        match young.TryGetValue key with
        // Another request stored the same meanwhile.
        | true, existing ->
            youngCost <- youngCost - cost key existing
            young[key] <- value
        | _ -> young[key] <- value
        this.RotateWhenFull()

    /// What `read` makes of the entry for `key`, promoting it if it's old.
    member this.Get(key: 'K, read: 'V -> 'R) : 'R voption =
        match young.TryGetValue key with
        | true, value -> ValueSome(read value)
        | _ ->
            match old.TryGetValue key with
            | true, value ->
                old.Remove key |> ignore
                let found = read value
                this.Insert(key, value)
                ValueSome found
            | _ -> ValueNone
