//! The operations of `main.rs`. Each branch of `run` says what its `args` are.

use std::collections::BTreeMap;
use std::fmt;
use std::sync::Arc;
use std::time::Instant;

use askama::Template;
use campfire_views::fragment_cache::{self, Fragment, FragmentCache};
#[allow(unused_imports)]
use campfire_views::helpers::{self as h, filters};
use campfire_views::messages::{self, MessageView};
use campfire_views::recorded;
use campfire_views::users::{MentionUser, Role, Status, UserSummary};
use campfire_views::*;
use serde_json::{Value, json};

type Out = Result<Value, String>;

// ---- templates this crate declares because rust/crates/views has no struct for them -------------

#[derive(Template)]
#[template(path = "layouts/_lightbox.html")]
struct Lightbox<'a> {
    ctx: &'a ViewContext<'a>,
}

/// What `recorded.rs`'s own tests render: a list of handed fragments, then a plain one.
struct Handed(Fragment);

impl fmt::Display for Handed {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        recorded::hand(&self.0);
        f.write_str(&self.0)
    }
}

#[derive(Template)]
#[template(source = "<ul>{% for item in items %}\n  {{ item|safe }}{% endfor %}\n</ul>{{ unhanded|safe }}", ext = "html")]
struct List<'a> {
    items: &'a [Handed],
    unhanded: &'a str,
}

// ---- reading arguments ------------------------------------------------------------------------------------

fn s(v: &Value) -> String {
    v.as_str().unwrap_or_default().to_string()
}

fn opt(v: &Value) -> Option<String> {
    v.as_str().map(str::to_string)
}

fn arr(v: &Value) -> Vec<Value> {
    v.as_array().cloned().unwrap_or_default()
}

fn i(v: &Value) -> i64 {
    v.as_i64().unwrap_or_default()
}

fn b(v: &Value) -> bool {
    v.as_bool().unwrap_or(false)
}

fn html(text: &str) -> h::Html {
    h::raw(text.to_string())
}

fn timestamp(v: &Value) -> jiff::Timestamp {
    v.as_str().unwrap().parse().unwrap()
}

/// The shared value of `key` unless the case's `ctx` has its own.
fn pick(ctx: &Value, shared: &Value, key: &str) -> String {
    ctx.get(key).or_else(|| shared.get(key)).and_then(Value::as_str).unwrap_or_default().to_string()
}

/// Every logical asset path resolves to a deterministic fake digest: `x.svg` is `/assets/x-d1g3st.svg`.
pub fn asset(logical: &str) -> String {
    match logical.find('.') {
        Some(at) => format!("/assets/{}-d1g3st{}", &logical[..at], &logical[at..]),
        None => format!("/assets/{logical}-d1g3st"),
    }
}

fn platform(v: &Value) -> Platform {
    let flag = |name: &str| b(&v[name]);
    Platform {
        ios: flag("ios"),
        android: flag("android"),
        mac: flag("mac"),
        windows: flag("windows"),
        chrome: flag("chrome"),
        firefox: flag("firefox"),
        safari: flag("safari"),
        edge: flag("edge"),
        mobile: flag("mobile"),
        desktop: flag("desktop"),
        apple_messages: flag("apple_messages"),
        browser: s(&v["browser"]),
        operating_system: s(&v["operating_system"]),
    }
}

/// Runs `f` with the `ViewContext` described by `ctx`.
fn with_ctx<R>(ctx: &Value, shared: &Value, f: impl FnOnce(&ViewContext) -> R) -> R {
    let asset_path = |logical: &str| asset(logical);
    let importmap = pick(ctx, shared, "importmap_tags");
    let stylesheet = pick(ctx, shared, "stylesheet_tags");
    let current_user = ctx["current_user"].as_object().map(|user| CurrentUser {
        id: i(&user["id"]),
        name: s(&user["name"]),
        administrator: b(&user["administrator"]),
        bot: b(&user["bot"]),
        avatar_url: s(&user["avatar_url"]),
    });
    let view = ViewContext {
        current_user,
        account: AccountSummary {
            name: s(&ctx["account"]["name"]),
            logo_url: s(&ctx["account"]["logo_url"]),
            has_logo: b(&ctx["account"]["has_logo"]),
        },
        flash_notice: opt(&ctx["flash_notice"]),
        flash_alert: opt(&ctx["flash_alert"]),
        platform: platform(&ctx["platform"]),
        vapid_public_key: opt(&ctx["vapid_public_key"]),
        asset_path: &asset_path,
        importmap_tags: &importmap,
        stylesheet_tags: &stylesheet,
        custom_styles: opt(&ctx["custom_styles"]),
        cable_url: s(&ctx["cable_url"]),
        base_url: s(&ctx["base_url"]),
        request_url: s(&ctx["request_url"]),
        referrer: opt(&ctx["referrer"]),
        last_room_visited_id: ctx["last_room_visited_id"].as_i64(),
        app_version: s(&ctx["app_version"]),
    };
    f(&view)
}

/// `[[name, kind, value], ...]`: kind is "text", "safe", "bool", "int" or "none".
fn attrs_from(list: &Value) -> h::Attrs {
    let mut attrs = h::attrs();
    for item in arr(list) {
        let name = s(&item[0]);
        let value = &item[2];
        attrs = match item[1].as_str().unwrap() {
            "text" => attrs.attr(name, s(value)),
            "safe" => attrs.attr(name, html(&s(value))),
            "bool" => attrs.attr(name, b(value)),
            "int" => attrs.attr(name, i(value)),
            "none" => attrs.attr_opt::<String>(name, None),
            other => panic!("attribute kind {other}"),
        };
    }
    attrs
}

fn user_summary(v: &Value) -> UserSummary {
    UserSummary {
        id: i(&v["id"]),
        name: s(&v["name"]),
        bio: opt(&v["bio"]),
        email_address: opt(&v["email_address"]),
        role: match v["role"].as_str().unwrap_or("member") {
            "administrator" => Role::Administrator,
            "bot" => Role::Bot,
            _ => Role::Member,
        },
        status: match v["status"].as_str().unwrap_or("active") {
            "deactivated" => Status::Deactivated,
            "banned" => Status::Banned,
            _ => Status::Active,
        },
        avatar_path: s(&v["avatar_path"]),
    }
}

fn help_contact(v: &Value) -> Option<accounts::HelpContact> {
    v.is_object().then(|| accounts::HelpContact { name: s(&v["name"]), email_address: s(&v["email_address"]) })
}

fn text(out: String) -> Out {
    Ok(json!({ "out": out }))
}

fn fragment_text(text: &str, n: usize, size: usize) -> Fragment {
    let _ = text;
    Arc::new(format!("<li id=\"{n}\">{}</li>", "x".repeat(size)))
}

// ---- the operations ---------------------------------------------------------------------------------

pub fn run(case: &Value, shared: &Value) -> Out {
    let op = case["op"].as_str().ok_or("no op")?;
    let args = &case["args"];
    let ctx = &case["ctx"];
    match op {
        // ---- layouts -----------------------------------------------------------------------------
        // args: page_title?, body_class?, head, nav, content, footer, sidebar (html strings)
        "layouts/application_wrapper" => with_ctx(ctx, shared, |ctx| {
            let part = |key: &str| html(&s(&args[key]));
            let layout = layouts::Application {
                ctx,
                page_title: opt(&args["page_title"]),
                body_class: opt(&args["body_class"]),
                head: part("head"),
                nav: part("nav"),
                content: part("content"),
                footer: part("footer"),
                sidebar: part("sidebar"),
            };
            text(layout.render().map_err(|e| e.to_string())?)
        }),
        "layouts/_lightbox" => with_ctx(ctx, shared, |ctx| text(Lightbox { ctx }.render().map_err(|e| e.to_string())?)),
        // args: head, content (html strings, the content as an unrecorded page)
        "layouts/turbo_rails/frame" => with_ctx(ctx, shared, |ctx| {
            let layout = layouts::FrameLayout {
                ctx,
                head: html(&s(&args["head"])),
                content: h::raw(recorded::RecordedPage::from(s(&args["content"]))),
            };
            text(layout.render().map_err(|e| e.to_string())?)
        }),
        // args: sizes (fragment lengths), unhanded (length of a fragment written without being handed),
        //       frame (bool: write the recorded page into the Turbo-Frame layout)
        "recorded/page" => with_ctx(ctx, shared, |ctx| {
            let items: Vec<Handed> =
                arr(&args["sizes"]).iter().enumerate().map(|(n, size)| Handed(fragment_text("", n, i(size) as usize))).collect();
            let unhanded = fragment_text("", 99, i(&args["unhanded"]) as usize);
            let list = List { items: &items, unhanded: &unhanded };
            let page = recorded::render(&list, 0).map_err(|e| e.to_string())?;
            let page = if b(&args["frame"]) {
                let layout = layouts::FrameLayout { ctx, head: h::empty(), content: h::raw(page) };
                recorded::render(&layout, 0).map_err(|e| e.to_string())?
            } else {
                page
            };
            let plain = list.render().map_err(|e| e.to_string())?;
            let whole = page.to_string();
            if !b(&args["frame"]) && whole != plain {
                return Err("a recorded page is not the plain render".into());
            }
            let lengths: Vec<Value> = page.fragments().iter().map(|(offset, fragment)| json!([offset, fragment.len()])).collect();
            Ok(json!({ "out": whole, "text": page.text(), "fragments": lengths }))
        }),

        // ---- shared partials ------------------------------------------------------------------------
        // args: help_contact: {name, email_address} | null
        "accounts/_help_contact" => with_ctx(ctx, shared, |ctx| {
            text(accounts::HelpContactPartial { ctx, help_contact: help_contact(&args["help_contact"]) }.render().map_err(|e| e.to_string())?)
        }),
        // args: join_code
        "accounts/_invite" => with_ctx(ctx, shared, |ctx| {
            text(accounts::Invite { ctx, join_code: s(&args["join_code"]) }.render().map_err(|e| e.to_string())?)
        }),
        "pwa/_install_instructions" => with_ctx(ctx, shared, |ctx| text(pwa::InstallInstructions { ctx }.render().map_err(|e| e.to_string())?)),
        "pwa/_browser_settings" => with_ctx(ctx, shared, |ctx| text(pwa::BrowserSettings { ctx }.render().map_err(|e| e.to_string())?)),
        "pwa/_system_settings" => with_ctx(ctx, shared, |ctx| text(pwa::SystemSettings { ctx }.render().map_err(|e| e.to_string())?)),
        // args: user: {id, name, bio, email_address, role, status, avatar_path}, attachable_sgid
        "users/_mention" => with_ctx(ctx, shared, |ctx| {
            let user = MentionUser { user: user_summary(&args["user"]), attachable_sgid: s(&args["attachable_sgid"]) };
            text(users::Mention { ctx, user }.render().map_err(|e| e.to_string())?)
        }),
        "users/autocompletables/_template" => {
            with_ctx(ctx, shared, |ctx| text(users::AutocompletableTemplate { ctx }.render().map_err(|e| e.to_string())?))
        }
        // args: current_user_name
        "welcome/show" => with_ctx(ctx, shared, |ctx| {
            text(welcome::Show { ctx, current_user_name: s(&args["current_user_name"]) }.render().map_err(|e| e.to_string())?)
        }),

        // ---- helpers -----------------------------------------------------------------------------------
        _ if op.starts_with("helpers/") => helper(&op["helpers/".len()..], args, ctx, shared),
        _ if op.starts_with("fragment_cache/") => cache_op(&op["fragment_cache/".len()..], args),
        _ if op.starts_with("messages/") => message_op(&op["messages/".len()..], args, ctx, shared),
        "autocompletable/users_index_json" => {
            let users: Vec<MentionUser> = arr(&args["users"])
                .iter()
                .map(|u| MentionUser { user: user_summary(&u["user"]), attachable_sgid: s(&u["attachable_sgid"]) })
                .collect();
            text(autocompletable::users_index_json(&users, &s(&args["base_url"])))
        }
        "searches/search_path" => text(searches::search_path(&s(&args["query"]))),
        // args: name?, direct, others: [names], for_user?
        "rooms/room_display_name" => {
            let others: Vec<String> = arr(&args["others"]).iter().map(s).collect();
            text(rooms::room_display_name(opt(&args["name"]).as_deref(), b(&args["direct"]), &others, opt(&args["for_user"]).as_deref()))
        }
        // args: room_id, display_name
        "rooms/button_to_delete_room" => {
            with_ctx(ctx, shared, |ctx| text(rooms::button_to_delete_room(ctx, i(&args["room_id"]), &s(&args["display_name"])).0))
        }
        // args: mention_prompt_src: room_id
        "rooms/mention_prompt_src" => text(rooms::mention_prompt_src(i(&args["room_id"]))),
        other => Err(format!("unknown op {other}")),
    }
}

fn helper(name: &str, args: &Value, ctx: &Value, shared: &Value) -> Out {
    let with = |f: &dyn Fn(&ViewContext) -> String| with_ctx(ctx, shared, |ctx| text(f(ctx)));
    match name {
        // args: kind (content_tag | content_tag_text | content_tag_block | builder_tag | legacy_tag), name, attrs, content
        "tag" => {
            let attrs = attrs_from(&args["attrs"]);
            let (tag, content) = (s(&args["name"]), s(&args["content"]));
            text(match args["kind"].as_str().unwrap() {
                "content_tag" => h::content_tag(&tag, attrs, &content).0,
                "content_tag_text" => h::content_tag_text(&tag, attrs, &content).0,
                "content_tag_block" => h::tag::content_tag_block(&tag, attrs, content).map_err(|e| e.to_string())?.0,
                "builder_tag" => h::builder_tag(&tag, attrs).0,
                "legacy_tag" => h::legacy_tag(&tag, attrs).0,
                other => return Err(format!("tag kind {other}")),
            })
        }
        // args: source, attrs
        "image_tag" => with(&|ctx| h::image_tag(ctx, s(&args["source"]), attrs_from(&args["attrs"])).0),
        // args: source
        "asset_path" => with(&|ctx| h::asset_path(ctx, &s(&args["source"]))),
        // args: kind (link_to | link_to_text | link_to_if | mail_to), url, text, condition, attrs, content
        "link" => {
            let (url, label, content) = (s(&args["url"]), s(&args["text"]), s(&args["content"]));
            let attrs = attrs_from(&args["attrs"]);
            text(match args["kind"].as_str().unwrap() {
                "link_to" => h::link_to(&url, attrs, &content).0,
                "link_to_text" => h::link_to_text(&label, &url, attrs).0,
                "link_to_if" => h::link_to_if(b(&args["condition"]), &label, &url, attrs).0,
                "mail_to" => h::mail_to(&label).0,
                other => return Err(format!("link kind {other}")),
            })
        }
        // args: url, model?, method?, id?, class?, data: [[key, value]], auto_submit, multipart, open (bool: only the open tag),
        //       fields: [{type, method, value?, attrs, checked_value?, unchecked_value?, current?, fields_for?}]
        "form" => {
            let mut form = h::form_with(s(&args["url"]));
            if let Some(model) = opt(&args["model"]) {
                form = form.model(&model);
            }
            if let Some(method) = opt(&args["method"]) {
                form = form.method(&method);
            }
            if let Some(id) = opt(&args["id"]) {
                form = form.id(id);
            }
            if let Some(class) = opt(&args["class"]) {
                form = form.class(class);
            }
            for pair in arr(&args["data"]) {
                form = form.data(&s(&pair[0]), s(&pair[1]));
            }
            if b(&args["auto_submit"]) {
                form = form.auto_submit();
            }
            if b(&args["multipart"]) {
                form = form.multipart();
            }
            if b(&args["open"]) {
                return text(form.open().0);
            }
            let mut fields = String::new();
            for field in arr(&args["fields"]) {
                let scoped = opt(&field["fields_for"]).map(|name| form.fields_for(&name));
                let form = scoped.as_ref().unwrap_or(&form);
                let (method, value) = (s(&field["method"]), opt(&field["value"]));
                let attrs = attrs_from(&field["attrs"]);
                fields.push_str(&match field["type"].as_str().unwrap() {
                    "text" => form.text_field(&method, value.as_deref(), attrs).0,
                    "email" => form.email_field(&method, value.as_deref(), attrs).0,
                    "url" => form.url_field(&method, value.as_deref(), attrs).0,
                    "password" => form.password_field(&method, attrs).0,
                    "hidden" => form.hidden_field(&method, value.as_deref(), attrs).0,
                    "file" => form.file_field(&method, attrs).0,
                    "text_area" => form.text_area(&method, value.as_deref(), attrs).0,
                    "check_box" => {
                        form.check_box(&method, attrs, &s(&field["checked_value"]), &s(&field["unchecked_value"]), &s(&field["current"])).0
                    }
                    other => return Err(format!("field type {other}")),
                });
            }
            text(form.wrap(&fields).0)
        }
        // args: kind (button_to | button_to_block | button_tag | hidden_field_tag | method_tag), url, name, value?, attrs, content
        "button" => {
            let attrs = attrs_from(&args["attrs"]);
            let (url, content) = (s(&args["url"]), s(&args["content"]));
            text(match args["kind"].as_str().unwrap() {
                "button_to" => h::button_to(&url, attrs, &content).0,
                "button_to_block" => h::forms::button_to_block(&url, &attrs, content).map_err(|e| e.to_string())?.0,
                "button_tag" => h::button_tag(attrs, &content).0,
                "hidden_field_tag" => h::hidden_field_tag(&s(&args["name"]), opt(&args["value"]).as_deref(), attrs).0,
                "method_tag" => h::method_tag(&s(&args["name"])).0,
                other => return Err(format!("button kind {other}")),
            })
        }
        // args: name (the helper), plus its own arguments
        "application" => {
            let content = s(&args["content"]);
            match args["name"].as_str().unwrap() {
                "page_title_tag" => text(h::page_title_tag(opt(&args["page_title"]).as_deref()).0),
                "current_user_meta_tags" => with(&|ctx| h::current_user_meta_tags(ctx).0),
                "script_aware_action_cable_meta_tag" => with(&|ctx| h::script_aware_action_cable_meta_tag(ctx).0),
                "custom_styles_tag" => with(&|ctx| h::custom_styles_tag(ctx).0),
                "body_classes" => with(&|ctx| h::body_classes(ctx, opt(&args["body_class"]).as_deref())),
                "link_back" => with(&|ctx| h::link_back(ctx).0),
                "link_back_to" => with(&|ctx| h::link_back_to(ctx, s(&args["destination"])).0),
                "link_back_to_last_room_visited" => with(&|ctx| h::link_back_to_last_room_visited(ctx).0),
                "version_badge" => with(&|ctx| h::version_badge(ctx).0),
                "button_to_copy_to_clipboard" => text(h::button_to_copy_to_clipboard(&s(&args["url"]), &content).0),
                "link_to_zoom_qr_code" => text(h::link_to_zoom_qr_code(&s(&args["url"]), &content).0),
                "web_share_session_button" => {
                    text(h::web_share_session_button(&s(&args["url"]), &s(&args["title"]), &s(&args["text"]), &content).0)
                }
                "truncate" => text(h::truncate(&s(&args["text"]), i(&args["length"]) as usize, &s(&args["omission"]))),
                "capitalize" => text(h::capitalize(&s(&args["text"]))),
                // capitalize each character of text on its own
                "capitalize_each" => text(s(&args["text"]).chars().map(|c| h::capitalize(&c.to_string())).collect::<String>()),
                "to_sentence" => {
                    let items: Vec<String> = arr(&args["items"]).iter().map(s).collect();
                    text(h::to_sentence(&items, &s(&args["connector"])))
                }
                other => Err(format!("application helper {other}")),
            }
        }
        // args: name (the helper), plus its own arguments
        "users" => {
            let avatar = h::AvatarUser { id: i(&args["id"]), title: s(&args["title"]), avatar_path: s(&args["avatar_path"]) };
            match args["name"].as_str().unwrap() {
                "avatar_tag" => with(&|ctx| h::avatar_tag(ctx, &avatar, attrs_from(&args["attrs"])).0),
                "avatar_background_color" => text(h::avatar_background_color(i(&args["id"])).to_string()),
                "initials" => text(h::initials(&s(&args["text"]))),
                // initials("<c>x") for each character c of text, one per line
                "initials_each" => text(s(&args["text"]).chars().map(|c| h::initials(&format!("{c}x"))).collect::<Vec<_>>().join("\n")),
                "user_title" => text(h::user_title(&s(&args["text"]), opt(&args["bio"]).as_deref())),
                "button_to_direct_room_with" => with(&|ctx| h::button_to_direct_room_with(ctx, i(&args["id"])).0),
                "curl_text_line" => text(h::curl_text_line(s(&args["text"]))),
                "curl_upload_line" => text(h::curl_upload_line(s(&args["text"]))),
                "account_logo_tag" => with(&|ctx| h::account_logo_tag(ctx, opt(&args["style"]).as_deref()).0),
                "profile_form_submit_button" => with(&|ctx| h::profile_form_submit_button(ctx).0),
                "sidebar_turbo_frame_tag" => text(h::sidebar_turbo_frame_tag(opt(&args["src"]).as_deref(), &s(&args["content"])).0),
                "user_filter_menu_tag" => text(h::user_filter_menu_tag(&s(&args["content"])).0),
                "user_filter_search_tag" => text(h::user_filter_search_tag().0),
                other => Err(format!("users helper {other}")),
            }
        }
        // args: name (the helper), plus its own arguments
        "rooms" => match args["name"].as_str().unwrap() {
            "link_to_room" => {
                let out = h::rooms::link_to_room(i(&args["room_id"]), &attrs_from(&args["attrs"]), s(&args["content"])).map_err(|e| e.to_string())?;
                text(out.0)
            }
            "humanize_involvement" => text(h::humanize_involvement(&s(&args["involvement"])).to_string()),
            "next_involvement" => text(h::next_involvement(b(&args["direct"]), &s(&args["involvement"])).to_string()),
            "button_to_change_involvement" => with(&|ctx| {
                let param_key = s(&args["param_key"]);
                let room = h::InvolvementRoom { id: i(&args["room_id"]), param_key: &param_key, direct: b(&args["direct"]) };
                h::button_to_change_involvement(ctx, &room, &s(&args["involvement"])).0
            }),
            other => Err(format!("rooms helper {other}")),
        },
        // args: kind (translations_for | translation_button), key
        "translations" => match args["kind"].as_str().unwrap() {
            "translations_for" => text(h::translations_for(&s(&args["key"])).0),
            "translation_button" => with(&|ctx| h::translation_button(ctx, &s(&args["key"])).0),
            other => Err(format!("translations helper {other}")),
        },
        // args: kind (with_query | directs), path, params: [[key, value | [values]]], user_ids
        "url" => match args["kind"].as_str().unwrap() {
            "with_query" => {
                let params: Vec<(String, h::Param)> = arr(&args["params"])
                    .iter()
                    .map(|pair| {
                        let value = match &pair[1] {
                            Value::Array(values) => h::Param::Many(values.iter().map(s).collect()),
                            value => h::Param::One(s(value)),
                        };
                        (s(&pair[0]), value)
                    })
                    .collect();
                let borrowed: Vec<(&str, h::Param)> = params.iter().map(|(k, v)| (k.as_str(), clone_param(v))).collect();
                text(h::with_query(&s(&args["path"]), borrowed))
            }
            "directs" => text(h::rooms_directs_with_users(&arr(&args["user_ids"]).iter().map(i).collect::<Vec<_>>())),
            "cgi_escape" => text(h::cgi_escape(&s(&args["text"]))),
            other => Err(format!("url helper {other}")),
        },
        // args: kind (turbo_stream_from | page_requires_reload | dom_id | frame_options), ...
        "turbo" => match args["kind"].as_str().unwrap() {
            "turbo_stream_from" => text(h::turbo_stream_from(&s(&args["name"])).0),
            "page_requires_reload" => text(h::turbo_page_requires_reload_tag().0),
            "dom_id" => text(h::dom_id(&s(&args["model"]), i(&args["id"]), opt(&args["prefix"]).as_deref())),
            "turbo_frame_tag" => {
                let attrs = attrs_from(&args["attrs"]);
                let mut options = attrs.view();
                let src = options.remove("src").map(h::tag::AttrValue::as_str);
                let target = options.remove("target").map(h::tag::AttrValue::as_str);
                let id = s(&args["id"]);
                let out = h::tag::content_tag_block("turbo-frame", h::turbo_frame_options(&id, src, target, options), s(&args["content"]))
                    .map_err(|e| e.to_string())?;
                text(out.0)
            }
            other => Err(format!("turbo helper {other}")),
        },
        other => Err(format!("unknown helper {other}")),
    }
}

fn clone_param(param: &h::Param) -> h::Param {
    match param {
        h::Param::One(value) => h::Param::One(value.clone()),
        h::Param::Many(values) => h::Param::Many(values.clone()),
    }
}

/// args: kind, plus
///   cache_version / cache_key_with_version / record_fragment_key: at (RFC 3339), id, table, template, digest
///   script: max_bytes, steps: [["fetch", key, size] | ["get", key]]; the answer lists each step's effect
fn cache_op(name: &str, args: &Value) -> Out {
    match name {
        "keys" => {
            let at = timestamp(&args["at"]);
            let id = i(&args["id"]);
            let table = s(&args["table"]);
            let mut record = String::from("existing/");
            fragment_cache::push_record_fragment_key(&mut record, &s(&args["template"]), &s(&args["digest"]), &table, id, at);
            text(format!("{}\n{}\n{}", fragment_cache::cache_version(at), fragment_cache::cache_key_with_version(&table, id, at), record))
        }
        "script" => {
            let cache = FragmentCache::new(i(&args["max_bytes"]) as usize);
            let mut lines = Vec::new();
            let mut keys: Vec<String> = Vec::new();
            for step in arr(&args["steps"]) {
                let key = s(&step[1]);
                if !keys.contains(&key) {
                    keys.push(key.clone());
                }
                match step[0].as_str().unwrap() {
                    "fetch" => {
                        let mut rendered = false;
                        let size = i(&step[2]) as usize;
                        let fragment = cache.fetch(&key, || {
                            rendered = true;
                            "x".repeat(size)
                        });
                        lines.push(format!("fetch {key} rendered={rendered} len={} bytes={} count={}", fragment.len(), cache.bytes(), cache.len()));
                    }
                    _ => {
                        let found = cache.get::<Fragment>(&key);
                        lines.push(format!("get {key} found={} bytes={} count={}", found.is_some(), cache.bytes(), cache.len()));
                    }
                }
            }
            let held: Vec<&String> = keys.iter().filter(|key| cache.get::<Fragment>(key).is_some()).collect();
            lines.push(format!("held {held:?} bytes={}", cache.bytes()));
            text(lines.join("\n"))
        }
        other => Err(format!("unknown cache op {other}")),
    }
}

/// args: kind, plus
///   epoch_ms / iso8601 / json_time: at (RFC 3339)
///   ruby_number: int, or float_bits (hex of the f64's bits), and half (bool)
///   presentation: message (a MessageView as the views crate deserializes it)
///   json_by_bots_index / json_by_bots_show / json_boosts_by_bots_show: the Jbuilder input
fn message_op(name: &str, args: &Value, ctx: &Value, shared: &Value) -> Out {
    use messages::support::{RubyNumber, epoch_ms, iso8601, json_time};
    match name {
        "epoch_ms" => text(epoch_ms(timestamp(&args["at"])).to_string()),
        "iso8601" => text(iso8601(timestamp(&args["at"]))),
        "json_time" => text(json_time(timestamp(&args["at"]))),
        "ruby_number" => {
            let number = match args["int"].as_i64() {
                Some(n) => RubyNumber::Int(n),
                None => RubyNumber::Float(f64::from_bits(u64::from_str_radix(args["float_bits"].as_str().unwrap(), 16).unwrap())),
            };
            text(if b(&args["half"]) { number.half().to_string() } else { number.to_string() })
        }
        "presentation" => with_ctx(ctx, shared, |ctx| {
            let message: MessageView = serde_json::from_value(args["message"].clone()).map_err(|e| e.to_string())?;
            text(messages::presentation::message_presentation(ctx, &message))
        }),
        "json_by_bots_index" => {
            let input: Vec<messages::json::MessageJson> = serde_json::from_value(args["input"].clone()).map_err(|e| e.to_string())?;
            text(messages::json::by_bots_index(&input))
        }
        "json_by_bots_show" => {
            let input: messages::json::MessageJson = serde_json::from_value(args["input"].clone()).map_err(|e| e.to_string())?;
            text(messages::json::by_bots_show(&input))
        }
        "json_boosts_by_bots_show" => {
            let input: messages::json::BoostJson = serde_json::from_value(args["input"].clone()).map_err(|e| e.to_string())?;
            text(messages::json::boosts_by_bots_show(&input))
        }
        other => Err(format!("unknown messages op {other}")),
    }
}

/// Renders every case `rounds` times and prints the time each operation takes, as JSON lines.
pub fn bench(cases: &[Value], shared: &Value, rounds: usize) {
    let mut totals: BTreeMap<String, (u128, usize)> = BTreeMap::new();
    let warmup = rounds / 4;
    for round in 0..rounds + warmup {
        for case in cases {
            let op = case["op"].as_str().unwrap().to_string();
            let started = Instant::now();
            let answer = run(case, shared);
            let elapsed = started.elapsed().as_nanos();
            std::hint::black_box(&answer);
            if round >= warmup {
                let entry = totals.entry(op).or_default();
                entry.0 += elapsed;
                entry.1 += 1;
            }
        }
    }
    for (op, (nanos, count)) in totals {
        println!("{}", json!({ "op": op, "renders": count, "ns_per_render": nanos / count as u128 }));
    }
}

/// The page alone: `welcome/show` of the first such case, its context built once, rendered `rounds`
/// times (askama's `render`, the exact string it returns). Prints the time per page.
pub fn bench_page(cases: &[Value], shared: &Value, rounds: usize) {
    let case = cases.iter().find(|case| case["op"] == "welcome/show").expect("a welcome/show case");
    with_ctx(&case["ctx"], shared, |ctx| {
        let name = s(&case["args"]["current_user_name"]);
        let render = || welcome::Show { ctx, current_user_name: name.clone() }.render().unwrap();
        let length = render().len();
        for _ in 0..rounds / 4 {
            std::hint::black_box(render());
        }
        let started = Instant::now();
        for _ in 0..rounds {
            std::hint::black_box(render());
        }
        let nanos = started.elapsed().as_nanos() / rounds as u128;
        println!("{}", json!({ "op": "welcome/show (page only)", "bytes": length, "ns_per_render": nanos }));
    });
}

/// The fragment cache's hit, as a room page makes it for each of its messages: the key built in the thread's
/// buffer and looked up, 40 fragments of 4 KB, `rounds` times over. Prints the time per hit.
pub fn bench_cache(rounds: usize) {
    let cache = FragmentCache::new(fragment_cache::DEFAULT_MAX_BYTES);
    let at: jiff::Timestamp = "2026-09-26T12:23:46.483521Z".parse().unwrap();
    let digest = fragment_cache::digest(&["messages/_message", "messages/_actions"]);
    let digest = digest.as_str();
    let key = |id: i64| move |key: &mut String| fragment_cache::push_record_fragment_key(key, "messages/_message", digest, "messages", id, at);
    fragment_cache::with(&cache, || {
        for id in 0..40 {
            fragment_cache::fetch(key(1_000 + id), || "x".repeat(4096));
        }
        let hit = |round: usize| {
            for id in 0..40 {
                let fragment = fragment_cache::fetch(key(1_000 + id), || unreachable!("cached"));
                std::hint::black_box((&fragment, round));
            }
        };
        for round in 0..rounds / 4 {
            hit(round);
        }
        let started = Instant::now();
        for round in 0..rounds {
            hit(round);
        }
        let nanos = started.elapsed().as_nanos() / (rounds * 40) as u128;
        println!("{}", json!({ "op": "fragment_cache hit", "ns_per_hit": nanos }));
    });
}
