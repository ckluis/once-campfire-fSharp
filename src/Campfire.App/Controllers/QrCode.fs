// Port of rust/crates/campfire/src/controllers/qr_code.rs
//
// `QrCodeController` (reference/app/controllers/qr_code_controller.rb): a QR code SVG for a
// Base64url-encoded URL. (`QrCode/Rqrcode.fs` is the port of the gem.)
namespace Campfire.App.Controllers

open System.Threading.Tasks
open Campfire.Kit
open Campfire.App

module QrCode =
    /// `allow_unauthenticated_access`
    let show (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c (Before.allowUnauthenticatedAccess Before.Default)
            // `Base64.urlsafe_decode64(params[:id])` raises ArgumentError (a 500) on malformed input.
            let id = match c.ParamStr "id" with null -> "" | id -> id
            let! url =
                match Campfire.RailsCompat.RailsEncoding.urlsafeDecode id with
                | Some url -> Ok url
                | None -> Error(Internal(exn "invalid base64"))
            // Too much to encode is the client's doing (rqrcode raises, a 500 in Rails).
            let! qrCode =
                match Rqrcode.svgBytes url with
                | Some svg -> Ok svg
                | None -> Error(Status Status.UnprocessableEntity)

            // `expires_in 1.year, public: true`
            c.ExpiresIn(31_556_952UL, { ExpiresIn.Default with Public = true })
            return c.RenderAs(Status.Ok, "image/svg+xml; charset=utf-8", qrCode)
        }
