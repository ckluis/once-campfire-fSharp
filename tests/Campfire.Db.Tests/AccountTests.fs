// Port of rust/crates/db/src/tests/account_test.rs
// `test/models/account_test.rb`, `test/models/account/joinable_test.rb`
module Campfire.Db.Tests.AccountTests

open Xunit
open Campfire.Db
open Campfire.Db.Tests.Support

let private signal (t: TestDb) : Account = t.Read(fun c -> (Account.first c).Value)

let private reload (t: TestDb) (account: Account) : Account = t.Read(fun c -> Account.find c account.Id)

let private restrict = "restrict_room_creation_to_administrators"

[<Fact>]
let ``settings`` () =
    use t = new TestDb()
    let account = signal t
    Assert.Equal(None, account.SettingsJson) // fixture leaves settings NULL

    let settings = Account.settings account |> AccountSettings.setRestrictRoomCreationToAdministrators "true"
    Assert.True(AccountSettings.restrictRoomCreationToAdministrators settings)
    Assert.Equal("""{"restrict_room_creation_to_administrators":true}""", AccountSettings.toJson settings)

    t.Write(fun tx -> Account.update tx account None None (Some [ restrict, "true" ]) |> ignore)
    let account = reload t account
    Assert.True(AccountSettings.restrictRoomCreationToAdministrators (Account.settings account))
    Assert.Equal(Some """{"restrict_room_creation_to_administrators":true}""", account.SettingsJson)

    let settings = Account.settings account |> AccountSettings.setRestrictRoomCreationToAdministrators "false"
    Assert.False(AccountSettings.restrictRoomCreationToAdministrators settings)
    Assert.Equal("""{"restrict_room_creation_to_administrators":false}""", AccountSettings.toJson settings)

    t.Write(fun tx -> Account.update tx account None None (Some [ restrict, "false" ]) |> ignore)
    let account = reload t account
    Assert.False(AccountSettings.restrictRoomCreationToAdministrators (Account.settings account))

[<Fact>]
let ``updating other attributes leaves null settings alone`` () =
    // What Rails does: `update!(name:)` on the fixture account doesn't write settings.
    use t = new TestDb()
    let account = signal t
    t.Write(fun tx -> Account.update tx account (Some "X") None None |> ignore)
    let account = signal t
    Assert.Equal("X", account.Name)
    Assert.Equal(None, account.SettingsJson)

[<Fact>]
let ``unknown settings are rejected`` () =
    use t = new TestDb()
    let account = signal t
    Assert.True((t.TryWrite(fun tx -> Account.update tx account None None (Some [ "nope", "1" ]))) |> Result.isError)

[<Fact>]
let ``new accounts get a joinable code`` () =
    use t = new TestDb()
    let account =
        t.Write(fun tx ->
            tx.Conn.Execute("DELETE FROM accounts", [||]) |> ignore
            Account.create tx "Chat")
    let parts = account.JoinCode.Split '-'
    Assert.Equal(3, parts.Length)
    Assert.True(parts |> Array.forall (fun p -> p.Length = 4 && p |> Seq.forall System.Char.IsAsciiLetterOrDigit))
    Assert.Equal(Some """{"restrict_room_creation_to_administrators":false}""", account.SettingsJson)
    Assert.Equal(0L, account.SingletonGuard)

[<Fact>]
let ``only one account can exist`` () =
    use t = new TestDb()
    Assert.True((t.TryWrite(fun tx -> Account.create tx "Second")) |> Result.isError)

[<Fact>]
let ``accounts can reset join code`` () =
    use t = new TestDb()
    let before = (signal t).JoinCode
    let account = signal t
    t.Write(fun tx -> Account.resetJoinCode tx account |> ignore)
    Assert.NotEqual<string>(before, (signal t).JoinCode)
