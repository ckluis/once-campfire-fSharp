// Port of the entry point of rust/crates/campfire/src/integrations/opengraph.rs
//
// Link unfurling: `UnfurlLinksController#create` (reference/app/controllers/unfurl_links_controller.rb)
// over `Opengraph::Metadata`, `Location`, `Fetch` and `Document` (reference/app/models/opengraph).
//
// What is here is the part that needs no HTTP client: `Opengraph::Location#valid?` (http(s), and a host
// the private network guard resolves to a public address). A URL that fails it unfurls nothing in the
// reference too (no document, so no title: `head :no_content`), without any request. Fetching the
// document, following redirects, parsing the page and checking the image are the integrations unit's;
// until then a valid URL raises, which the controller answers as a 500.
namespace Campfire.App.Integrations

open System
open System.Threading.Tasks
open Campfire.RichText

/// What `UnfurlLinksController#create` responds with.
type Unfurl =
    /// `render json: opengraph` (200, `application/json`): the body.
    | Json of string
    /// `head :no_content`
    | NoContent

module Opengraph =
    /// `Location#valid?`: both validations run, so the host is resolved even for a non-http URL.
    let private isValidLocation (net: Network) (url: string) : Task<bool> =
        task {
            let parsed =
                match RubyUri.parse url with
                | Ok uri -> Some uri
                | Error _ -> None
            let! public' =
                match parsed |> Option.bind (fun uri -> uri.Host) with
                | Some host ->
                    task {
                        match! Guard.resolve net.Resolver host with
                        | Ok _ -> return true
                        | Error _ -> return false
                    }
                | None -> Task.FromResult false
            return public' && (parsed |> Option.exists RubyUri.isHttp)
        }

    /// The action after `params.require(:url)` (a missing or blank `url` is the controller's 400).
    let unfurl (net: Network) (url: string) : Task<Result<Unfurl, exn>> =
        task {
            match! isValidLocation net url with
            | false -> return Ok NoContent
            | true ->
                return
                    Error(
                        NotImplementedException(
                            "Opengraph fetching (Opengraph::Fetch, Metadata, Document) is ported with the integrations unit"
                        )
                        :> exn
                    )
        }
