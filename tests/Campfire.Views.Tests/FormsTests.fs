// Port of the #[cfg(test)] module in rust/crates/views/src/helpers/forms.rs, with tests of the form
// helpers' shapes (their bytes are compared with Rust's by bin/views-differential)
module Campfire.Views.Tests.FormsTests

open Xunit
open Campfire.Views
open Campfire.Views.Helpers
open Campfire.Views.Helpers.Tag

[<Fact>]
let ``sanitizes nested object names`` () =
    Assert.Equal("account_settings", Forms.sanitizeObjectName "account[settings]")
    Assert.Equal("user", Forms.sanitizeObjectName "user")

[<Fact>]
let ``a character that is not allowed in an id is one underscore, not one per UTF-16 unit`` () =
    // Rust maps `chars()`: an emoji is one character.
    Assert.Equal("__ab", Forms.sanitizeToId "😀 a]b")
    Assert.Equal("a_b-c:d.e", Forms.sanitizeToId "a b-c:d.e")
    Assert.Equal("a_b", Forms.sanitizeObjectName "a😀b")
    Assert.Equal("a_b", Forms.sanitizeObjectName "a[b]")

[<Fact>]
let ``a form is written after its fields, so a file field makes it multipart`` () =
    let form = (Forms.formWith "/account").Model("account").Method("patch")
    let html =
        Render.text (fun w ->
            form.Wrap(w, fun inner ->
                form.TextField(inner, "name", Some "A&B", Tag.attrs().Class("input"))
                form.FileField(inner, "logo", Tag.attrs())))
    // The bytes the Rust crate writes for the same form.
    Assert.Equal(
        "<form enctype=\"multipart/form-data\" action=\"/account\" accept-charset=\"UTF-8\" method=\"post\"><input type=\"hidden\" name=\"_method\" value=\"patch\" /><input class=\"input\" type=\"text\" value=\"A&amp;B\" name=\"account[name]\" id=\"account_name\" /><input type=\"file\" name=\"account[logo]\" id=\"account_logo\" /></form>",
        html)

[<Fact>]
let ``fields are scoped under the model and fields_for nests`` () =
    let form = (Forms.formWith "/x").Model "account"
    let nested = form.FieldsFor "settings"
    let html = Render.text (fun w -> nested.TextField(w, "limit", Some "5", Tag.attrs ()))
    Assert.Contains("name=\"account[settings][limit]\"", html)
    Assert.Contains("id=\"account_settings_limit\"", html)

[<Fact>]
let ``a password field never renders the models value`` () =
    let form = (Forms.formWith "/x").Model "user"
    let html = Render.text (fun w -> form.PasswordField(w, "password", Tag.attrs()))
    Assert.DoesNotContain("value=", html)

[<Fact>]
let ``a check box is a hidden unchecked value then the box`` () =
    let form = (Forms.formWith "/x").Model "account"
    let html = Render.text (fun w -> form.CheckBox(w, "restrict", Tag.attrs(), "true", "false", "true"))
    Assert.Equal(
        "<input name=\"account[restrict]\" type=\"hidden\" value=\"false\" /><input type=\"checkbox\" value=\"true\" checked=\"checked\" name=\"account[restrict]\" id=\"account_restrict\" />",
        html)

[<Fact>]
let ``button_to wraps a button in a form that carries the method`` () =
    let html =
        Render.text (fun w ->
            Forms.buttonToBlock w "/rooms/1" (Tag.attrs().Method("delete").Class("btn")) (fun w -> w.Raw "Delete"))
    Assert.Equal(
        "<form class=\"button_to\" method=\"post\" action=\"/rooms/1\"><input type=\"hidden\" name=\"_method\" value=\"delete\" /><button class=\"btn\" type=\"submit\">Delete</button></form>",
        html)
