/// The resolver the corpus tests (and reference-tools/richtext/differential) use, built from the
/// corpus file: users, rooms and the SGIDs Rails minted.
module Campfire.RichText.Tests.CorpusResolver

open System
open System.Text.Json
open Campfire.RichText

let private str (e: JsonElement) : string = e.GetString() |> nonNull

/// Stands in for the app: SGIDs Rails minted are "verified" by exact match, and GIDs are looked
/// up by model and id.
type TestResolver(users: MentionUser list, rooms: int64 list, signed: (string * string * int64 * bool) list) =
    interface IAttachableResolver with
        member _.LocateSigned sgid =
            match signed |> List.tryFind (fun (s, _, _, _) -> s = sgid) with
            | Some(_, model, id, true) when model = "User" -> SignedLookup.User(users |> List.find (fun u -> u.Id = id))
            | Some(_, model, _, _) -> SignedLookup.MissingRecord model
            | None -> SignedLookup.Invalid

        member _.FindGid gid =
            // GlobalID's default locator ignores the app name
            if not (gid.StartsWith("gid://", StringComparison.Ordinal)) then
                GidLookup.NotFound
            elif not (gid |> Seq.forall (fun c -> c < '\u0080')) then
                GidLookup.NotFound
            else
                let rest = gid.Substring 6
                match rest.IndexOf '/' with
                | -1 -> GidLookup.NotFound
                | slash ->
                    let rest = (rest.Substring(slash + 1).Split '?').[0]
                    match rest.IndexOf '/' with
                    | -1 -> GidLookup.NotFound
                    | s2 ->
                        let model = rest.Substring(0, s2)
                        match Int64.TryParse(rest.Substring(s2 + 1), Globalization.NumberStyles.AllowLeadingSign, Globalization.CultureInfo.InvariantCulture) with
                        | true, id ->
                            match model with
                            | "User" ->
                                match users |> List.tryFind (fun u -> u.Id = id) with
                                | Some u -> GidLookup.User u
                                | None -> GidLookup.NotFound
                            | "Room" when List.contains id rooms -> GidLookup.OtherModel
                            | _ -> GidLookup.NotFound
                        | _ -> GidLookup.NotFound

let resolverOf (json: JsonElement) : IAttachableResolver =
    let users =
        [ for u in json.GetProperty("users").EnumerateArray() ->
              { Id = u.GetProperty("id").GetInt64()
                Name = str (u.GetProperty "name")
                Title = str (u.GetProperty "title")
                AttachableSgid = str (u.GetProperty "attachable_sgid")
                UserPath = str (u.GetProperty "user_path")
                AvatarPath = str (u.GetProperty "avatar_path") } ]
    let rooms = [ for r in json.GetProperty("rooms").EnumerateArray() -> r.GetInt64() ]
    let signed =
        [ for s in json.GetProperty("signed").EnumerateArray() ->
              str (s.GetProperty "sgid"), str (s.GetProperty "model"), s.GetProperty("id").GetInt64(), s.GetProperty("exists").GetBoolean() ]
    TestResolver(users, rooms, signed)
