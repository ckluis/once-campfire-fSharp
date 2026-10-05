// Port of rust/crates/rails_compat/src/global_id.rs
/// GlobalID / SignedGlobalID (`gid://campfire/User/1`, `user.attachable_sgid`).
///
/// SGIDs are signed by `GlobalID::Verifier` (key `generate_key("signed_global_ids")`, HMAC-SHA1,
/// URL-safe Base64 *with* padding, the app's `:json_allow_marshal` serializer, and the
/// `{"_rails":{"data":..,"exp":..,"pur":..}}` envelope). Rails 7.0 SGIDs used the legacy
/// envelope with a Marshal-dumped string, which Rails still reads.
namespace Campfire.RailsCompat

open System
open System.Globalization

/// A parsed `gid://app/Model/id`. Query params (Rails puts `?expires_in` into attachable SGIDs)
/// are dropped: the locator ignores them.
type GlobalId =
    { App: string
      ModelName: string
      Id: string }

    override this.ToString() = $"gid://{this.App}/{this.ModelName}/{this.Id}"

module GlobalId =
    [<Literal>]
    let Salt = "signed_global_ids"

    /// `GlobalID.app`, from the application name (`Campfire::Application`).
    [<Literal>]
    let App = "campfire"

    /// `ActionText::Attachable::LOCATOR_NAME`.
    [<Literal>]
    let AttachablePurpose = "attachable"

    /// `SignedGlobalID::DEFAULT_PURPOSE`.
    [<Literal>]
    let DefaultPurpose = "default"

    let create (modelName: string) (id: string) : GlobalId = { App = App; ModelName = modelName; Id = id }

    /// `GlobalID.parse` for the URI form (`URI::GID`): `gid://<app>/<Model>/<id>[?params]`.
    let parse (gid: string) : GlobalId option =
        if not (gid.StartsWith("gid://", StringComparison.Ordinal)) then
            None
        else
            let rest = gid.Substring 6
            let rest = (rest.Split '?').[0]
            match rest.IndexOf '/' with
            | -1 -> None
            | slash ->
                let app = rest.Substring(0, slash)
                let path = rest.Substring(slash + 1)
                match path.IndexOf '/' with
                | -1 -> None
                | slash ->
                    let modelName = path.Substring(0, slash)
                    let id = path.Substring(slash + 1)
                    if app = "" || modelName = "" || id = "" then
                        None
                    else
                        Some { App = app; ModelName = modelName; Id = id }

    /// `GlobalID#to_param`: URL-safe Base64 without padding, as used in Turbo stream names.
    let toParam (gid: GlobalId) : string =
        RailsEncoding.urlsafeEncodeUnpadded (System.Text.Encoding.UTF8.GetBytes(gid.ToString()))

    let fromParam (param: string) : GlobalId option =
        RailsEncoding.urlsafeDecode param
        |> Option.bind (fun bytes ->
            try
                Some(System.Text.UTF8Encoding(false, true).GetString bytes)
            with :? System.Text.DecoderFallbackException ->
                None)
        |> Option.bind parse

    let verifier (secrets: Secrets) : MessageVerifier =
        let secret = secrets.KeyGenerator.GenerateKey(Salt, 64)
        MessageVerifier.create secret Digest.Sha1 Encoding.UrlSafePadded (Serializer.JsonWithFallback true)

    /// `record.attachable_sgid`, i.e. `to_sgid(expires_in: nil, for: "attachable")`. GlobalID turns
    /// the leftover `expires_in: nil` option into a query param, so the signed data is
    /// `gid://campfire/User/1?expires_in`, with no expiry in the envelope.
    let attachableSgid (secrets: Secrets) (gid: GlobalId) : string =
        MessageVerifier.generate (verifier secrets) (Value.String($"{gid}?expires_in")) (Some AttachablePurpose) None

    /// `SignedGlobalID.new(gid_uri, for: purpose, expires_at:)` for a bare GID URI (no params).
    let sgid (secrets: Secrets) (gid: GlobalId) (purpose: string) (expiresAt: Timestamp option) : string =
        MessageVerifier.generate (verifier secrets) (Value.String(gid.ToString())) (Some purpose) expiresAt

    /// globalid < 1.0 signed `{"gid":..,"purpose":..,"expires_at":..}` without a Rails envelope.
    let private verifyWithLegacySelfValidatedMetadata (verifier: MessageVerifier) (sgid: string) (purpose: string) (now: Timestamp) : Value option =
        match MessageVerifier.verify verifier sgid None now with
        | Error _ -> None
        | Ok metadata ->
            match metadata.AsObject with
            | None -> None
            | Some entries ->
                let get key = entries |> List.tryFind (fun (k, _) -> k = key) |> Option.map snd
                let notExpired =
                    match get "expires_at" |> Option.filter (fun v -> not v.IsNull) with
                    | None -> true
                    | Some expiresAt ->
                        match expiresAt.AsString |> Option.bind Timestamps.tryParse with
                        | None -> false
                        | Some expiresAt -> not (now > expiresAt)
                if notExpired && Metadata.rubyToS (get "purpose") = purpose then
                    Some(defaultArg (get "gid") Value.Null)
                else
                    None

    /// `SignedGlobalID.parse(sgid, for: purpose)`: the GID if the signature, purpose and expiry
    /// check out. Looking the record up (and `only:` restrictions) is the caller's job.
    let locateSigned (secrets: Secrets) (sgid: string) (purpose: string) (now: Timestamp) : GlobalId option =
        let verifier = verifier secrets
        let data =
            match MessageVerifier.verify verifier sgid (Some purpose) now with
            | Ok data -> Some data
            | Error _ -> verifyWithLegacySelfValidatedMetadata verifier sgid purpose now
        match data with
        | Some(Value.String uri) ->
            match parse uri with
            | Some gid -> Some gid
            | None -> fromParam uri
        | _ -> None
