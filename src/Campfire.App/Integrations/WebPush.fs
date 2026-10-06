// Port of the entry point `deliver_test_notification` of rust/crates/campfire/src/integrations/web_push.rs
//
// `WebPush::Notification` delivery. The pool, the encryption, VAPID signing and the HTTP exchange are
// the integrations unit's; until then this raises, and the test-notification action answers a 500
// (Rails answers one too when delivery raises).
namespace Campfire.App.Integrations

open System
open System.Threading.Tasks
open Campfire.App
open Campfire.Db

module WebPush =
    /// `Users::PushSubscriptions::TestNotificationsController#create`: a "Campfire Test"
    /// notification with a random body, delivered inline. `path` is `user_push_subscriptions_url`
    /// (a full URL); `badge` is the subscriber's unread count. Errors propagate, as in Rails.
    let deliverTestNotification
        (_net: Network)
        (_pool: IWebPushPool)
        (_subscription: PushSubscription)
        (_badge: int64)
        (_path: string)
        : Task<Result<unit, exn>> =
        Task.FromResult(Error(NotImplementedException "Web Push delivery is ported with the integrations unit" :> exn))
