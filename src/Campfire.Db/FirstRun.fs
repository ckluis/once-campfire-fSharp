// Port of rust/crates/db/src/models/first_run.rs
// `reference/app/models/first_run.rb`
namespace Campfire.Db

module FirstRun =
    [<Literal>]
    let AccountName = "Campfire"

    [<Literal>]
    let FirstRoomName = "All Talk"

    /// `FirstRun.create!(user_params)`: the account, an administrator, and the first (open)
    /// room, which the administrator joins. Rails runs these as separate saves; the room and
    /// user callbacks (open room grants, user's open-room memberships) run after commit and
    /// the explicit grant follows them.
    let create (tx: Tx) (name: string) (emailAddress: string) (passwordDigest: PasswordDigest) : User =
        Account.create tx AccountName |> ignore
        let administrator =
            User.create
                tx
                { NewUser.create name with
                    EmailAddress = Some emailAddress
                    PasswordDigest = Some passwordDigest
                    Role = Role.Administrator }
        let room = Room.create tx Open (Some FirstRoomName) administrator.Id
        let administratorId = administrator.Id
        tx.AfterCommit(fun tx -> Room.grantTo tx room [ administratorId ])
        administrator
