// Port of rust/crates/assets/src/lib.rs
/// The reference's asset pipeline, precomputed at build time and embedded in the assembly.
///
/// `Campfire.Assets.Build` digests and compiles `reference/app/assets`, `reference/app/javascript`,
/// `reference/vendor/javascript` and the gem assets in `vendor/` exactly as Propshaft's
/// `assets:precompile` does, renders the import map from `reference/config/importmap.rb`, and
/// embeds everything along with `reference/public`.
///
/// - `assetPath` and friends (`Helpers`): ActionView's asset URL helpers over the Propshaft manifest.
/// - `stylesheetLinkTag`, `stylesheetLinkTagAll` and `javascriptImportmapTags` (`Tags`): the
///   layout's head tags, plus the `link` preload header Rails sends alongside them.
/// - `serve` (`Serve`): ActionDispatch::Static over the embedded public/ directory.
///
/// `Helpers`, `Tags` and `Serve` are modules of their own, as in Rust; what `lib.rs` re-exports is
/// re-exported here.
module Campfire.Assets.Assets

/// The URL prefix digested assets are served under (`config.assets.prefix`).
[<Literal>]
let Prefix = Helpers.Prefix

let digestedPath = Helpers.digestedPath
let tryAssetPath = Helpers.tryAssetPath
let assetPath = Helpers.assetPath
let assetUrl = Helpers.assetUrl
let audioPath = Helpers.audioPath
let imagePath = Helpers.imagePath
let imageUrl = Helpers.imageUrl
let javascriptPath = Helpers.javascriptPath
let stylesheetPath = Helpers.stylesheetPath

let allStylesheetPaths = Tags.allStylesheetPaths
let appendPreloadLinks = Tags.appendPreloadLinks
let javascriptImportmapTags = Tags.javascriptImportmapTags
let stylesheetLinkTag = Tags.stylesheetLinkTag
let stylesheetLinkTagAll = Tags.stylesheetLinkTagAll

let serve = Serve.serve

/// Propshaft's manifest, `{"logical": {"digested_path": ..., "integrity": null}}`, as served
/// from `/assets/.manifest.json`.
let manifestJson () : string = Embedded.manifestJson.Value

/// Every `(logical path, digested path)` pair, sorted by logical path.
let manifest () : (string * string)[] = Embedded.manifest
