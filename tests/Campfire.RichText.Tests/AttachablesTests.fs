// Port of the tests in rust/crates/richtext/src/attachables.rs
module Campfire.RichText.Tests.AttachablesTests

open System
open Xunit
open Campfire.RailsCompat
open Campfire.RichText
open Campfire.RichText.Tests.Support
open Campfire.Tests

/// `GlobalID.find` over the reference's records the vectors name: users 1 and 2 exist, and the
/// default locator ignores the GID's app.
type private VectorRecords() =
    interface IAttachableResolver with
        member _.LocateSigned _ = SignedLookup.Invalid

        member _.FindGid gid =
            let user id =
                GidLookup.User { Id = id; Name = ""; Title = ""; AttachableSgid = ""; UserPath = ""; AvatarPath = "" }
            if not (gid.StartsWith "gid://") then
                GidLookup.NotFound
            else
                let rest = ((gid.Substring 6).Split '?').[0]
                match rest.IndexOf '/' with
                | -1 -> GidLookup.NotFound
                | slash ->
                    let path = rest.Substring(slash + 1)
                    if path.StartsWith "User/" then
                        match path.Substring 5 with
                        | "1" -> user 1L
                        | "2" -> user 2L
                        | _ -> GidLookup.NotFound
                    else
                        GidLookup.OtherModel

/// The reference's answers for SGIDs whose signature doesn't verify (`unverified_sgids` in
/// `vectors/rails_compat.json`, from `reference-tools/rails_compat_vectors.rb`).
[<Fact>]
let ``possibly_expired_sgids_find_users_like_rails`` () =
    use vectors = Repo.vector "rails_compat"
    let cases = vectors.RootElement.GetProperty("unverified_sgids").EnumerateArray() |> Seq.toArray
    // Every vector case is run: a case that silently went missing would fail here.
    Assert.Equal(23, cases.Length)
    let ctx = render (VectorRecords()) None
    let mutable run = 0
    for case in cases do
        let label = case.GetProperty("case").GetString()
        let sgid =
            match case.GetProperty("sgid").ValueKind with
            | Text.Json.JsonValueKind.String -> ValueSome(case.GetProperty("sgid").GetString() |> nonNull)
            | _ -> ValueNone
        let found = Attachables.attachableFromPossiblyExpiredSgid sgid ctx |> Result.map (Option.map (fun u -> u.Id))
        let expected = case.GetProperty "expected"
        match expected.ValueKind with
        | Text.Json.JsonValueKind.Null -> Assert.True((found = Ok None), label)
        | Text.Json.JsonValueKind.String ->
            let gid = expected.GetString() |> nonNull
            let id = Int64.Parse(gid.Substring(gid.LastIndexOf '/' + 1))
            Assert.True((found = Ok(Some id)), label)
        | _ -> Assert.True(isError found, $"{label} should raise like Rails ({expected})")
        run <- run + 1
    Assert.Equal(23, run)
