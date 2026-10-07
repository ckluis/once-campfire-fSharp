// Uploads and removals of an avatar and a logo through the controllers, over the `default` seed. The Rust
// tests don't drive these (`reference-tools/campfire/controllers_a/replay.py` compares them with the
// reference); a SQL parameter written as `?1` ahead of this test made every one of them a 500.
module Campfire.App.Tests.AttachmentTests

open System.IO
open Xunit
open Campfire.App.Tests.Support
open Campfire.Tests

let private png () : byte[] = File.ReadAllBytes(Repo.path "reference/app/assets/images/campfire-icon.png")

let private upload (path: string) (meth: string) (fields: (string * string) list) (file: string) : Req =
    multipartReq (request "POST" path) ((if meth <> "post" then [ "_method", meth ] else []) @ fields) (file, "me.png", "image/png", png ())

[<Fact>]
let ``uploads and removes an avatar`` () =
    task {
        use! test = bootSeeded "default"
        let b = browser test "198.51.100.30"
        do! b.SignIn(test.Label "emails.kevin")
        let kevin = test.Label "users.kevin"

        let! reply = b.Write(upload "/users/me/profile" "patch" [] "user[avatar]")
        Assert.True((reply.Status = 302), reply.Text)
        Assert.Equal(Some "http://campfire.test/users/me/profile", reply.Location)
        let! profile = b.Get "/users/me/profile"
        Assert.Contains("It may take up to 30 minutes to change everywhere.", profile.Text)
        Assert.Contains($"action=\"/users/{kevin}/avatar\"", profile.Text)

        // Kevin's own avatar is now an uploaded image, served as its WebP variant.
        let marker = "/users/eyJ"
        let at = profile.Text.IndexOf marker
        let avatarPath = profile.Text.Substring(at).Split('?')[0]
        let! avatar = b.Get avatarPath
        Assert.True((avatar.Status = 200), avatar.Text)
        Assert.Equal(Some "image/webp", avatar.Header "content-type")

        let! removed = b.Form("delete", $"/users/{avatarPath.Split('/')[2]}/avatar", [])
        Assert.True((removed.Status = 302), removed.Text)
        Assert.Equal(Some "http://campfire.test/users/me/profile", removed.Location)
        let! after = b.Get avatarPath
        Assert.Equal(Some "image/svg+xml; charset=utf-8", after.Header "content-type")
    }

[<Fact>]
let ``uploads and removes the account logo`` () =
    task {
        use! test = bootSeeded "default"
        let b = browser test "198.51.100.31"
        do! b.SignIn(test.Label "emails.david")
        let accountId = test.Label "accounts.signal"

        let! reply = b.Write(upload $"/account.{accountId}" "patch" [] "account[logo]")
        Assert.True((reply.Status = 302), reply.Text)
        Assert.Equal(Some "http://campfire.test/account/edit", reply.Location)
        let! logo = b.Get "/account/logo"
        Assert.Equal((200, Some "image/png"), (logo.Status, logo.Header "content-type"))

        let! removed = b.Form("delete", "/account/logo", [])
        Assert.True((removed.Status = 302), removed.Text)
        Assert.Equal(Some "http://campfire.test/account/edit", removed.Location)
    }

[<Fact>]
let ``creates a bot with an avatar`` () =
    task {
        use! test = bootSeeded "default"
        let b = browser test "198.51.100.32"
        do! b.SignIn(test.Label "emails.david")
        let! reply = b.Write(upload "/account/bots" "post" [ "user[name]", "Pixel" ] "user[avatar]")
        Assert.True((reply.Status = 302), reply.Text)
        Assert.Equal(Some "http://campfire.test/account/bots", reply.Location)
        let! bots = b.Get "/account/bots"
        Assert.Contains("Pixel", bots.Text)
    }
