// The tests that sit beside the code in rust/crates/db: models/ban.rs, models/sound.rs, testing.rs,
// fixtures.rs.
module Campfire.Db.Tests.UnitTests

open System
open System.IO
open System.Text
open Xunit
open Campfire.Db
open Campfire.Db.Tests.Support

// models/ban.rs

let private messages (ip: string) : string list = Ban.validate ip |> fun errors -> errors.Items |> List.map snd

[<Fact>]
let ``public addresses are valid`` () =
    Assert.True(List.isEmpty (messages "8.8.8.8"))
    Assert.True(List.isEmpty (messages "2001:4860:4860::8888"))

[<Fact>]
let ``internal addresses are rejected`` () =
    for ip in
        [ "127.0.0.1"; "10.1.2.3"; "172.16.0.1"; "192.168.1.1"; "169.254.169.254"; "::1"; "fc00::1"; "fd12::1"; "fe80::1"
          "::ffff:10.0.0.1"; "::ffff:127.0.0.1" ] do
        Assert.True((messages ip = [ "cannot be a private or internal IP address" ]), ip)

[<Fact>]
let ``garbage is invalid`` () =
    Assert.Equal<string list>([ "is not a valid IP address" ], messages "not an ip")
    Assert.Equal<string list>([ "is not a valid IP address" ], messages "")

// models/sound.rs

[<Fact>]
let ``builtin sounds match reference`` () =
    let ruby = File.ReadAllText(Campfire.Tests.Repo.path "reference/app/models/sound.rb")
    let count = ruby.Split('\n') |> Array.filter (fun l -> l.TrimStart().StartsWith "new(name:") |> Array.length
    Assert.Equal(count, List.length Sound.builtin)
    for sound in Sound.builtin do
        Assert.True(ruby.Contains $"new(name: \"{sound.Name}\"", sound.Name)
        match sound.Text with
        | Some text -> Assert.True(ruby.Contains $"text: \"{text}\"", sound.Name)
        | None -> ()
    Assert.Equal("sounds/top.webp", SoundImage.assetPath (Sound.findByName "deeper").Value.Image.Value)

// testing.rs

let private memory () : Conn = Conn.OpenInMemory()

/// `MentionTestHelper#mention_attachment_for`, with an unsigned SGID.
let private unsignedMention (userId: int64) : string =
    let payload =
        Convert.ToBase64String(Encoding.UTF8.GetBytes $"""{{"_rails":{{"data":"gid://campfire/User/{userId}","pur":"attachable"}}}}""")
    $"""<action-text-attachment sgid="{payload}--unsigned" content-type="application/vnd.campfire.mention"></action-text-attachment>"""

[<Fact>]
let ``plain text strips tags`` () =
    use conn = memory ()
    let richText = BasicRichText() :> RichText
    Assert.Equal("My hovercraft is full of eels", richText.ToPlainText(conn, "<span>My hovercraft is full of eels</span>"))
    Assert.Equal("Hello there", richText.ToPlainText(conn, "Hello <b>there</b>"))

[<Fact>]
let ``plain text renders mentions by name`` () =
    use t = new TestDb()
    let html = "Hey " + unsignedMention (id "kevin")
    let richText = BasicRichText() :> RichText
    Assert.Equal("Hey @Kevin", t.Read(fun c -> richText.ToPlainText(c, html)))

[<Fact>]
let ``reports no mentions`` () =
    use conn = memory ()
    let html = "<div>Hey " + unsignedMention (id "kevin") + "</div>"
    Assert.Equal<int64 list>([], (BasicRichText() :> RichText).MentionedUserIds(conn, html))

// fixtures.rs

[<Fact>]
let ``identify matches rails`` () =
    // ActiveRecord::FixtureSet.identify, from the reference app.
    Assert.Equal(127326141L, Fixtures.identify "david")
    Assert.Equal(873240054L, Fixtures.identify "signal")
    Assert.Equal(654632876L, Fixtures.identify "designers")

[<Fact>]
let ``table names`` () =
    let dir = Path.Combine(Path.DirectorySeparatorChar.ToString(), "f")
    Assert.Equal("action_text_rich_texts", Fixtures.tableName dir (Path.Combine(dir, "action_text", "rich_texts.yml")))
    Assert.Equal("push_subscriptions", Fixtures.tableName dir (Path.Combine(dir, "push", "subscriptions.yml")))
    Assert.Equal("users", Fixtures.tableName dir (Path.Combine(dir, "users.yml")))

[<Fact>]
let ``erb ago renders whole seconds`` () =
    let options: Fixtures.Options = { Now = (Timestamp.ParseDb "2026-09-26 12:24:38.211309").Value; BcryptCost = 4 }
    let erb = Fixtures.Erb options
    Assert.Equal("created_at: 2026-09-26 11:24:38 UTC", erb.Render "created_at: <%= 1.hour.ago %>")
    Assert.Equal("\na: 2026-09-26 12:19:38 UTC", erb.Render "<% x = 5.minutes.ago %>\na: <%= x %>")
