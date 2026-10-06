// Port of rust/crates/views/src/helpers/translations.rs
/// `TranslationsHelper`: the language popups beside form fields.
module Campfire.Views.Helpers.Translations

open System
open Campfire.Views
open Campfire.Views.Helpers.Tag

/// `translations_for(key)`.
let translationsFor (w: Out) (key: string) : unit =
    let entries =
        match TranslationsTable.translations |> Array.tryFind (fun (name, _) -> name = key) with
        | Some(_, entries) -> entries
        | None -> failwith $"unknown translation key {key}"
    contentTagBlock w "dl" (attrs().Class("language-list")) (fun w ->
        for (language, translation) in entries do
            contentTagText w "dt" (attrs ()) language
            contentTagText w "dd" (attrs().Class("margin-none")) translation)

/// `translation_button(key)`.
let translationButton (w: Out) (ctx: ViewContext) (key: string) : unit =
    let details =
        attrs()
            .Class("position-relative")
            .Data("controller", "popup")
            .Data("action", "keydown.esc->popup#close toggle->popup#toggle click@document->popup#closeOnClickOutside")
            .Data("popup_orientation_top_class", "popup-orientation-top")
    contentTagBlock w "details" details (fun w ->
        contentTagBlock w "summary" (attrs().Class("btn").Tabindex(-1)) (fun w ->
            Assets.imageTag w ctx "globe.svg" (attrs().Size(20).AriaHidden().Class("color-icon"))
            contentTagText w "span" (attrs().Class("for-screen-reader")) "Translate")
        contentTagBlock w "div" (attrs().Class("language-list-menu shadow").Data("popup_target", "menu")) (fun w ->
            translationsFor w key))
