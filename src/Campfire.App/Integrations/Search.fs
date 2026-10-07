// Port of rust/crates/campfire/src/integrations/search.rs
//
// `SearchesController#query` (reference/app/controllers/searches_controller.rb):
// `params[:q]&.gsub(/[^[:word:]]/, " ")`, which leaves only Onigmo's Unicode word characters
// (alphabetic, marks, decimal digits, connector punctuation, join controls) for the FTS5 `MATCH`.
namespace Campfire.App.Integrations

open System.Text

module Search =
    /// Onigmo's `[[:word:]]` in the reference's Ruby (Unicode 15.0), from the generated table.
    let isWord (codePoint: int) : bool =
        let ranges = SearchWordRanges.ranges
        let mutable lo = 0
        let mutable hi = ranges.Length / 2 - 1
        let mutable found = false
        while not found && lo <= hi do
            let mid = (lo + hi) / 2
            if ranges[mid * 2 + 1] < codePoint then lo <- mid + 1
            elif ranges[mid * 2] > codePoint then hi <- mid - 1
            else found <- true
        found

    /// Replaces every character that isn't a `[[:word:]]` character with a space (one space per
    /// character). `None` stays `None` (no `q` param).
    let sanitizeQuery (q: string option) : string option =
        q
        |> Option.map (fun q ->
            let output = StringBuilder(q.Length)
            for rune in q.EnumerateRunes() do
                if isWord rune.Value then output.Append(rune.ToString()) |> ignore else output.Append(' ') |> ignore
            output.ToString())
