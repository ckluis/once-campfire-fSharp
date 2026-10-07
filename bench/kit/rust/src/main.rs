//! The endpoints bench/kit measures, on campfire_kit and hyper (axum's server). bench/kit/fsharp/Program.fs
//! is the same endpoints on Campfire.Kit and Kestrel.
//!
//!   kitbench PORT

use std::net::SocketAddr;
use std::sync::{Arc, LazyLock};

use axum::Router;
use campfire_kit::clock::SystemClock;
use campfire_kit::{Ctx, Kit, KitConfig, Result, action};
use rails_compat::Secrets;

static PAGE: LazyLock<bytes::Bytes> = LazyLock::new(|| {
    let one = "<div class=\"message\" data-message-id=\"42\"><p>Hello there, see you at 10:30</p></div>\n";
    bytes::Bytes::from(one.repeat(1200))
});

async fn hello(c: &mut Ctx) -> Result {
    Ok(c.html("hello"))
}

async fn page(c: &mut Ctx) -> Result {
    Ok(c.html(PAGE.clone()))
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
    let port: u16 = std::env::args().nth(1).and_then(|p| p.parse().ok()).unwrap_or(3002);
    let secrets = Arc::new(Secrets::new("bench-secret-key-base"));
    let kit = Kit::new(KitConfig::default(), secrets, Arc::new(SystemClock), ());
    let router = Router::new()
        .route("/hello", campfire_kit::get(hello))
        .route("/page", campfire_kit::get(page))
        .route("/session", campfire_kit::get(session))
        .route("/rooms/{id}/messages", axum::routing::post(action(post)));
    // config.ru: `use Rack::Deflater` around the whole app.
    let app = campfire_kit::app(router, kit).layer(axum::middleware::from_fn(campfire_kit::deflater::deflater));
    let listener = tokio::net::TcpListener::bind(SocketAddr::from(([127, 0, 0, 1], port))).await.unwrap();
    axum::serve(listener, app.into_make_service_with_connect_info::<SocketAddr>()).await.unwrap();
}
