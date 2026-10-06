//! The Cable server bench/cable measures, on campfire_cable and axum. bench/cable/fsharp/Program.fs is the same
//! server on Campfire.Cable and Kestrel.
//!
//!   cablebench PORT

use std::collections::HashMap;

use axum::extract::{Query, State};
use axum::routing::post;
use campfire_cable::turbo::Action;
use campfire_cable::{Authenticate, Channel, ChannelResult, Config, ConnectRequest, Identified, Server, Subscription};

struct Anyone;

impl Identified for Anyone {
    fn connection_identifier(&self) -> String {
        "bench".to_string()
    }
}

struct Auth;

#[async_trait::async_trait]
impl Authenticate<Anyone> for Auth {
    async fn connect(&self, _request: &ConnectRequest) -> Option<Anyone> {
        Some(Anyone)
    }
}

struct Bench;

#[async_trait::async_trait]
impl Channel<Anyone> for Bench {
    async fn subscribed(&mut self, sub: &mut Subscription<Anyone>) -> ChannelResult {
        sub.stream_from("bench");
        Ok(())
    }
}

const FILLER: &str = "<p>Hello there, see you at 10:30 &amp; bring the <b>slides</b></p>";

fn html(seq: usize, bytes: usize) -> String {
    let mut body = format!("<div class=\"message\" data-seq=\"{seq}\">");
    while body.len() < bytes {
        body.push_str(FILLER);
    }
    body.push_str("</div>");
    body
}

async fn broadcast(State(server): State<Server<Anyone>>, Query(query): Query<HashMap<String, String>>) -> String {
    let get = |name: &str, fallback: usize| query.get(name).and_then(|v| v.parse().ok()).unwrap_or(fallback);
    let (seq, bytes, count) = (get("seq", 0), get("bytes", 600), get("count", 1));
    let mut received = 0;
    for i in 0..count {
        received = server.broadcast_action_to(
            &["bench"],
            Action::Append,
            campfire_cable::turbo::Target::Target("messages"),
            Some(&html(seq + i, bytes)),
            &[],
        );
    }
    received.to_string()
}

#[tokio::main]
async fn main() {
    let port: u16 = std::env::args().nth(1).expect("PORT").parse().unwrap();
    let config = Config { disable_request_forgery_protection: true, ..Config::default() };
    let server = Server::builder(config, Auth).channel("BenchChannel", || Bench).build();
    let app = axum::Router::new().route("/broadcast", post(broadcast)).with_state(server.clone()).merge(server.router::<()>("/cable"));
    let listener = tokio::net::TcpListener::bind(("127.0.0.1", port)).await.unwrap();
    axum::serve(listener, app).await.unwrap();
}
