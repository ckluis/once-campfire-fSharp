// Port of rust/crates/db/src/models/sound.rs
// `reference/app/models/sound.rb`
namespace Campfire.Db

type SoundImage =
    { /// `"sounds/#{name}"`
      Name: string
      Width: int
      Height: int }

module SoundImage =
    let assetPath (image: SoundImage) : string = $"sounds/{image.Name}"

type Sound =
    { Name: string
      Text: string option
      Image: SoundImage option }

module Sound =
    let private text (name: string) (text: string) : Sound = { Name = name; Text = Some text; Image = None }

    let private image (name: string) (file: string) (width: int) (height: int) : Sound =
        { Name = name; Text = None; Image = Some { Name = file; Width = width; Height = height } }

    let builtin : Sound list =
        [
      image "56k" "56k.webp" 79 33
      text "bell" "🔔"
      text "bezos" "😆💭"
      text "bueller" "anyone?"
      text "butts" "👐 🚬"
      image "clowntown" "clowntown.webp" 210 150
      text "cottoneyejoe" "🎶🙉🎶 "
      text "crickets" "hears crickets chirping"
      image "curb" "curb.webp" 150 101
      text "dadgummit" "dad gummit!! 🎣"
      image "dangerzone" "dangerzone.webp" 157 32
      text "danielsan" "🎆 🏆 🎆"
      image "deeper" "top.webp" 188 80
      text "ballmer" "developers!"
      image "donotwant" "donotwant.webp" 150 150
      image "drama" "drama.webp" 300 16
      text "flawless" "#flawless"
      text "glados" "🤖💢"
      text "gogogo" "Go, go, go!"
      image "greatjob" "greatjob.webp" 79 16
      text "greyjoy" "😖🎺"
      text "guarantee" "guarantees it 👌"
      text "heygirl" "✨💁✨"
      text "honk" "HONK"
      text "horn" "🐶 ✂\uFE0F 🐱"
      text "horror" "💀 💀 💀 💀 💀 💀 💀"
      text "inconceivable" "doesn't think it means what you think it means…"
      text "letitgo" "❄\uFE0F👩❄\uFE0F⛄\uFE0F❄\uFE0F"
      text "live" "is DOING IT LIVE"
      image "loggins" "loggins.webp" 200 151
      text "makeitso" "make it so 👉"
      text "noooo" "👸💀😒"
      image "nyan" "nyan.webp" 36 15
      text "ohmy" "raises an eyebrow 😏"
      text "ohyeah" "isn't playing by the rules"
      image "pushit" "pushit.webp" 104 15
      text "rimshot" "plays a rimshot"
      text "rollout" "is rolling out 🚗"
      image "rumble" "rumble.webp" 220 150
      text "sax" "🌇🎷🎶"
      text "secret" "found a secret area 🔑"
      text "sexyback" "🔞"
      text "story" "and now you know…"
      text "tada" "plays a fanfare 🎏"
      text "tmyk" "✨ ⭐\uFE0F The More You Know ✨ ⭐\uFE0F"
      text "totes" "😁👍"
      text "trololo" "трололо"
      text "trombone" "plays a sad trombone"
      text "unix" "knows this 💻"
      text "vuvuzela" "======<() ~ ♪ ~♫"
      image "what" "what.webp" 100 131
      text "whoomp" "👏‼\uFE0F😎"
      text "wups" "wups!"
      image "yay" "yay.webp" 103 50
      image "yeah" "yeah.webp" 104 15
      text "yodel" "📣🗻🙉" ]

    /// `"#{name}.mp3"`
    let assetPath (sound: Sound) : string = $"{sound.Name}.mp3"

    let findByName (name: string) : Sound option = builtin |> List.tryFind (fun s -> s.Name = name)

    /// `Sound.names`: sorted.
    let names () : string list = builtin |> List.map (fun s -> s.Name) |> List.sortWith (fun a b -> System.String.CompareOrdinal(a, b))
