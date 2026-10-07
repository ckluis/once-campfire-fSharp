// Port of rust/crates/db/src/testing.rs
//
// Stand-ins for tests (Rust gates them, and the fixtures, behind the `test-support` feature so that
// production can't be wired with a sink that drops every event or with rich text that only
// approximates Action Text's; F# has no features, so the app simply never references them).
namespace Campfire.Db

open System
open System.Text
open System.Threading
open Campfire.RailsCompat.Clock

/// Drops every event.
type NullSink() =
    interface EventSink with
        member _.Emit(_event: Event) : unit = ()

/// Records events (`assert_enqueued_jobs` and friends).
type RecordingSink() =
    let gate = obj ()
    let events = ResizeArray<Event>()

    member _.Events() : Event list = lock gate (fun () -> List.ofSeq events)

    member _.Take() : Event list =
        lock gate (fun () ->
            let taken = List.ofSeq events
            events.Clear()
            taken)

    interface EventSink with
        member _.Emit(event: Event) : unit = lock gate (fun () -> events.Add event)

/// Tag stripping, with mentions read from unverified SGIDs, as Campfire's plain text reads them
/// (`reference/lib/rails_ext/action_text_attachables.rb`); the exact rules belong to
/// `Campfire.RichText`. It reports no mentions: those need verified SGIDs (`Content#attachables`),
/// so tests that need mentions state them.
type BasicRichText() =
    static let attachmentClose = "</action-text-attachment>"

    static let userName (conn: Conn) (id: int64) : string option =
        try
            User.findById conn id |> Option.map (fun user -> user.Name)
        with _ ->
            None

    static let attributeValues (html: string) (name: string) : string list =
        let needle = name + "=\""
        let values = ResizeArray<string>()
        let mutable rest = html
        let mutable go = true
        while go do
            match rest.IndexOf(needle, StringComparison.Ordinal) with
            | -1 -> go <- false
            | start ->
                let after = rest.Substring(start + needle.Length)
                match after.IndexOf '"' with
                | -1 -> go <- false
                | stop ->
                    values.Add(after.Substring(0, stop))
                    rest <- after.Substring stop
        List.ofSeq values

    /// Reads `gid://campfire/User/<id>` out of an SGID's message without verifying it.
    static let userIdFromSgid (sgid: string) : int64 option =
        let first = sgid.Split("--")[0]
        let message = first.Replace("%3D", "=").Replace("%2B", "+").Replace("%2F", "/")
        let decoded =
            try
                Some(Convert.FromBase64String message)
            with :? FormatException ->
                try
                    Some(Convert.FromBase64String(message.Replace('-', '+').Replace('_', '/')))
                with :? FormatException ->
                    None
        match decoded with
        | None -> None
        | Some bytes ->
            let text = Encoding.UTF8.GetString bytes
            let marker = "gid://campfire/User/"
            match text.IndexOf(marker, StringComparison.Ordinal) with
            | -1 -> None
            | at ->
                let digits = text.Substring(at + marker.Length) |> Seq.takeWhile Char.IsAsciiDigit |> Seq.toArray |> String
                match Int64.TryParse digits with
                | true, id -> Some id
                | _ -> None

    static let decodeEntities (s: string) : string =
        s.Replace("&nbsp;", "\u00a0").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&amp;", "&")

    interface RichText with
        member _.ToPlainText(conn: Conn, html: string) : string =
            let out = StringBuilder()
            let mutable rest = html
            let mutable go = true
            while go do
                match rest.IndexOf '<' with
                | -1 -> go <- false
                | start ->
                    out.Append(rest.Substring(0, start)) |> ignore
                    match rest.IndexOf('>', start) with
                    | -1 ->
                        rest <- ""
                        go <- false
                    | stop ->
                        let tag = rest.Substring(start + 1, stop - start - 1)
                        let closing = tag.StartsWith "/"
                        let name =
                            tag.TrimStart('/') |> Seq.takeWhile (fun c -> Char.IsAsciiLetterOrDigit c || c = '-') |> Seq.toArray |> String
                        let mutable skipped = false
                        if name = "action-text-attachment" && not closing then
                            let mention =
                                attributeValues tag "sgid"
                                |> List.tryHead
                                |> Option.bind userIdFromSgid
                                |> Option.bind (userName conn)
                            match mention with
                            | Some name -> out.Append('@').Append(name) |> ignore
                            | None -> ()
                            // Skip the attachment's inner content.
                            match rest.IndexOf(attachmentClose, start, StringComparison.Ordinal) with
                            | -1 -> ()
                            | close ->
                                rest <- rest.Substring(close + attachmentClose.Length)
                                skipped <- true
                        if not skipped then
                            if
                                List.contains name [ "br"; "p"; "div"; "li"; "h1"; "blockquote"; "pre" ]
                                && out.Length > 0
                                && not closing
                            then
                                out.Append '\n' |> ignore
                            rest <- rest.Substring(stop + 1)
            out.Append rest |> ignore
            decodeEntities (out.ToString().Trim())

        member _.MentionedUserIds(_conn: Conn, _html: string) : int64 list = []

module Testing =
    /// The system clock, no side effects and `BasicRichText`.
    let defaultEnv () : Env =
        { Clock = SystemClock()
          Sink = NullSink()
          RichText = BasicRichText()
          BcryptCost = Campfire.RailsCompat.Password.Cost }
