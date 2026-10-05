//! The endpoints bench/front measures: bench/kit's, behind the front server (`campfire_kit::front`) as the
//! app runs it. bench/front/fsharp/Program.fs is the same on Campfire.Kit's front server.
//!
//!   frontbench HTTP_PORT TARGET_PORT

use std::sync::{Arc, LazyLock};

use axum::Router;
use campfire_kit::clock::SystemClock;
use campfire_kit::front::{self, FrontConfig};
use campfire_kit::{Ctx, Kit, KitConfig, Result, action};
use rails_compat::Secrets;

static PAGE: LazyLock<bytes::Bytes> = LazyLock::new(|| {
    let one = "<div class=\"message\" data-message-id=\"42\"><p>Hello there, see you at 10:30</p></div>\n";
    bytes::Bytes::from(one.repeat(1200))
});

static ASSET: LazyLock<bytes::Bytes> = LazyLock::new(|| bytes::Bytes::from("body { color: #123456; }\n".repeat(800)));

async fn hello(c: &mut Ctx) -> Result {
    Ok(c.html("hello"))
}

async fn page(c: &mut Ctx) -> Result {
    Ok(c.html(PAGE.clone()))
}

async fn asset(c: &mut Ctx) -> Result {
    Ok(c.html(ASSET.clone()).header("cache-control", "public, max-age=3600"))
}

async fn session(c: &mut Ctx) -> Result {
    match c.cookies.signed("session_token") {
        None => c.redirect_to("/session/new"),
        Some(token) => {
            let mut body = token.into_bytes();
            body.extend_from_slice(&PAGE);
            Ok(c.html(body))
        }
    }
}

async fn post(c: &mut Ctx) -> Result {
    c.verify_authenticity_token()?;
    let message = c.params.require("message")?.clone();
    let len = message.get("body").and_then(|b| b.to_s()).map(|s| s.len()).unwrap_or(0);
    c.redirect_to(&format!("/rooms/1?m={len}"))
}

#[tokio::main]
async fn main() {
    let args: Vec<String> = std::env::args().collect();
    let (http, target) = (args.get(1).cloned().unwrap_or("3002".into()), args.get(2).cloned().unwrap_or("3102".into()));
    let secrets = Arc::new(Secrets::new("bench-secret-key-base"));
    let kit = Kit::new(KitConfig::default(), secrets, Arc::new(SystemClock), ());
    let router = Router::new()
        .route("/hello", campfire_kit::get(hello))
        .route("/page", campfire_kit::get(page))
        .route("/asset", campfire_kit::get(asset))
        .route("/session", campfire_kit::get(session))
        .route("/rooms/{id}/messages", axum::routing::post(action(post)));
    // config.ru: `use Rack::Deflater` around the whole app.
    let app = campfire_kit::app(router, kit).layer(axum::middleware::from_fn(campfire_kit::deflater::deflater));
    let config = FrontConfig::from_lookup(|name| match name {
        "HTTP_PORT" => Some(http.clone()),
        "TARGET_PORT" => Some(target.clone()),
        "LOG_REQUESTS" => Some("false".into()),
        _ => None,
    });
    front::serve(config, app, std::future::pending()).await.unwrap();
}
