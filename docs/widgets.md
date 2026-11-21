# Widgets

The widgets panel (the Widgets button on the taskbar) is VDE's own, in the spirit of the Windows 7 gadgets. It needs no account and
no Microsoft service. Everything in it either comes from this computer or from open data you choose.

## What is built in

| Card | Where it gets its data |
|---|---|
| Weather | [Open-Meteo](https://open-meteo.com), open data with no key. Only the city name you typed is sent. |
| Photos (On this day) | Your Pictures folder: files dated today in an earlier year |
| System | This machine: CPU and memory |
| Top stories | RSS or Atom feeds whose addresses you add (Settings > Widgets) |

The panel also has a web search box (DuckDuckGo). Cards are switched on and off in Settings > Widgets or from a card's "..." menu.

## Making a widget

There are two ways. Both are **allowed by the user** (Settings > Widgets > Community widgets) before anything runs, and a widget is
never shown advertising by the panel: the panel only draws what the widget's card says.

### 1. An app that sends a card (works for a Flatpak, so a widget can live on Flathub)

Add two keys to the app's `.desktop` file (Flatpak exports it, so VDE finds it like any app):

```
X-VDE-Widget=org.example.Tides
X-VDE-Widget-Path=/org/example/Tides
```

and, on the session bus, own that name and export `org.vde.Widget1` at that path:

```
method GetCard() -> s        the card as JSON (below)
method Activate(s action)    a button or list entry was pressed
signal Changed()             the card is different; the panel asks again
```

The shell calls the app, so the app only has to *own* the name. In a Flatpak manifest that is `--own-name=org.example.Tides`. If the
app also ships a D-Bus service file for the name (Flatpak exports those too), the panel starts the app when it asks for the card.

**The card** is data, not code. Nothing of it runs in the shell:

```json
{"title": "Tides", "rows": [
  {"type": "text", "text": "High 14:32", "size": "large"},
  {"type": "text", "text": "Low 20:41", "muted": true},
  {"type": "image", "path": "/app/share/tides/chart.png", "height": 96},
  {"type": "progress", "value": 0.4, "label": "Tide"},
  {"type": "list", "items": [{"text": "Headline", "subtitle": "Source", "action": "open:1"}]},
  {"type": "button", "text": "Refresh", "action": "refresh"}
]}
```

Row kinds: `text` (size `small`/`normal`/`large`), `image` (a file the panel can read, or an icon name), `progress` (0 to 1), `list`,
`button`. A card has at most 24 rows and 12 list entries; unknown kinds are skipped; texts are cut at 400 characters.

### 2. A component (not sandboxed)

Implement `Vde.Components.IWidgetProvider` (`Title`, `IconNames`, `CreateContent()` returning an Avalonia control), register the class
with RegSvrVDE in category `ComponentCategories.Widget`. The registration must be trusted and the user must allow it, as for launcher
search providers.

## Where the choices are kept

`HKCU\Software\Vde\Widgets` (city, units, feeds), `...\Widgets\Show` (one switch per card) and `...\Widgets\Community\<id>`
(`Title`, and `Allowed`, which only the user's own hive can hold).
