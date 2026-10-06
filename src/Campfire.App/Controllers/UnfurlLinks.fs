// Port of rust/crates/campfire/src/controllers/unfurl_links.rs
//
// `UnfurlLinksController` (reference/app/controllers/unfurl_links_controller.rb): the composer
// asks for a pasted URL's OpenGraph metadata.
namespace Campfire.App.Controllers

open System.Threading.Tasks
open Campfire.Kit
open Campfire.App
open Campfire.App.Integrations

module UnfurlLinks =
    /// `Opengraph::Metadata.from_url(url)`, as JSON when `valid?` (`Opengraph`). The network is the
    /// system's unless the request carries another one (tests).
    let private opengraphJson (c: Ctx) (url: string) : Task<Result<string option, Error>> =
        task {
            let net =
                match c.Current<Network>() with
                | ValueSome net -> net
                | ValueNone -> Network.system ()
            match! Opengraph.unfurl net url with
            | Error e -> return Error(Internal(exn (UnfurlError.message e)))
            | Ok(Unfurl.Json json) -> return Ok(Some json)
            | Ok Unfurl.NoContent -> return Ok None
        }

    let create (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            // `params.require(:url)`. A hash or array passes `require`, but `URI.parse` can't take it
            // (`InvalidURIError`, rescued), so the metadata has no title and isn't valid.
            let! required = c.Params.Require "url"
            match Option.ofObj required.AsStr with
            | None -> return c.Head Status.NoContent
            | Some url ->
                match! opengraphJson c url with
                // `render json: opengraph`
                | Some(json: string) -> return c.RenderAs(Status.Ok, Response.JsonUtf8, json)
                | None -> return c.Head Status.NoContent
        }
