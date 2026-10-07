#!/usr/bin/env python3
"""Writes the source of the Rust server: the app of rust/crates/kit/tests/http.rs (its actions, routes,
configuration), cut out of that file as it is, with a `main` that serves it.

  gen_main.py HTTP_RS OUT
"""
import sys

source = open(sys.argv[1]).read()
app = source[source.index("#[derive(Debug)]\nstruct AppState"):source.index("struct Reply {")]
header = '''use std::net::SocketAddr;
use std::sync::{Arc, LazyLock};

use axum::Router;
use campfire_kit::exceptions::ErrorPages;
use campfire_kit::format::{HTML, JSON, TURBO_STREAM};
use campfire_kit::{
    Cookie, Ctx, ExpiresIn, Freshness, Kit, KitConfig, Redirect, Result, SendOptions, SharedClock, StatusCode, TestClock, action, halt,
};
use rails_compat::Secrets;
use serde_json::json;

'''
main = '''
fn ssl_app() -> Router {
    let error_pages = ErrorPages::new([(422, "<h1>Unprocessable</h1>".into())]);
    let mut config = KitConfig { error_pages, force_ssl: true, ..KitConfig::default() };
    config.proxy.assume_ssl = true;
    app_with(config)
}

#[tokio::main]
async fn main() {
    let port: u16 = std::env::args().nth(1).and_then(|p| p.parse().ok()).unwrap_or(4001);
    // config.ru: `use Rack::Deflater` around the whole app.
    let router = if std::env::args().nth(2).as_deref() == Some("ssl") { ssl_app() } else { app() };
    let router = router.layer(axum::middleware::from_fn(campfire_kit::deflater::deflater));
    let listener = tokio::net::TcpListener::bind(SocketAddr::from(([127, 0, 0, 1], port))).await.unwrap();
    axum::serve(listener, router.into_make_service_with_connect_info::<SocketAddr>()).await.unwrap();
}
'''
open(sys.argv[2], "w").write(header + app + main)
