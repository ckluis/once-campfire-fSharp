// Port of rust/crates/assets/src/helpers.rs
/// ActionView::Helpers::AssetUrlHelper over Propshaft's static resolver (no asset host, no
/// relative_url_root, as the reference is configured).
module Campfire.Assets.Helpers

open System

/// The URL prefix digested assets are served under (`config.assets.prefix`).
[<Literal>]
let Prefix = "/assets"

/// Propshaft::MissingAssetError, raised by `compute_asset_path` for an unknown logical path.
type MissingAssetError = { LogicalPath: string }

module MissingAssetError =
    let message (e: MissingAssetError) : string = $"The asset '{e.LogicalPath}' was not found in the load path."

/// The digested path (relative to `/assets/`) for a logical path, from the manifest.
let digestedPath (logicalPath: string) : string voption = Embedded.digestedPath logicalPath

/// ActionView::Helpers::AssetUrlHelper::URI_REGEXP: %r{^[-a-z]+://|^(?:cid|data):|^//}i
let private isUri (source: string) : bool =
    let startsWith (prefix: string) = source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
    if startsWith "//" || startsWith "cid:" || startsWith "data:" then
        true
    else
        match source.IndexOf("://", StringComparison.Ordinal) with
        | i when i > 0 -> source.AsSpan(0, i).IndexOfAnyExcept("-abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ") < 0
        | _ -> false

/// Ruby's File.extname.
let private fileExtname (path: string) : string =
    let slash = path.LastIndexOf '/'
    let b = if slash < 0 then path else path.Substring(slash + 1)
    let trimmed = b.TrimStart '.'
    match trimmed.LastIndexOf '.' with
    | -1 -> ""
    | dot -> trimmed.Substring dot

/// File.join(host, path)
let private fileJoin (host: string) (path: string) : string = host.TrimEnd '/' + "/" + path.TrimStart '/'

let private compute (source: string) (extname: string | null) : Result<string, MissingAssetError> =
    if source = "" then
        Ok ""
    elif isUri source then
        Ok source
    else
        // tail, source = source[/([?#].+)$/], source.sub(/([?#].+)$/, "")
        let source, tail =
            match source.IndexOfAny [| '?'; '#' |] with
            | i when i >= 0 && i + 1 < source.Length -> source.Substring(0, i), source.Substring i
            | _ -> source, ""

        let source =
            match extname with
            | null -> source
            | extname when fileExtname source <> extname -> source + extname
            | _ -> source

        if source.StartsWith '/' then
            Ok(source + tail)
        else
            match Embedded.digestedPath source with
            | ValueSome digested -> Ok($"{Prefix}/{digested}{tail}")
            | ValueNone -> Error { LogicalPath = source }

/// Like `tryAssetPath`, but raises for a missing asset the way Rails raises
/// Propshaft::MissingAssetError while rendering.
let private orRaise (result: Result<string, MissingAssetError>) : string =
    match result with
    | Ok path -> path
    | Error e -> raise (InvalidOperationException(MissingAssetError.message e))

/// `asset_path(source)`: "/assets/<digested>" for pipeline assets; URLs and absolute paths pass
/// through; a `?query` or `#fragment` tail is kept.
let tryAssetPath (source: string) : Result<string, MissingAssetError> = compute source null

let assetPath (source: string) : string = orRaise (tryAssetPath source)

let imagePath (source: string) : string = assetPath source

let audioPath (source: string) : string = assetPath source

/// `javascript_path`: appends ".js" unless the source already ends in it.
let javascriptPath (source: string) : string = orRaise (compute source ".js")

/// `stylesheet_path`: appends ".css" unless the source already ends in it.
let stylesheetPath (source: string) : string = orRaise (compute source ".css")

/// `asset_url(source)`: the path joined onto the request's base URL (e.g. "https://host:3000"),
/// which is what Rails uses as the host when no asset_host is configured.
let assetUrl (baseUrl: string) (source: string) : string =
    let path = assetPath source
    if path = "" || isUri path then path else fileJoin baseUrl path

let imageUrl (baseUrl: string) (source: string) : string = assetUrl baseUrl source
