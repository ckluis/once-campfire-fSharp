//! What a page of cached fragments costs the Rust kit per request once its pieces are stored: splice the
//! parts, the ETag, and the gzip member (bench/front/fsharp's `micro` is the same on Campfire.Kit).
//!
//!   splice [iterations]

use std::sync::Arc;
use std::time::Instant;

use bytes::Bytes;
use campfire_kit::deflater::splice::PageParts;

fn message(n: usize) -> Arc<String> {
    Arc::new(format!(
        "<div id=\"message_{n}\" class=\"message\">{}</div>\n",
        "<button>Boost</button> hello there, see you at 10:30 in the usual room ".repeat(140 + n % 7)
    ))
}

fn main() {
    let iterations: usize = std::env::args().nth(1).and_then(|n| n.parse().ok()).unwrap_or(20_000);
    let messages: Vec<Arc<String>> = (0..40).map(message).collect();
    let head = "<html><head><title>Room</title></head><body>layout ".repeat(300);
    let tail = "</body></html>".repeat(100);
    let mut text = head.clone();
    let mut fragments = Vec::new();
    for message in &messages {
        text.push_str("  ");
        fragments.push((text.len(), message.clone()));
    }
    text.push_str(&tail);
    let body: usize = text.len() + messages.iter().map(|m| m.len()).sum::<usize>();

    let page = |text: &str| {
        let text = Bytes::copy_from_slice(text.as_bytes());
        let parts = PageParts::splice(&text, fragments.clone()).expect("parts");
        let etag = parts.etag();
        let gzip = parts.gzip(0);
        (etag, gzip)
    };
    for _ in 0..2_000 {
        page(&text);
    }
    let started = Instant::now();
    let mut gzip_len = 0;
    for _ in 0..iterations {
        gzip_len = page(&text).1.len();
    }
    let elapsed = started.elapsed();
    // Where the time goes, each step timed inside the loop.
    let (mut copy, mut splice, mut etag, mut gzip) = (0u128, 0u128, 0u128, 0u128);
    for _ in 0..iterations {
        let t0 = Instant::now();
        let bytes = Bytes::copy_from_slice(text.as_bytes());
        let t1 = Instant::now();
        let parts = PageParts::splice(&bytes, fragments.clone()).expect("parts");
        let t2 = Instant::now();
        std::hint::black_box(parts.etag());
        let t3 = Instant::now();
        std::hint::black_box(parts.gzip(0));
        let t4 = Instant::now();
        copy += (t1 - t0).as_nanos();
        splice += (t2 - t1).as_nanos();
        etag += (t3 - t2).as_nanos();
        gzip += (t4 - t3).as_nanos();
    }
    let per = |n: u128| n as f64 / 1000.0 / iterations as f64;
    println!("  in the loop: copy {:.2} splice {:.2} etag {:.2} gzip {:.2} us", per(copy), per(splice), per(etag), per(gzip));
    println!("body {body} bytes, gzip member {gzip_len} bytes: {:.2} us per page (splice, etag, gzip)", elapsed.as_secs_f64() * 1e6 / iterations as f64);
}
