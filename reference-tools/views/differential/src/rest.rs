//! The operations for the remaining templates (unit 4.3): the account, bot, custom styles, sign-in, join, first-run,
//! profile and push-subscription pages, their partials, the avatar SVG and the PWA manifest and service worker.
//! `run` answers `None` for any other operation.
//!
//! A page operation takes `frame` (also render in the Turbo-Frame layout) and answers `out` (the page as served),
//! `text` (the recorded page's text) and `fragments`; the pages here hold no cached fragments, so the answer is
//! checked against a second render, a recorded one, and, for `frame`, the layout's.

use askama::Template;
use campfire_views::accounts::{self, Bot, BotForm, BotRoom};
#[allow(unused_imports)]
use campfire_views::helpers::{self as h, filters};
use campfire_views::users::{self, ProfileMembership, PushSubscription};
use campfire_views::*;
use serde_json::{Value, json};

use crate::ops::{self, Out};

// ---- templates this crate declares because rust/crates/views has no struct for them ----------------------

#[derive(Template)]
#[template(path = "accounts/bots/_form.html")]
struct BotFormPartial<'a> {
    ctx: &'a ViewContext<'a>,
    form: &'a h::FormWith,
    bot: &'a BotForm,
}

#[derive(Template)]
#[template(path = "accounts/bots/_bot.html")]
struct BotPartial<'a> {
    ctx: &'a ViewContext<'a>,
    bot: &'a Bot,
}

#[derive(Template)]
#[template(path = "users/profiles/_membership.html")]
struct MembershipPartial<'a> {
    ctx: &'a ViewContext<'a>,
    membership: &'a ProfileMembership,
}

#[derive(Template)]
#[template(path = "users/push_subscriptions/_push_subscription.html")]
struct PushSubscriptionPartial<'a> {
    ctx: &'a ViewContext<'a>,
    push_subscription: &'a PushSubscription,
}

// ---- reading arguments ------------------------------------------------------------------------------------

fn error(e: impl ToString) -> String {
    e.to_string()
}

fn users(list: &Value) -> Vec<users::UserSummary> {
    ops::arr(list).iter().map(ops::user_summary).collect()
}

fn help_contact(v: &Value) -> Option<accounts::HelpContact> {
    v.is_object().then(|| accounts::HelpContact { name: ops::s(&v["name"]), email_address: ops::s(&v["email_address"]) })
}

fn bot_form(v: &Value) -> BotForm {
    BotForm { name: ops::opt(&v["name"]), webhook_url: ops::opt(&v["webhook_url"]), avatar_attachment_url: ops::opt(&v["avatar_attachment_url"]) }
}

fn bot(v: &Value) -> Bot {
    Bot {
        user: ops::user_summary(&v["user"]),
        bot_key: ops::s(&v["bot_key"]),
        rooms: ops::arr(&v["rooms"]).iter().map(|room| BotRoom { id: ops::i(&room["id"]), name: ops::s(&room["name"]) }).collect(),
    }
}

fn membership(v: &Value) -> ProfileMembership {
    ProfileMembership {
        room_id: ops::i(&v["room_id"]),
        room_param_key: ops::s(&v["room_param_key"]),
        room_display_name: ops::s(&v["room_display_name"]),
        involvement: ops::s(&v["involvement"]),
        direct: ops::b(&v["direct"]),
    }
}

fn memberships(list: &Value) -> Vec<ProfileMembership> {
    ops::arr(list).iter().map(membership).collect()
}

fn push_subscription(v: &Value) -> PushSubscription {
    PushSubscription {
        id: ops::i(&v["id"]),
        endpoint: ops::s(&v["endpoint"]),
        browser: ops::s(&v["browser"]),
        version: ops::s(&v["version"]),
        platform: ops::s(&v["platform"]),
    }
}

// ---- answers -----------------------------------------------------------------------------------------------------

/// A page: rendered twice (the same bytes), plain and recorded (the same bytes), and in the Turbo-Frame layout when
/// `frame` is set.
macro_rules! page_answer {
    ($ctx:expr, $args:expr, $page:expr) => {{
        let page = || $page;
        let render = || -> Result<(String, campfire_views::recorded::RecordedPage), String> {
            let plain = page().render().map_err(error)?;
            let recorded = if ops::b(&$args["frame"]) {
                layouts::frame($ctx, page().as_head(), page().as_content()).map_err(error)?
            } else {
                campfire_views::recorded::render(&page(), 0).map_err(error)?
            };
            Ok((plain, recorded))
        };
        let (first_plain, first) = render()?;
        let (plain, recorded) = render()?;
        if first.to_string() != recorded.to_string() || first_plain != plain {
            return Err("a second render is not the first".into());
        }
        if !ops::b(&$args["frame"]) && recorded.to_string() != plain {
            return Err("a recorded page is not the plain render".into());
        }
        let lengths: Vec<Value> = recorded.fragments().iter().map(|(offset, fragment)| json!([offset, fragment.len()])).collect();
        Ok(json!({ "out": recorded.to_string(), "text": recorded.text(), "fragments": lengths }))
    }};
}

/// A template that is not a page: rendered twice.
macro_rules! fragment_answer {
    ($page:expr) => {{
        let page = || $page;
        let first = page().render().map_err(error)?;
        let second = page().render().map_err(error)?;
        if first != second {
            return Err("a second render is not the first".into());
        }
        Ok(json!({ "out": second }))
    }};
}

/// `None` unless `op` is one of this file's.
pub fn run(op: &str, args: &Value, ctx: &Value, shared: &Value) -> Option<Out> {
    let known = [
        "accounts/edit", "accounts/users/_user", "accounts/users/_next_page_container", "accounts/users/index_turbo_stream",
        "accounts/bots/_bot", "accounts/bots/_form", "accounts/bots/index", "accounts/bots/new", "accounts/bots/edit",
        "accounts/custom_styles/edit", "first_runs/show", "sessions/new", "sessions/incompatible_browser", "sessions/transfers/show",
        "users/new", "users/show", "users/_ban_button", "users/profiles/show", "users/profiles/_membership", "users/profiles/_transfer",
        "users/push_subscriptions/index", "users/push_subscriptions/_push_subscription", "users/avatars/show", "pwa/manifest",
        "pwa/service_worker",
    ];
    known.contains(&op).then(|| ops::with_ctx(ctx, shared, |ctx| answer(op, args, ctx)))
}

fn answer(op: &str, args: &Value, ctx: &ViewContext) -> Out {
    match op {
        // args: edit: {account_id, join_code, restrict_room_creation_to_administrators, administrators: [user],
        //       members: [user], next_page}, frame
        "accounts/edit" => {
            let edit = &args["edit"];
            page_answer!(
                ctx,
                args,
                accounts::Edit {
                    ctx,
                    account_id: ops::i(&edit["account_id"]),
                    join_code: ops::s(&edit["join_code"]),
                    restrict_room_creation_to_administrators: ops::b(&edit["restrict_room_creation_to_administrators"]),
                    administrators: users(&edit["administrators"]),
                    members: users(&edit["members"]),
                    next_page: ops::opt(&edit["next_page"]),
                }
            )
        }
        // args: user
        "accounts/users/_user" => fragment_answer!(accounts::UserPartial { ctx, user: ops::user_summary(&args["user"]) }),
        // args: page
        "accounts/users/_next_page_container" => fragment_answer!(accounts::NextPageContainer { page: ops::s(&args["page"]) }),
        // args: users, next_page
        "accounts/users/index_turbo_stream" => {
            fragment_answer!(accounts::UsersIndexTurboStream { ctx, users: users(&args["users"]), next_page: ops::opt(&args["next_page"]) })
        }
        // args: bot: {user, bot_key, rooms: [{id, name}]}
        "accounts/bots/_bot" => {
            let bot = bot(&args["bot"]);
            fragment_answer!(BotPartial { ctx, bot: &bot })
        }
        // args: form: {url, model?, method?, class?}, bot: {name?, webhook_url?, avatar_attachment_url?}
        "accounts/bots/_form" => {
            let form = &args["form"];
            let mut builder = h::form_with(ops::s(&form["url"]));
            if let Some(model) = ops::opt(&form["model"]) {
                builder = builder.model(&model);
            }
            if let Some(class) = ops::opt(&form["class"]) {
                builder = builder.class(class);
            }
            let bot = bot_form(&args["bot"]);
            fragment_answer!(BotFormPartial { ctx, form: &builder, bot: &bot })
        }
        // args: bots: [bot], frame
        "accounts/bots/index" => {
            let bots: Vec<Bot> = ops::arr(&args["bots"]).iter().map(bot).collect();
            page_answer!(ctx, args, accounts::BotsIndex { ctx, bots: bots.clone() })
        }
        // args: bot: {name?, webhook_url?, avatar_attachment_url?}, frame
        "accounts/bots/new" => page_answer!(ctx, args, accounts::BotsNew { ctx, bot: bot_form(&args["bot"]) }),
        // args: bot_id, bot: {name?, webhook_url?, avatar_attachment_url?}, frame
        "accounts/bots/edit" => {
            page_answer!(ctx, args, accounts::BotsEdit { ctx, bot_id: ops::i(&args["bot_id"]), bot: bot_form(&args["bot"]) })
        }
        // args: custom_styles?, frame
        "accounts/custom_styles/edit" => {
            page_answer!(ctx, args, accounts::CustomStylesEdit { ctx, custom_styles: ops::opt(&args["custom_styles"]) })
        }
        // args: frame
        "first_runs/show" => page_answer!(ctx, args, first_runs::Show { ctx }),
        // args: email_address?, help_contact?, frame
        "sessions/new" => page_answer!(
            ctx,
            args,
            sessions::New { ctx, email_address: ops::opt(&args["email_address"]), help_contact: help_contact(&args["help_contact"]) }
        ),
        // args: frame
        "sessions/incompatible_browser" => page_answer!(ctx, args, sessions::IncompatibleBrowser { ctx }),
        // args: action, frame
        "sessions/transfers/show" => page_answer!(ctx, args, sessions::TransferShow { ctx, action: ops::s(&args["action"]) }),
        // args: join_code, help_contact?, frame
        "users/new" => page_answer!(
            ctx,
            args,
            users::New { ctx, join_code: ops::s(&args["join_code"]), help_contact: help_contact(&args["help_contact"]) }
        ),
        // args: user, transfer_id, frame
        "users/show" => {
            page_answer!(ctx, args, users::Show { ctx, user: ops::user_summary(&args["user"]), transfer_id: ops::s(&args["transfer_id"]) })
        }
        // args: user
        "users/_ban_button" => fragment_answer!(users::BanButton { ctx, user: ops::user_summary(&args["user"]) }),
        // args: profile: {user, avatar_attached, transfer_id, shared_memberships, direct_memberships}, frame
        "users/profiles/show" => {
            let profile = &args["profile"];
            page_answer!(
                ctx,
                args,
                users::ProfileShow {
                    ctx,
                    user: ops::user_summary(&profile["user"]),
                    avatar_attached: ops::b(&profile["avatar_attached"]),
                    transfer_id: ops::s(&profile["transfer_id"]),
                    shared_memberships: memberships(&profile["shared_memberships"]),
                    direct_memberships: memberships(&profile["direct_memberships"]),
                }
            )
        }
        // args: membership: {room_id, room_param_key, room_display_name, involvement, direct}
        "users/profiles/_membership" => {
            let membership = membership(&args["membership"]);
            fragment_answer!(MembershipPartial { ctx, membership: &membership })
        }
        // args: user, transfer_id
        "users/profiles/_transfer" => {
            fragment_answer!(users::Transfer { ctx, user: ops::user_summary(&args["user"]), transfer_id: ops::s(&args["transfer_id"]) })
        }
        // args: push_subscriptions: [{id, endpoint, browser, version, platform}], frame
        "users/push_subscriptions/index" => {
            let push_subscriptions: Vec<PushSubscription> = ops::arr(&args["push_subscriptions"]).iter().map(push_subscription).collect();
            page_answer!(ctx, args, users::PushSubscriptionsIndex { ctx, push_subscriptions: push_subscriptions.clone() })
        }
        // args: push_subscription
        "users/push_subscriptions/_push_subscription" => {
            let push_subscription = push_subscription(&args["push_subscription"]);
            fragment_answer!(PushSubscriptionPartial { ctx, push_subscription: &push_subscription })
        }
        // args: user_id, initials
        "users/avatars/show" => {
            fragment_answer!(users::AvatarSvg { user_id: ops::i(&args["user_id"]), initials: ops::s(&args["initials"]) })
        }
        // args: account_name?, logo_path_small, logo_path, base_url
        "pwa/manifest" => {
            let asset_path = |logical: &str| ops::asset(logical);
            fragment_answer!(pwa::Manifest {
                account_name: ops::opt(&args["account_name"]),
                logo_path_small: ops::s(&args["logo_path_small"]),
                logo_path: ops::s(&args["logo_path"]),
                base_url: ops::s(&args["base_url"]),
                asset_path: &asset_path,
            })
        }
        "pwa/service_worker" => Ok(json!({ "out": pwa::SERVICE_WORKER_JS })),
        _ => unreachable!(),
    }
}
