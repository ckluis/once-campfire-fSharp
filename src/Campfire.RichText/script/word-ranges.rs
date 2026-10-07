// Prints the ranges of code points `\w` matches in the Rust regex crate (see generate-tables.py).
// Run from a scratch crate that depends on regex 1.13:  cargo run --release > word_ranges.txt
use regex::Regex;

fn main() {
    let re = Regex::new(r"\A\w\z").unwrap();
    let mut ranges: Vec<(u32, u32)> = vec![];
    let mut buf = [0u8; 4];
    for cp in 0..=0x10FFFFu32 {
        let Some(c) = char::from_u32(cp) else { continue };
        if re.is_match(c.encode_utf8(&mut buf)) {
            match ranges.last_mut() {
                Some((_, hi)) if *hi + 1 == cp => *hi = cp,
                _ => ranges.push((cp, cp)),
            }
        }
    }
    for (lo, hi) in &ranges {
        println!("{lo:x} {hi:x}");
    }
}
