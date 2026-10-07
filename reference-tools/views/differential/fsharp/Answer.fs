/// What an operation answers (`src/main.rs` of the Rust tool writes the same three fields).
module Campfire.Views.Differential.Answer

type Answer =
    { Out: string
      Text: string option
      Fragments: (int * int) list option }

let text (out: string) : Answer = { Out = out; Text = None; Fragments = None }

/// Renders with `ctx` and takes the text.
let rendered (f: Campfire.Views.Out -> unit) : Answer = text (Campfire.Views.Render.text f)
