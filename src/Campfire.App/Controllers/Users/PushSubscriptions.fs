// Port of rust/crates/campfire/src/controllers/users/push_subscriptions.rs
//
// `Users::PushSubscriptionsController` (reference/app/controllers/users/push_subscriptions_controller.rb):
// the signed-in user's Web Push subscriptions.
namespace Campfire.App.Controllers

open System
open System.Threading.Tasks
open Campfire.Views
open Campfire.Kit
open Campfire.App
open Campfire.App.Integrations
open Campfire.App.Presenters
open Campfire.Db
open Campfire.Routes
open Campfire.Ruby

module PushSubscriptions =
    module IndexPage = Campfire.Views.Templates.Users.PushSubscriptions.Index

    let private indexSize = RenderSize()

    /// `params.require(:push_subscription).permit(:endpoint, :p256dh_key, :auth_key)`
    let private pushSubscriptionParams (c: Ctx) : Result<Campfire.Kit.ParamMap, Error> =
        c.Params.Require "push_subscription"
        |> Result.map (fun param -> param.Permit(Params.permitKeys [ "endpoint"; "p256dh_key"; "auth_key" ]))

    /// `Current.user.push_subscriptions.find_by(push_subscription_params)`: only the given keys are
    /// conditions (none at all finds the user's first subscription); nil is `IS NULL`.
    let private findBy (conn: Conn) (userId: int64) (parameters: Campfire.Kit.ParamMap) : PushSubscription option =
        let sql = Text.StringBuilder("""SELECT "push_subscriptions"."id" FROM "push_subscriptions" WHERE "push_subscriptions"."user_id" = ?""")
        let values = ResizeArray<SqlArg>([ I userId ])
        for struct (key, param) in parameters.Iter do
            match Option.ofObj (param.ToS()) with
            | Some value ->
                sql.Append($" AND \"push_subscriptions\".\"{key}\" = ?") |> ignore
                values.Add(S value)
            | None -> sql.Append($" AND \"push_subscriptions\".\"{key}\" IS NULL") |> ignore
        sql.Append " LIMIT 1" |> ignore
        conn.QueryOne(sql.ToString(), values.ToArray(), fun r -> r.Int64 0) |> Option.map (PushSubscription.find conn)

    /// `RestrictedHTTP::PrivateNetworkGuard.resolve(endpoint_uri.host)`, done ahead of the (synchronous)
    /// validation for the one host it asks about: a first pass records the host, then it's resolved.
    let private resolveEndpoint (subscription: PushSubscription) : Task<Map<string, string option>> =
        task {
            let mutable asked: string option = None
            PushSubscription.validate
                (fun host ->
                    asked <- Some host
                    None)
                subscription
            |> ignore
            match asked with
            | Some host ->
                let network = Network.system ()
                let! resolved = Guard.resolve network.Resolver host
                let ip =
                    match resolved with
                    | Ok ip -> Some(ip.ToString())
                    | Error _ -> None
                return Map.ofList [ host, ip ]
            | None -> return Map.empty
        }

    let private resolver (resolved: Map<string, string option>) : string -> string option =
        fun host -> resolved |> Map.tryFind host |> Option.flatten

    /// `subscription.valid?`
    let private validate (subscription: PushSubscription) : Task<Errors> =
        task {
            let! resolved = resolveEndpoint subscription
            return PushSubscription.validate (resolver resolved) subscription
        }

    let index (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            do! c.RespondTo [ Format.Html ] |> Result.map ignore
            let! (user: User) = Concerns.requireCurrentUser c
            let! subscriptions = c.App.Read(fun conn -> PushSubscription.forUser conn user.Id)
            let pushSubscriptions = subscriptions |> List.map Accounts.pushSubscription
            return!
                Page.framedPage
                    c
                    Status.Ok
                    indexSize
                    (fun w ctx -> IndexPage.render w ctx pushSubscriptions)
                    IndexPage.head
                    (fun w ctx -> IndexPage.content w ctx pushSubscriptions)
        }

    let create (c: Ctx) : Task<Result<Response, Error>> =
        act {
            c.WrapParameters("push_subscription", ValueNone)
            do! Concerns.beforeActions c Before.Default
            let! (user: User) = Concerns.requireCurrentUser c
            let userId = user.Id
            let! parameters = pushSubscriptionParams c

            let! existing = c.App.Read(fun conn -> findBy conn userId parameters)
            match existing with
            // Existing endpoints must pass current validations
            | Some subscription ->
                let! errors = validate subscription
                if Errors.isEmpty errors then
                    let! () = c.App.Write(fun tx -> Common.touch tx.Conn "push_subscriptions" subscription.Id (tx.Now()))
                    return c.Head Status.Ok
                else
                    return c.Head Status.UnprocessableEntity
            | None ->
                let value key = Accounts.paramToS parameters key
                let subscription =
                    PushSubscription.build userId (value "endpoint") (value "p256dh_key") (value "auth_key") (Option.ofObj c.Request.UserAgent)
                let! resolved = resolveEndpoint subscription
                let! result = c.App.Db.Write(fun tx -> PushSubscription.create tx subscription (resolver resolved))
                match result with
                | Ok _ -> return c.Head Status.Ok
                | Error(RecordInvalid _) -> return c.Head Status.UnprocessableEntity
                | Error error -> return! Error(Internal(Exception(DbError.display error)))
        }

    /// `@push_subscriptions.destroy_by(id: params[:id])`
    let destroy (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            let! (user: User) = Concerns.requireCurrentUser c
            let userId = user.Id
            match (match c.ParamStr "id" with null -> None | id -> Ruby.integerCast id) with
            | Some id ->
                let! () =
                    c.App.Write(fun tx ->
                        let found =
                            try
                                Some(PushSubscription.find tx.Conn id)
                            with DbException(RecordNotFound _) ->
                                None
                        match found with
                        | Some subscription when subscription.UserId = userId -> PushSubscription.destroy tx subscription
                        | _ -> ())
                ()
            | None -> ()
            return! c.RedirectTo(c.UrlFor(Routes.userPushSubscriptions ()))
        }
