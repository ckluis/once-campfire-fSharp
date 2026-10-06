// Port of rust/crates/campfire/src/controllers/presenters/pagination.rs
//
// geared_pagination 1.2.0's `set_page_and_extract_portion_from records, per_page:` for an
// unordered-by-cursor relation (`PortionAtOffset`), and its JSON response headers.
namespace Campfire.App.Presenters

open System
open System.Text
open Campfire.Kit
open Campfire.Ruby

module Pagination =
    module Url =
        let private hexValue (b: byte) : int =
            if b >= byte '0' && b <= byte '9' then int b - int '0'
            elif b >= byte 'a' && b <= byte 'f' then int b - int 'a' + 10
            elif b >= byte 'A' && b <= byte 'F' then int b - int 'A' + 10
            else -1

        /// `Addressable::URI.unencode_component`. `query_values` turns "+" in values (not keys) into
        /// spaces first, so an encoded `%2B` stays a "+".
        let unencode (value: string) : string =
            let bytes = Encoding.UTF8.GetBytes value
            let output = ResizeArray<byte>(bytes.Length)
            let mutable i = 0
            while i < bytes.Length do
                if bytes[i] = byte '%' && i + 2 < bytes.Length && hexValue bytes[i + 1] >= 0 && hexValue bytes[i + 2] >= 0 then
                    output.Add(byte (hexValue bytes[i + 1] * 16 + hexValue bytes[i + 2]))
                    i <- i + 3
                else
                    output.Add bytes[i]
                    i <- i + 1
            Encoding.UTF8.GetString(output.ToArray())

        /// `Addressable::URI.encode_component(value, CharacterClassesRegexps::UNRESERVED)`, which keeps
        /// the same characters as `ERB::Util.url_encode` (`Campfire.Ruby` checks both against the reference).
        let encodeComponent (value: string) : string = Ruby.urlEncode value

        let private split (separator: char) (text: string) : string * string option =
            match text.IndexOf separator with
            | -1 -> text, None
            | at -> text.Substring(0, at), Some(text.Substring(at + 1))

        /// Addressable's `uri.query_values = (uri.query_values || {}).merge("page" => page)`: the query
        /// is re-encoded from a hash, so keys come out sorted, duplicates collapse to the last value, and
        /// components are percent-encoded outside Addressable's unreserved set.
        let withPage (url: string) (page: string) : string =
            let baseUrl, fragment = split '#' url
            let path, query = split '?' baseUrl
            let values = ResizeArray<string * string option>()
            for pair in (defaultArg query "").Split('&', StringSplitOptions.RemoveEmptyEntries) do
                let key, value =
                    match split '=' pair with
                    | key, Some value -> unencode key, Some(unencode (value.Replace('+', ' ')))
                    | key, None -> unencode key, None
                match values.FindIndex(fun (existing, _) -> existing = key) with
                | -1 -> values.Add((key, value))
                | at -> values[at] <- (key, value)
            match values.FindIndex(fun (key, _) -> key = "page") with
            | -1 -> values.Add(("page", Some page))
            | at -> values[at] <- ("page", Some page)
            // Rust compares the keys' UTF-8 bytes; the sort is stable.
            let byBytes (a: string, _) (b: string, _) =
                ReadOnlySpan<byte>(Encoding.UTF8.GetBytes a).SequenceCompareTo(ReadOnlySpan<byte>(Encoding.UTF8.GetBytes b))
            let query =
                values
                |> List.ofSeq
                |> List.sortWith byBytes
                |> List.map (fun (key, value) ->
                    match value with
                    | Some value -> $"{encodeComponent key}={encodeComponent value}"
                    | None -> encodeComponent key)
                |> String.concat "&"
            let result = $"{path}?{query}"
            match fragment with
            | Some fragment -> $"{result}#{fragment}"
            | None -> result


    [<Sealed; NoComparison>]
    type Page private (number: int64, recordsCount: int64, ratios: int64[]) =
        /// `Recordset.new(records, per_page:).page(params[:page])`.
        static member Create(pageParam: string | null, recordsCount: int64, perPage: int64 list) : Page =
            // `param.to_i > 0 ? param.to_i : 1`, capped so the page arithmetic can't overflow on a huge `?page=`.
            let number =
                match pageParam with
                | null -> 0L
                | text -> Ruby.toI text
            Page(Math.Clamp(number, 1L, 1_000_000_000L), recordsCount, Array.ofList perPage)

        /// `page.number`
        member _.Number = number

        /// `recordset.records_count`
        member _.RecordsCount = recordsCount

        member private _.Ratio(pageNumber: int64) : int64 =
            let i = pageNumber - 1L
            if i >= 0L && i < int64 ratios.Length then ratios[int i] else ratios[ratios.Length - 1]

        /// `PortionAtOffset#limit`
        member this.Limit: int64 = this.Ratio number

        /// `PortionAtOffset#offset`
        member this.Offset: int64 =
            let size = int64 ratios.Length
            let mutable variable = 0L
            for index in 0L .. (min (number - 1L) (size - 1L)) - 1L do
                variable <- variable + this.Ratio(index + 1L)
            variable + (max (number - size) 0L) * this.Ratio size

        /// The page's slice of the (already ordered) records.
        member this.Records(all: 'T list) : 'T list =
            let skip = int (min this.Offset (int64 all.Length))
            all |> List.skip skip |> List.truncate (int (min this.Limit (int64 Int32.MaxValue)))

        /// `recordset.page_count`
        member this.PageCount: int64 =
            let mutable count = 0L
            let mutable residual = recordsCount
            while residual > 0L do
                count <- count + 1L
                residual <- residual - this.Ratio count
            max count 1L

        /// `page.last?`
        member this.IsLast: bool = number = this.PageCount

        /// `page.next_param`
        member _.NextParam: int64 = number + 1L

        /// `set_paginated_headers` (after_action), for JSON requests: `X-Total-Count`, and a `Link`
        /// to the next page unless this is the last one.
        member this.ApplyHeaders(c: Ctx) : unit =
            let json =
                match c.Formats() with
                | Ok(first :: _) -> first.Is "json"
                | _ -> false
            if json then
                c.SetHeader("x-total-count", string recordsCount)
                if not this.IsLast then
                    let url = Url.withPage c.Request.Url (string this.NextParam)
                    c.SetHeader("link", $"<{url}>; rel=\"next\"")
