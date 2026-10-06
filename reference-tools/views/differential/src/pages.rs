//! The operations for the hot-path templates (unit 4.2): the room page, messages and their partials, the
//! sidebar, search, the room forms and the mentions prompt. `run` answers `None` for any other operation.
//!
//! Operations that take messages read them as the views crate's serde format (`MessageView`s) and take
//! `mode`: "view" gives every message to the template as a view (rendered into the fragment cache as it is
//! used), "mixed" renders every second one into the cache first and gives the template the cached fragment
//! (`MessageItem::Fragment`), as a presenter does when the cache already holds a message version. A page is
//! rendered cold and then again with the cache warm, and the two must be the same bytes.
//!
//! Page operations (`frame`: also render in the Turbo-Frame layout) answer `out` (the page as served, its
//! fragments written in), `text` (the recorded page's text, fragments left out) and `fragments` (each
//! recorded fragment's `[offset, length]`).

use std::sync::Arc;

use askama::Template;
use campfire_views::fragment_cache::{self, FragmentCache};
use campfire_views::messages::{self, EditView, MessageItem, MessageView, RoomKind, UserView};
use campfire_views::rooms::{self, ClosedFormView, DirectEditView, InvolvementView, OpenFormView, RefreshView, ShowView};
use campfire_views::searches::{self, IndexView};
use campfire_views::users::{self, MentionUser, SidebarDirectItem, SidebarRoom};
use campfire_views::*;
use serde_json::{Value, json};

use crate::ops::{self, Out};

// ---- templates this crate declares because rust/crates/views has no struct for them ----------------------

#[derive(Template)]
#[template(path = "messages/_actions.html")]
struct Actions<'a> {
    ctx: &'a ViewContext<'a>,
    message: &'a MessageView,
}

#[derive(Template)]
#[template(path = "messages/_template.html")]
struct MessageTemplate<'a> {
    ctx: &'a ViewContext<'a>,
    user: &'a UserView,
}

// ---- reading arguments -------------------------------------------------------------------------------------

fn parse<T: serde::de::DeserializeOwned>(args: &Value, key: &str) -> Result<T, String> {
    serde_json::from_value(args[key].clone()).map_err(|e| format!("{key}: {e}"))
}

fn mixed(ctx: &ViewContext, items: Vec<MessageItem>, mode: &Value) -> Vec<MessageItem> {
    if mode != "mixed" {
        return items;
    }
    items
        .into_iter()
        .enumerate()
        .map(|(n, item)| match item {
            MessageItem::View(view) if n % 2 == 1 => {
                let html = messages::message(ctx, &view);
                MessageItem::Fragment { client_message_id: view.client_message_id.clone(), room_id: view.room_id, html }
            }
            other => other,
        })
        .collect()
}

fn with_cache<R>(f: impl FnOnce() -> R) -> R {
    let cache: Arc<FragmentCache> = FragmentCache::new(fragment_cache::DEFAULT_MAX_BYTES);
    fragment_cache::with(&cache, f)
}

fn error(e: impl ToString) -> String {
    e.to_string()
}

/// The page answer: rendered cold, then warm (the same bytes), plain and recorded (the same bytes), and in the
/// Turbo-Frame layout when `frame` is set.
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
        let (cold_plain, cold) = render()?;
        let (plain, recorded) = render()?;
        if cold.to_string() != recorded.to_string() || cold_plain != plain {
            return Err("a warm render is not the cold one".into());
        }
        if !ops::b(&$args["frame"]) && recorded.to_string() != plain {
            return Err("a recorded page is not the plain render".into());
        }
        let lengths: Vec<Value> = recorded.fragments().iter().map(|(offset, fragment)| json!([offset, fragment.len()])).collect();
        Ok(json!({ "out": recorded.to_string(), "text": recorded.text(), "fragments": lengths }))
    }};
}

/// A template that is not a page: rendered cold and then warm.
macro_rules! fragment_answer {
    ($page:expr) => {{
        let page = || $page;
        let cold = page().render().map_err(error)?;
        let warm = page().render().map_err(error)?;
        if cold != warm {
            return Err("a warm render is not the cold one".into());
        }
        Ok(json!({ "out": warm }))
    }};
}

fn show_view(ctx: &ViewContext, args: &Value) -> Result<ShowView, String> {
    let mut show: ShowView = parse(args, "show")?;
    show.messages = mixed(ctx, std::mem::take(&mut show.messages), &args["mode"]);
    Ok(show)
}

fn items(ctx: &ViewContext, args: &Value, key: &str) -> Result<Vec<MessageItem>, String> {
    let views: Vec<MessageView> = parse(args, key)?;
    Ok(mixed(ctx, views.into_iter().map(MessageItem::from).collect(), &args["mode"]))
}

fn mention_users(args: &Value, key: &str) -> Vec<MentionUser> {
    ops::arr(&args[key]).iter().map(|u| MentionUser { user: ops::user_summary(&u["user"]), attachable_sgid: ops::s(&u["attachable_sgid"]) }).collect()
}

fn sidebar<'a>(ctx: &'a ViewContext<'a>, args: &Value) -> users::SidebarShow<'a> {
    let mixed = args["mode"] == "mixed";
    users::SidebarShow {
        ctx,
        current_user: ops::user_summary(&args["current_user"]),
        rooms_stream: ops::s(&args["rooms_stream"]),
        user_rooms_stream: ops::s(&args["user_rooms_stream"]),
        direct_memberships: ops::arr(&args["direct"])
            .iter()
            .enumerate()
            .map(|(n, v)| {
                let membership = ops::sidebar_direct(v);
                if mixed && n % 2 == 1 { SidebarDirectItem::Fragment(users::direct_room(ctx, &membership)) } else { membership.into() }
            })
            .collect(),
        direct_placeholder_users: ops::arr(&args["placeholders"]).iter().map(ops::user_summary).collect(),
        other_memberships: ops::arr(&args["shared"])
            .iter()
            .map(|r| SidebarRoom { id: ops::i(&r["id"]), param_key: ops::s(&r["param_key"]), name: ops::s(&r["name"]), unread: ops::b(&r["unread"]) })
            .collect(),
        can_create_rooms: ops::b(&args["can_create_rooms"]),
    }
}

/// `None` unless `op` is one of this file's.
pub fn run(op: &str, args: &Value, ctx: &Value, shared: &Value) -> Option<Out> {
    let known = [
        "messages/_message", "messages/message_cached", "messages/_actions", "messages/_presentation", "messages/_unrenderable",
        "messages/_template", "messages/index", "messages/show", "messages/edit", "messages/create_turbo_stream",
        "messages/destroy_turbo_stream", "messages/room_not_found", "messages/boosts/_boost", "messages/boosts/_boosts",
        "messages/boosts/index", "messages/boosts/new", "rooms/show", "rooms/involvements/show", "rooms/refreshes/show",
        "rooms/opens/new", "rooms/opens/edit", "rooms/closeds/new", "rooms/closeds/edit", "rooms/directs/new", "rooms/directs/edit",
        "rooms/layouts/_form", "searches/index", "autocompletable/users/index", "autocompletable/users/_prompt_item",
        "users/sidebars/show",
    ];
    known.contains(&op).then(|| ops::with_ctx(ctx, shared, |ctx| with_cache(|| answer(op, args, ctx))))
}

fn answer(op: &str, args: &Value, ctx: &ViewContext) -> Out {
    match op {
        // args: message (a MessageView). Rendered as the partial itself, not through the cache.
        "messages/_message" => {
            let message: MessageView = parse(args, "message")?;
            fragment_answer!(messages::MessagePartial { ctx, message: &message })
        }
        // args: message, second (the same message version with other content): the partial is cached, so the second render
        // is the first's; answers {out: the first, text: the second as `cached_message` writes it}
        "messages/message_cached" => {
            let (first, second): (MessageView, MessageView) = (parse(args, "message")?, parse(args, "second")?);
            let a = messages::message(ctx, &first);
            let b = messages::message(ctx, &second);
            if !Arc::ptr_eq(&a, &b) {
                return Err("the second render did not reuse the cached fragment".into());
            }
            let c = messages::cached_message(ctx, &second).0;
            let d = messages::cached_message_item(ctx, &MessageItem::from(second.clone())).0;
            if c != d {
                return Err("cached_message and cached_message_item differ".into());
            }
            Ok(json!({ "out": a.as_str(), "text": c.as_str() }))
        }
        "messages/_actions" => {
            let message: MessageView = parse(args, "message")?;
            fragment_answer!(Actions { ctx, message: &message })
        }
        "messages/_presentation" => {
            let message: MessageView = parse(args, "message")?;
            fragment_answer!(messages::PresentationPartial { ctx, message: &message })
        }
        "messages/_unrenderable" => fragment_answer!(messages::Unrenderable),
        // args: user (a UserView)
        "messages/_template" => {
            let user: UserView = parse(args, "user")?;
            fragment_answer!(MessageTemplate { ctx, user: &user })
        }
        // args: messages (MessageViews), mode
        "messages/index" => {
            let messages = items(ctx, args, "messages")?;
            let page = messages::Index { ctx, messages: &messages };
            let recorded = campfire_views::recorded::render(&page, 0).map_err(error)?;
            let plain = page.render().map_err(error)?;
            if recorded.to_string() != plain {
                return Err("a recorded page is not the plain render".into());
            }
            let lengths: Vec<Value> = recorded.fragments().iter().map(|(offset, fragment)| json!([offset, fragment.len()])).collect();
            Ok(json!({ "out": plain, "text": recorded.text(), "fragments": lengths }))
        }
        "messages/show" => {
            let message: MessageView = parse(args, "message")?;
            fragment_answer!(messages::Show { ctx, message: &message })
        }
        // args: edit (an EditView)
        "messages/edit" => {
            let edit: EditView = parse(args, "edit")?;
            fragment_answer!(messages::Edit { ctx, edit: &edit })
        }
        // args: message (a MessageView), room_kind, fragment (bool): the message as the cached fragment
        "messages/create_turbo_stream" => {
            let message: MessageView = parse(args, "message")?;
            let room_kind: RoomKind = parse(args, "room_kind")?;
            let item = if ops::b(&args["fragment"]) {
                let html = messages::message(ctx, &message);
                MessageItem::Fragment { client_message_id: message.client_message_id.clone(), room_id: message.room_id, html }
            } else {
                MessageItem::from(message)
            };
            fragment_answer!(messages::CreateStream { ctx, message: &item, room_kind })
        }
        // args: message (a MessageView)
        "messages/destroy_turbo_stream" => {
            let message: MessageView = parse(args, "message")?;
            fragment_answer!(messages::DestroyStream { message: &message })
        }
        "messages/room_not_found" => fragment_answer!(messages::RoomNotFound),
        // args: boost (a BoostView), rendered into the cache
        "messages/boosts/_boost" => {
            let boost: messages::BoostView = parse(args, "boost")?;
            let first = messages::boost(ctx, &boost);
            let second = messages::boost(ctx, &boost);
            if !Arc::ptr_eq(&first, &second) {
                return Err("the second render did not reuse the cached fragment".into());
            }
            let partial = messages::BoostPartial { ctx, boost: &boost }.render().map_err(error)?;
            if partial != *first {
                return Err("the cached boost is not the partial".into());
            }
            Ok(json!({ "out": partial, "text": messages::cached_boost(ctx, &boost).0.as_str() }))
        }
        "messages/boosts/_boosts" => {
            let message: MessageView = parse(args, "message")?;
            fragment_answer!(messages::BoostsPartial { ctx, message: &message })
        }
        "messages/boosts/index" => {
            let message: MessageView = parse(args, "message")?;
            fragment_answer!(messages::BoostsIndex { ctx, message: &message })
        }
        // args: message, user
        "messages/boosts/new" => {
            let message: MessageView = parse(args, "message")?;
            let user: UserView = parse(args, "user")?;
            fragment_answer!(messages::NewBoost { ctx, message: &message, user: &user })
        }
        // args: show (a ShowView), mode, frame
        "rooms/show" => {
            let show = show_view(ctx, args)?;
            page_answer!(ctx, args, rooms::Show { ctx, show: &show })
        }
        // args: involvement (an InvolvementView)
        "rooms/involvements/show" => {
            let involvement: InvolvementView = parse(args, "involvement")?;
            fragment_answer!(rooms::InvolvementShow { ctx, involvement: &involvement })
        }
        // args: refresh (a RefreshView), mode
        "rooms/refreshes/show" => {
            let mut refresh: RefreshView = parse(args, "refresh")?;
            refresh.new_messages = mixed(ctx, std::mem::take(&mut refresh.new_messages), &args["mode"]);
            refresh.updated_messages = mixed(ctx, std::mem::take(&mut refresh.updated_messages), &args["mode"]);
            let page = rooms::RefreshShow { ctx, refresh: &refresh };
            let recorded = campfire_views::recorded::render(&page, 0).map_err(error)?;
            let plain = page.render().map_err(error)?;
            if recorded.to_string() != plain {
                return Err("a recorded page is not the plain render".into());
            }
            let lengths: Vec<Value> = recorded.fragments().iter().map(|(offset, fragment)| json!([offset, fragment.len()])).collect();
            Ok(json!({ "out": plain, "text": recorded.text(), "fragments": lengths }))
        }
        // args: form (an OpenFormView), frame
        "rooms/opens/new" => {
            let form: OpenFormView = parse(args, "form")?;
            page_answer!(ctx, args, rooms::OpensNew { ctx, form: &form })
        }
        "rooms/opens/edit" => {
            let form: OpenFormView = parse(args, "form")?;
            page_answer!(ctx, args, rooms::OpensEdit { ctx, form: &form })
        }
        // args: form (a ClosedFormView), frame
        "rooms/closeds/new" => {
            let form: ClosedFormView = parse(args, "form")?;
            page_answer!(ctx, args, rooms::ClosedsNew { ctx, form: &form })
        }
        "rooms/closeds/edit" => {
            let form: ClosedFormView = parse(args, "form")?;
            page_answer!(ctx, args, rooms::ClosedsEdit { ctx, form: &form })
        }
        "rooms/directs/new" => page_answer!(ctx, args, rooms::DirectsNew { ctx }),
        // args: edit (a DirectEditView), frame
        "rooms/directs/edit" => {
            let edit: DirectEditView = parse(args, "edit")?;
            page_answer!(ctx, args, rooms::DirectsEdit { ctx, edit: &edit })
        }
        // args: room: {id, name}, can_administer, kind ("open" | "closed"), content (html)
        "rooms/layouts/_form" => {
            let room: rooms::FormRoom = parse(args, "room")?;
            let kind: RoomKind = parse(args, "kind")?;
            fragment_answer!(rooms::FormLayout {
                ctx,
                room: &room,
                can_administer: ops::b(&args["can_administer"]),
                kind,
                content: ops::s(&args["content"])
            })
        }
        // args: index (an IndexView), mode, frame
        "searches/index" => {
            let mut index: IndexView = parse(args, "index")?;
            index.messages = mixed(ctx, std::mem::take(&mut index.messages), &args["mode"]);
            page_answer!(ctx, args, searches::Index { ctx, index: &index })
        }
        // args: users: [{user, attachable_sgid}]
        "autocompletable/users/index" => {
            let users = mention_users(args, "users");
            fragment_answer!(autocompletable::UsersIndex { ctx, users: users.clone() })
        }
        // args: user: {user, attachable_sgid}
        "autocompletable/users/_prompt_item" => {
            let user = mention_users(&json!({ "users": [args["user"].clone()] }), "users").remove(0);
            fragment_answer!(autocompletable::PromptItem { ctx, user: user.clone() })
        }
        // args: current_user, rooms_stream, user_rooms_stream, direct: [membership], placeholders: [user],
        //       shared: [{id, param_key, name, unread}], can_create_rooms, mode, frame
        "users/sidebars/show" => {
            page_answer!(ctx, args, sidebar(ctx, args))
        }
        _ => unreachable!(),
    }
}
