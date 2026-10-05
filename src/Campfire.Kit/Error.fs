// Port of rust/crates/kit/src/error.rs
//
// Errors an action can return, and the status Rails would answer with.
//
// Rails maps exception classes to statuses in `ActionDispatch::ExceptionWrapper.rescue_responses`
// and renders `public/<status>.html` through `ActionDispatch::PublicExceptions`. `Halt` is not an
// error at all: it's how a before-action that rendered or redirected stops the chain.
namespace Campfire.Kit

open System

/// Why request parameters could not be built (`rust/crates/kit/src/params.rs`).
type ParamError =
    /// `ParameterTypeError`: e.g. `a=1&a[b]=2`.
    | Type of string
    /// `InvalidParameterError`: bad %-encoding or invalid UTF-8.
    | Invalid of string
    /// `ParamsTooDeepError`.
    | TooDeep
    /// `QueryLimitError`.
    | Limit of string
    /// `ActionDispatch::Http::Parameters::ParseError` (malformed JSON or multipart).
    | Parse

    member this.Message: string =
        match this with
        | Type message
        | Invalid message
        | Limit message -> message
        | TooDeep -> "exceeded available parameter key space"
        | Parse -> "Error occurred while parsing request parameters"

type Error =
    /// A before-action (or the action itself) produced the response; stop and send it.
    | Halt of Response
    /// `ActionController::BadRequest`, `ParamError`s, `Parameters::ParseError`.
    | BadRequest of string
    /// `ActionController::ParameterMissing`.
    | ParameterMissing of string
    /// `ActionController::InvalidAuthenticityToken` (the `:exception` forgery strategy).
    | InvalidAuthenticityToken of string
    /// `ActionController::InvalidCrossOriginRequest`.
    | InvalidCrossOriginRequest
    /// `ActionController::UnknownFormat` / `MissingExactTemplate`.
    | UnknownFormat
    /// `ActiveRecord::RecordNotFound`, `ActionController::RoutingError`.
    | NotFound
    /// `ActionController::UnknownHttpMethod`.
    | MethodNotAllowed
    /// `ActionDispatch::Cookies::CookieOverflow` (not rescued by Rails: a 500).
    | CookieOverflow of string
    /// `ActionController::Redirecting::UnsafeRedirectError` and friends (a 500).
    | UnsafeRedirect of string
    /// `ActionDispatch::RemoteIp::IpSpoofAttackError` (a 500).
    | IpSpoofAttack
    /// Any other status an app wants to map an error to.
    | Status of int
    /// Like `Status`, keeping the error that caused it for the log line (see `Error.withStatus`).
    | WithStatus of int * exn
    | Internal of exn

module Error =
    /// `{:#}` of an `anyhow::Error`: the message and then each cause, joined with ": ".
    let private chain (error: exn) : string =
        let messages = ResizeArray<string>()
        let mutable current: exn | null = error
        while not (isNull current) do
            let e = nonNull current
            messages.Add e.Message
            current <- e.InnerException
        String.Join(": ", messages)

    let private statusText (code: int) : string =
        match Campfire.Kit.Status.canonicalReason code with
        | null -> string code
        | reason -> $"{code} {reason}"

    /// The error's text as the log line shows it (`{error:#}`): `Display`, with causes.
    let display (error: Error) : string =
        match error with
        | Halt response -> $"halted with {statusText response.Status}"
        | BadRequest message -> $"bad request: {message}"
        | ParameterMissing message -> $"param is missing or the value is empty or invalid: {message}"
        | InvalidAuthenticityToken message -> $"invalid authenticity token: {message}"
        | InvalidCrossOriginRequest -> "invalid cross-origin request"
        | UnknownFormat -> "unknown format"
        | NotFound -> "not found"
        | MethodNotAllowed -> "unknown http method"
        | CookieOverflow message -> $"{message} cookie overflowed"
        | UnsafeRedirect message -> $"unsafe redirect: {message}"
        | IpSpoofAttack -> "IP spoofing attack"
        | Status code -> statusText code
        | WithStatus(code, cause) -> $"{statusText code}: {chain cause}"
        | Internal cause -> chain cause

    let status (error: Error) : int =
        match error with
        | Halt response -> response.Status
        | BadRequest _
        | ParameterMissing _ -> Campfire.Kit.Status.BadRequest
        | InvalidAuthenticityToken _
        | InvalidCrossOriginRequest -> Campfire.Kit.Status.UnprocessableEntity
        | UnknownFormat -> Campfire.Kit.Status.NotAcceptable
        | NotFound -> Campfire.Kit.Status.NotFound
        | MethodNotAllowed -> Campfire.Kit.Status.MethodNotAllowed
        | Status code
        | WithStatus(code, _) -> code
        | CookieOverflow _
        | UnsafeRedirect _
        | IpSpoofAttack
        | Internal _ -> Campfire.Kit.Status.InternalServerError

    let internalError (error: exn) : Error = Internal error

    /// Answer `error` with `status` instead of a 500.
    let withStatus (status: int) (error: exn) : Error = WithStatus(status, error)

    /// `From<ParamError>`.
    let ofParamError (error: ParamError) : Error = BadRequest error.Message

/// What an action produces: a response, or the error Rails would answer with.
type ActionResult = Result<Response, Error>

[<AutoOpen>]
module ErrorHelpers =
    /// Stop the before-action chain with `response`, like a Rails callback that renders or redirects.
    ///
    /// ```fsharp
    /// let ensureCanAdminister (c: Ctx) =
    ///     if not (canAdminister c) then halt (c.Head Status.Forbidden) else Ok ()
    /// ```
    let halt (response: Response) : Result<'a, Error> = Error(Halt response)
