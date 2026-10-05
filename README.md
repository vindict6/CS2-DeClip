# CS2-DeClip

A small [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) plugin that lets an admin type `!declip`
and walk straight through the player clips on whatever map is loaded. I use it for poking around maps, finding spots,
and testing stuff on a private server, but it's useful any time the invisible walls are in the way.

## Why this exists

In CS2 most player clips aren't entities you can delete. When a mapper paints a brush with `toolsplayerclip`, it gets
baked into the world collision on its own "player clip" layer. So there's nothing to remove, you have to make players
stop colliding with that layer instead.

My first version did exactly that: it stripped the player clip layer off every player's collision mask each tick. On
the client it looked like it worked, but as soon as you walked into a clip you got rubber-banded right back out.

The reason is that the server rebuilds the player's collision mask right before every movement command, and every
version of the mask it rebuilds has the player clip layer in it. The client doesn't do that. It just uses whatever mask
the server sends it. So the client predicted you walking through, the server still traced against the clip, and you got
snapped back.

So DeClip now fixes it where the mask actually gets built. When you run `!declip` it finds that bit of movement code in
the running server (in memory, nothing on disk gets touched) and takes the player clip layer out of the masks it builds.
From then on the server comes up with a clip-free mask by itself and sends it to clients, both sides agree, and you
don't get snapped back.

## What it does

- Players can walk, jump and fall through `toolsplayerclip` / `toolsclip` brushes.
- Removes `func_clip_vphysics` entities too, and removes them again after every round restart since the map spawns them
  back.
- Lasts until the map changes. Clips come back on the next map, or if the plugin gets unloaded.
- Works on both Windows and Linux servers.

## Requirements

- CS2 dedicated server with Metamod:Source
- CounterStrikeSharp **1.0.369 or newer** (the .NET 10 builds)

## Install

1. Grab the latest `CS2-DeClip-x.y.z.zip` from the [Releases](../../releases) page.
2. Extract it into your server's `game/csgo/` folder. You should end up with
   `addons/counterstrikesharp/plugins/CS2-DeClip/CS2-DeClip.dll`.
3. Restart the server or run `css_plugins load CS2-DeClip`.

There's no config file.

## Usage

Type `!declip` or `/declip` in chat, or run `css_declip` from the server console. You need the `@css/generic` flag.

Everyone gets a chat message saying who removed the clips. There's no command to put them back mid-map. Change the map
or run `css_plugins reload CS2-DeClip` and they're back.

## Good to know

- This patches the running server's code in memory. It's only a few bytes and they get put back on map change or
  unload, but it is a memory patch. If a CS2 update moves the code it looks for, `!declip` won't guess. It tells you it
  couldn't find it (in the reply and in the server log) and doesn't change anything. If that happens, open an issue.
- It affects everyone on the server, not just the admin who ran it.
- It only covers player movement. Some other game code does its own traces with hard-coded masks, so not everything in
  the game will ignore clips.
- Grenade clips and other clip types aren't touched, only player clips.

## Building

```bash
dotnet build -c Release
# output: bin/Release/net10.0/CS2-DeClip.dll
```

You need the .NET 10 SDK.

## Releasing

Releases are built by GitHub Actions, but only when you start it by hand. Go to **Actions → Build and Release → Run
workflow** and type a version, or leave it blank to use the `ModuleVersion` from `DeClipPlugin.cs`. It builds the
plugin, zips it up in the `addons/counterstrikesharp/plugins/CS2-DeClip/` layout, tags `vX.Y.Z`, and publishes the
release.

## License

MIT
