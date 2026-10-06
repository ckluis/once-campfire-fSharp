// Port of rust/crates/views/src/sessions.rs (the constants; the templates are modules under
// Templates/Sessions)
/// Views for `reference/app/views/sessions`.
module Campfire.Views.Sessions

/// `AllowBrowser::VERSIONS`, minus the browsers it blocks outright (`ie: false`).
let allowBrowserVersions: (string * string)[] = [| "safari", "17.2"; "chrome", "120"; "firefox", "121"; "opera", "104" |]
