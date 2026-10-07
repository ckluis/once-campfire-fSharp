use campfire_richtext::{
    editable_value, mentioned_users, message_presentation, to_plain_text, AttachableResolver, GidLookup, MentionUser, RenderContext, SignedLookup,
};
use serde_json::{json, Value};
use std::io::{BufRead, Write};

struct TestResolver {
    users: Vec<MentionUser>,
    rooms: Vec<i64>,
    signed: Vec<(String, String, i64, bool)>,
}

impl AttachableResolver for TestResolver {
    fn locate_signed(&self, sgid: &str) -> SignedLookup {
        match self.signed.iter().find(|(s, ..)| s == sgid) {
            Some((_, model, id, true)) if model == "User" => SignedLookup::User(self.users.iter().find(|u| u.id == *id).unwrap().clone()),
            Some((_, model, _, _)) => SignedLookup::MissingRecord { model_name: model.clone() },
            None => SignedLookup::Invalid,
        }
    }
    fn find_gid(&self, gid: &str) -> GidLookup {
        let Some(rest) = gid.strip_prefix("gid://") else { return GidLookup::NotFound };
        if !gid.is_ascii() { return GidLookup::NotFound; }
        let Some((_app, rest)) = rest.split_once('/') else { return GidLookup::NotFound };
        let rest = rest.split('?').next().unwrap();
        let Some((model, id)) = rest.split_once('/') else { return GidLookup::NotFound };
        let Ok(id) = id.parse::<i64>() else { return GidLookup::NotFound };
        match model {
            "User" => self.users.iter().find(|u| u.id == id).cloned().map_or(GidLookup::NotFound, GidLookup::User),
            "Room" if self.rooms.contains(&id) => GidLookup::OtherModel,
            _ => GidLookup::NotFound,
        }
    }
}

fn res<T: ToString, E: ToString>(r: Result<T, E>) -> Value {
    match r { Ok(v) => json!({"ok": v.to_string()}), Err(e) => json!({"err": e.to_string()}) }
}

fn main() {
    // The users, rooms and SGIDs the corpus was made with (the F# side reads the same file)
    let path = std::env::var("RICHTEXT_CORPUS")
        .unwrap_or_else(|_| concat!(env!("CARGO_MANIFEST_DIR"), "/../../../tests/Campfire.RichText.Tests/corpus/expected.json").to_string());
    let text = std::fs::read_to_string(&path).expect(&path);
    let corpus: Value = serde_json::from_str(&text).unwrap();
    let users: Vec<MentionUser> = corpus["users"].as_array().unwrap().iter().map(|u| MentionUser {
        id: u["id"].as_i64().unwrap(), name: u["name"].as_str().unwrap().into(), title: u["title"].as_str().unwrap().into(),
        attachable_sgid: u["attachable_sgid"].as_str().unwrap().into(), user_path: u["user_path"].as_str().unwrap().into(), avatar_path: u["avatar_path"].as_str().unwrap().into(),
    }).collect();
    let resolver = TestResolver {
        users,
        rooms: corpus["rooms"].as_array().unwrap().iter().map(|r| r.as_i64().unwrap()).collect(),
        signed: corpus["signed"].as_array().unwrap().iter().map(|s| (s["sgid"].as_str().unwrap().into(), s["model"].as_str().unwrap().into(), s["id"].as_i64().unwrap(), s["exists"].as_bool().unwrap())).collect(),
    };
    let stdin = std::io::stdin();
    let stdout = std::io::stdout();
    let mut out = std::io::BufWriter::new(stdout.lock());
    for line in stdin.lock().lines() {
        let line = line.unwrap();
        if line.is_empty() { continue; }
        let v: Value = serde_json::from_str(&line).unwrap();
        let body = v.as_str().unwrap();
        let ctx = RenderContext { resolver: &resolver, request_host: Some("once.campfire.test".into()) };
        let presentation = res(message_presentation(body, &ctx));
        let plain = res(to_plain_text(body, &ctx));
        let editable = match editable_value(body, &ctx) { Ok(Some(s)) => json!({"ok": s}), Ok(None) => json!({"none": true}), Err(e) => json!({"err": e.to_string()}) };
        let mentioned = match mentioned_users(body, &ctx) { Ok(us) => json!({"ok": us.iter().map(|u| u.id).collect::<Vec<_>>()}), Err(e) => json!({"err": e.to_string()}) };
        writeln!(out, "{}", serde_json::to_string(&json!([presentation, plain, editable, mentioned])).unwrap()).unwrap();
    }
}
