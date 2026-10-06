// Port of rust/crates/campfire/src/controllers/users/push_subscriptions/test_notifications.rs
//
// `Users::PushSubscriptions::TestNotificationsController`
// (reference/app/controllers/users/push_subscriptions/test_notifications_controller.rb).
namespace Campfire.App.Controllers

open System.Threading.Tasks
open Campfire.Kit
open Campfire.App
open Campfire.App.Integrations
open Campfire.Db
open Campfire.Routes
open Campfire.Ruby

module TestNotifications =
    /// `@push_subscription.notification(title: "Campfire Test", body: Random.uuid, path: user_push_subscriptions_url).deliver`
    let create (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            let! (user: User) = Concerns.requireCurrentUser c
            let userId = user.Id
            let! id =
                match (match c.ParamStr "push_subscription_id" with null -> None | id -> Ruby.integerCast id) with
                | Some id -> Ok id
                | None -> Error NotFound
            let! subscription, badge =
                c.App.Read(fun conn ->
                    // `Current.user.push_subscriptions.find(params[:push_subscription_id])`
                    let subscription = PushSubscription.find conn id
                    if subscription.UserId <> userId then Err.fail (RecordNotFound "Push::Subscription")
                    subscription, Membership.unreadCount conn userId)

            let location = c.UrlFor(Routes.userPushSubscriptions ())
            let! pool =
                match c.App.WebPush with
                | Some pool -> Ok pool
                | None -> Error(Internal(exn "Web Push is off (no valid VAPID keys)"))
            match! WebPush.deliverTestNotification (Network.system ()) pool.Vapid subscription badge location with
            | Error e -> return! Error(Internal(DeliveryError.toExn e))
            | Ok() -> return! c.RedirectTo location
        }
