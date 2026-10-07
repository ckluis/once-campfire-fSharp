// Port of rust/crates/campfire/src/controllers/searches.rs
//
// `SearchesController` (reference/app/controllers/searches_controller.rb). `set_messages` runs
// before every action, so a query that FTS5 rejects fails `create` and `clear` too.
namespace Campfire.App.Controllers

open System
open System.Threading.Tasks
open Campfire.App
open Campfire.App.Presenters
open Campfire.Db
open Campfire.Kit
open Campfire.Routes
open Campfire.Views
open Campfire.Views.Searches

module SearchesController =
    module IndexPage = Campfire.Views.Templates.Searches.Index

    let private indexSize = RenderSize()

    /// `params[:q]`; anything but a string makes `gsub` raise.
    let private queryParam (c: Ctx) : Result<string option, Error> =
        match c.Param "q" with
        | ValueNone -> Ok None
        | ValueSome Param.Null -> Ok None
        | ValueSome(Param.Str q) -> Ok(Some q)
        | ValueSome _ -> Error(Internal(exn "undefined method 'gsub'"))

    /// `params[:q]&.gsub(/[^[:word:]]/, " ")`
    let query (q: string option) : string option = Campfire.App.Integrations.Search.sanitizeQuery q

    let private isPresent (value: string) : bool = not (value |> Seq.forall Char.IsWhiteSpace)

    /// The `query` `set_messages` searches for: none when it's blank.
    let private searchableQuery (q: string option) : string option = query q |> Option.filter isPresent

    /// `set_messages`: `Current.user.reachable_messages.search(query).last(100)` when there's a query.
    let private setMessages (c: Ctx) (q: string option) : Task<Result<Message list, Error>> =
        act {
            match searchableQuery q with
            | None -> return []
            | Some query ->
                let! (user: User) = Concerns.requireCurrentUser c
                return! c.App.ReadOffloaded(fun conn -> Message.searchReachable conn user.Id query)
        }

    let index (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            let! q = queryParam c
            let! (user: User) = Concerns.requireCurrentUser c
            let query = searchableQuery q
            let lastRoom = Concerns.lastRoomCookie c
            // `set_messages`, the recent searches and `last_room_visited` in one trip to a reader, off the
            // runtime's workers since the search's cost grows with the whole database.
            let! messages, recentSearches, returnToRoomId =
                c.App.ReadOffloaded(fun conn ->
                    let messages =
                        match query with
                        | Some query -> Message.searchReachable conn user.Id query
                        | None -> []
                    let recentSearches = Search.orderedForUser conn user.Id |> List.map (fun search -> search.Query)
                    let returnToRoomId =
                        Concerns.lastRoomVisitedIn conn user.Id lastRoom |> Option.map (fun room -> room.Id) |> Option.defaultValue 0L
                    messages, recentSearches, returnToRoomId)
            let! messages = MessagesController.present c (fun presenter -> presenter.Messages messages)
            let index: IndexView =
                { Query = query
                  Q = q
                  Messages = messages
                  RecentSearches = recentSearches
                  ReturnToRoomId = returnToRoomId }
            return!
                Page.framedPage
                    c
                    Status.Ok
                    indexSize
                    (fun w ctx -> IndexPage.render w ctx index)
                    IndexPage.head
                    (fun w ctx -> IndexPage.content w ctx index)
        }

    let create (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            let! q = queryParam c
            let! (_: Message list) = setMessages c q
            let! (user: User) = Concerns.requireCurrentUser c
            let query = query q
            // Current.user.searches.record(query): a nil query violates `query`'s NOT NULL.
            let! recorded =
                match query with
                | Some query -> Ok query
                | None -> Error(Internal(exn "NOT NULL constraint failed: searches.query"))
            let! (_: Search) = c.App.Write(fun tx -> Search.record tx user.Id recorded)
            let path =
                match query with
                | Some query -> searchPath query
                | None -> Routes.searches ()
            return! c.RedirectTo(c.UrlFor path)
        }

    let clear (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            let! q = queryParam c
            let! (_: Message list) = setMessages c q
            let! (user: User) = Concerns.requireCurrentUser c
            do! c.App.Write(fun tx -> Search.destroyAllForUser tx user.Id)
            return! c.RedirectTo(c.UrlFor(Routes.searches ()))
        }
