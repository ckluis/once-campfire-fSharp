//! Reads one JSON request per line on stdin and writes the Rust kit's answer, one JSON line each. The
//! F# tool in `fsharp/` answers the same lines; `bin/kit-differential` compares them. Operations:
//!
//!   accept     {"op", "input"}                                            -> {"ok": [symbols]} | {"err"}
//!   formats    {"op", "accept"?, "path", "xhr", "content_type"?, "format_param"?} -> {"ok", "vary"} | {"err"}
//!   cookie     {"op", "input"}                                            -> {"ok": [[name, value]]}
//!   request    {"op", "headers": [[name, value]], "peer", "uri", "assume_ssl"} -> host, port, base_url, ...
//!   json_body  {"op", "input"}                                            -> {"ok": params} | {"err": kind}
//!   form       {"op", "hex"}                                              -> {"ok": params} | {"err": kind}
//!   permit     {"op", "input", "filters", "require"}                      -> {"ok", "require"} | {"err"}
//!   multipart  {"op", "hex", "content_type", "limit"?}                    -> {"ok": params} | {"perr"} | {"err"}
//!   boundary   {"op", "input"}                                            -> {"boundary": string | null}

use std::io::BufRead;

use axum::http::{HeaderMap, HeaderName, HeaderValue, Method};
use campfire_kit::format::{self, NegotiationInput};
use campfire_kit::params::ParamError;
use campfire_kit::request::{ProxyConfig, Request};
use campfire_kit::{Param, Permit};
use serde_json::{Value, json};

fn text(v: &Value, key: &str) -> Option<String> {
    v.get(key).and_then(|x| x.as_str()).map(|x| x.to_string())
}

fn error_kind(error: &ParamError) -> &'static str {
    match error {
        ParamError::Type(_) => "type",
        ParamError::Invalid(_) => "invalid",
        ParamError::TooDeep => "deep",
        ParamError::Limit(_) => "limit",
        ParamError::Parse => "parse",
    }
}

fn hex_decode(hex: &str) -> Vec<u8> {
    (0..hex.len()).step_by(2).map(|i| u8::from_str_radix(&hex[i..i + 2], 16).unwrap()).collect()
}

fn permit(filter: &Value) -> Permit {
    let name = filter["n"].as_str().unwrap().to_string();
    match filter["k"].as_str().unwrap() {
        "key" => Permit::Key(name),
        "array" => Permit::ScalarArray(name),
        "hash" => Permit::AnyHash(name),
        _ => Permit::Nested(name, filter["c"].as_array().unwrap().iter().map(permit).collect()),
    }
}

/// Params as JSON, with an uploaded file shown by what the kit records of it.
fn dump(param: &Param) -> Value {
    match param {
        Param::File(f) => json!({"file": {
            "name": f.original_filename, "ct": f.content_type, "headers": f.headers, "size": f.size,
            "hex": f.read().unwrap().iter().map(|b| format!("{b:02x}")).collect::<String>(),
        }}),
        Param::Array(items) => Value::Array(items.iter().map(dump).collect()),
        Param::Hash(map) => Value::Object(map.iter().map(|(k, v)| (k.clone(), dump(v))).collect()),
        other => other.to_json(),
    }
}

fn symbols(formats: &[format::Format]) -> Value {
    Value::Array(formats.iter().map(|m| json!(m.symbol)).collect())
}

fn answer(v: &Value) -> Value {
    match v["op"].as_str().unwrap() {
        "accept" => match format::parse_accept(&text(v, "input").unwrap()) {
            Ok(formats) => json!({"ok": symbols(&formats)}),
            Err(_) => json!({"err": true}),
        },
        "formats" => {
            let (accept, content_type, format_param) = (text(v, "accept"), text(v, "content_type"), text(v, "format_param"));
            let path = text(v, "path").unwrap();
            let input = NegotiationInput {
                format_param: format_param.as_deref(),
                accept: accept.as_deref(),
                content_type: content_type.as_deref(),
                path: &path,
                xhr: v["xhr"].as_bool().unwrap(),
            };
            match format::formats(&input) {
                Ok(formats) => json!({"ok": symbols(&formats), "vary": format::should_apply_vary_header(&input)}),
                Err(_) => json!({"err": true}),
            }
        }
        "cookie" => {
            let pairs = campfire_kit::cookies::parse_cookie_header(&text(v, "input").unwrap());
            json!({"ok": pairs.iter().map(|(k, v)| json!([k, v])).collect::<Vec<_>>()})
        }
        "request" => {
            let mut headers = HeaderMap::new();
            for h in v["headers"].as_array().unwrap() {
                headers.append(
                    HeaderName::from_bytes(h[0].as_str().unwrap().as_bytes()).unwrap(),
                    HeaderValue::from_str(h[1].as_str().unwrap()).unwrap(),
                );
            }
            let peer = text(v, "peer").map(|p| p.parse().unwrap());
            let uri: axum::http::Uri = text(v, "uri").unwrap().parse().unwrap();
            let mut proxy = ProxyConfig::default();
            proxy.assume_ssl = v["assume_ssl"].as_bool().unwrap();
            let r = Request::new(Method::GET, Method::GET, uri, headers, peer, bytes::Bytes::new(), &proxy);
            json!({
                "host": r.host(), "port": r.port(), "base_url": r.base_url(), "url": r.url(), "ssl": r.is_ssl(),
                "remote_ip": r.remote_ip().map(|s| s.to_string()).unwrap_or_else(|_| "spoof".to_string()),
                "remote_ok": r.remote_ip().is_ok(),
                "media_type": r.media_type(),
            })
        }
        "json_body" => match campfire_kit::params::from_json_body(text(v, "input").unwrap().as_bytes()) {
            Ok(map) => json!({"ok": map.to_json()}),
            Err(e) => json!({"err": error_kind(&e)}),
        },
        "form" => {
            let body = hex_decode(&text(v, "hex").unwrap());
            match campfire_kit::params::form_pairs(&body).and_then(campfire_kit::params::from_pairs) {
                Ok(map) => json!({"ok": map.to_json()}),
                Err(e) => json!({"err": error_kind(&e)}),
            }
        }
        "permit" => {
            let map = match campfire_kit::params::from_query_string(&text(v, "input").unwrap()) {
                Ok(map) => map,
                Err(e) => return json!({"err": error_kind(&e)}),
            };
            let filters: Vec<Permit> = v["filters"].as_array().unwrap().iter().map(permit).collect();
            let required = match map.require(&text(v, "require").unwrap()) {
                Ok(p) => p.to_json(),
                Err(_) => json!("missing"),
            };
            json!({"ok": map.permit(&filters).to_json(), "require": required})
        }
        "multipart" => {
            let body = hex_decode(&text(v, "hex").unwrap());
            let mut headers = HeaderMap::new();
            headers.insert("content-type", HeaderValue::from_str(&text(v, "content_type").unwrap()).unwrap());
            let limit = v["limit"].as_u64().map(|l| l as usize);
            let runtime = tokio::runtime::Builder::new_current_thread().build().unwrap();
            match runtime.block_on(campfire_kit::body::parse(&Method::POST, &headers, axum::body::Body::from(body), limit)) {
                Err(campfire_kit::body::BodyError::TooLarge) => json!({"err": "toolarge"}),
                Err(_) => json!({"err": "read"}),
                Ok(parsed) => match parsed.params {
                    Err(e) => json!({"perr": error_kind(&e)}),
                    Ok(map) => json!({"ok": dump(&Param::Hash(map)), "raw_len": parsed.raw.len()}),
                },
            }
        }
        "boundary" => {
            // What `multer::parse_boundary` does with it.
            let content_type = text(v, "input").unwrap();
            let boundary = content_type
                .parse::<mime::Mime>()
                .ok()
                .filter(|m| m.type_() == mime::MULTIPART && m.subtype() == mime::FORM_DATA)
                .and_then(|m| m.get_param(mime::BOUNDARY).map(|name| name.as_str().to_owned()));
            json!({"boundary": boundary})
        }
        other => panic!("unknown op {other}"),
    }
}

fn main() {
    for line in std::io::stdin().lock().lines() {
        let request: Value = serde_json::from_str(&line.unwrap()).unwrap();
        println!("{}", answer(&request));
    }
}
