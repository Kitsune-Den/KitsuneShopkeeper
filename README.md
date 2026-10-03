# Kitsune Shopkeeper

Auto-pricing and shop quality-of-life for TCG Card Shop Simulator. Written from
scratch against the game's own API, so it carries no third-party mod code and can
be shared freely.

Formerly **Kitsune Pricer**. The files keep that name (`KitsunePricer.dll`,
`gg.goodtimes.kitsunepricer.cfg`), so upgrading is a straight overwrite and your
settings carry over.

Built and load-tested against game build **22936874** (Steam, 24 Apr 2026),
Unity 2021.3.38, BepInEx 5.4.23.5.

## What it does

Prices your stock off the day's market value, with a markup you choose and
rounding to a clean increment.

- Reprices everything on the shop floor when a new day starts
- Prices a slot the moment stock is placed into it, **including stock placed by employees**
- Hotkeys to price everything, cards only, or items only
- Separate markup for cards, graded cards, items and bulk boxes
- Optional high-value tier, so chase cards can run a thinner margin than bulk singles
- Round up / round to nearest / no rounding, with a never-below-market guard

Plus some quality-of-life extras:

- **Auto lights** — switches the shop lights on at a configured hour (5pm by default).
  Edge triggered, so flipping the switch yourself is respected rather than undone.
- **Trade guard** — blocks the first accept when you offer a customer far more than
  their card is worth. An extra zero in the offer box is otherwise silent and
  expensive. Press accept again to push a deliberate overpay through.
- **No smelly customers** (off by default) — customers never arrive smelly, and any
  already smelly in a loaded save are cleared. Toggle it live; switching it on
  mid-day clears whoever is smelly right then. Cleared customers do not count
  towards the deodorant achievements.
- **Reduced wages** (off by default) — pay employees a percentage of their normal
  wage, down to 0% for free staff. Applies to wages billed from the next day end;
  bills already owed are unchanged. The hire screen shows the reduced rate.

Press **F8** in game for a settings panel covering every option below. Changes
save to the config file straight away and take effect live.

## Why it exists

Three specific shortcomings in the existing pricing mods:

**1. Employee-placed stock was never priced.**
The other mods hook input events such as `OnCardCompartmentMouseUp`. That is the
*player's* mouse, so anything an employee stocks stays unpriced until you notice
and press a hotkey. Kitsune Shopkeeper patches `InteractableCardCompartment.SetCardOnShelf`
and `ShelfCompartment.AddItem` / `SpawnItem` instead. Those live on the compartment
itself and run no matter who filled it.

**2. Newer bulk boxes were priced as ordinary items.**
`EItemType` gained the graded bulk boxes (`BulkBox_*GradeHG/MG/PG`, and later
`...GradeUG`) long after the original two. Anything that classifies by a fixed set
of enum members misses the later additions, which is why high-value green boxes
were coming out at item markup. Classification here is by name prefix at runtime,
so new box types are picked up automatically.

**3. Rounding could quietly undercut market.**
Rounding a marked-up price to a coarse increment can land below market and eat the
whole margin. `NeverBelowMarket` (on by default) pushes back up to the first
increment at or above market.

## Design notes

The game's roadmap includes more product types, more shelf and table types, and
more TCG types. Everything here is written to degrade safely when it meets content
it does not recognise:

- Card prices go through `CPlayerData.GetCardMarketPrice(CardData)` rather than
  switching on expansion type, so new expansions work with no code change
- Unknown or zero-market-price stock is **skipped**, never priced at a guess
- Bulk box detection is prefix-based, not a hardcoded list
- Currency conversion is mirrored from the game's own rounding, so non-USD saves
  do not end up with unsellable fractional prices

### Things worth knowing about this game's modding environment

**This game destroys the BepInEx manager GameObject shortly after startup**, taking
the `BaseUnityPlugin` component with it. Unity's overloaded `==` then reports that
destroyed component as `null`, so any `if (Plugin.Instance == null) return;` guard
turns its feature off permanently, while the plugin still logs a clean load and
Harmony still reports its patches applied. Config lives in `static` fields here
(`ConfigEntry` is a plain object and survives) and gating uses a `static bool Ready`.
Never gate on a plugin-instance null check.

**That destruction also runs the plugin's `OnDestroy()`, so never unpatch there.**
Up to 0.2.4, `OnDestroy` called `UnpatchSelf()`, which removed every Harmony patch
about a second after `patches applied: N/N` was logged. Placement pricing and the
trade guard looked applied and never once ran; only the sweep, the day-start event,
hotkeys and lights (none of which need a patch) worked. Process exit cleans up
patches on its own.

**A plugin-owned GameObject will not survive either.** Ours is destroyed outright,
and its respawned replacement gets *disabled* once the main scene loads, with no
`OnDestroy` and no error. The reliable approach is attaching the component to a
GameObject the game itself owns and must keep running:
`CSingleton<LightManager>.Instance.gameObject`.

**Never touch `CSingleton<T>.Instance` before that manager exists in the scene.**
This is the one that will cost you a day of debugging. The getter lazily calls
`FindObjectOfType<T>()` and, finding nothing (on the title screen, say),
**manufactures a phantom** `GameObject` with the component attached, marks it
`DontDestroyOnLoad`, and caches it in a static field for the rest of the process.
The game's HUD clock is literally
`m_TimeText.text = CSingleton<LightManager>.Instance.m_TimeString`, so a phantom
created early blanks the in-game clock and breaks "press to start next day",
stranding the save at the end of a day.

**This can destroy player purchases, so treat it as a data-loss bug.** Buying
furniture or a shelf spawns its package box immediately through `ShelfManager` -
there is no delivery queue and no overnight wait. With a phantom `ShelfManager`
cached, that box parents to a null group: the money is charged, the box never
appears, and nothing is queued to recover. It shipped in 0.1.x because the 15
second safety sweep called `ShelfManager.GetShelfList()` on the title screen
whenever the player lingered there longer than the sweep interval, which made it
look random and hit slow-loading modded installs hardest.

Everything that reads shelf data is now gated behind `GameScene.Ready`.

The giveaway is two managers where there should be one:

```
found 2 LightManagers: DontDestroyOnLoad@08:00 (disabled), Start@08:00
```

Use `Object.FindObjectOfType<T>()`, which returns null harmlessly, and avoid the
game's static helpers (`LightManager.GetTimeHour()` and friends) since they all
resolve through that singleton.

**Never patch `LightManager.Update`.** It looks like the ideal per-frame driver and
it is not: patching it removes the clock from the HUD and stops "press to start next
day" from responding, stranding the save at the end of a day. The patch reports as
applied while the postfix never fires once, so the wrapper is failing and taking
`EvaluateTimeClock` and the day transition down with it. If the in-game clock
disappears from the HUD, this is why.

**Read the clock from the manager that is actually running.** Two LightManagers
exist after a save loads: a disabled one under `DontDestroyOnLoad` frozen at 08:00,
and the live one in the `Start` scene. The game's own static helpers
(`GetTimeHour()` and friends) go through `CSingleton<LightManager>.Instance` and can
return the frozen one. `GameClock` picks whichever manager's clock is furthest along
and re-evaluates on every read; caching the choice fails, because before the shop
opens they all read 08:00 and the pick is arbitrary.

Because of all this, pricing does not rely on any single mechanism. A periodic
sweep prices anything unpriced, so a missed hook or event costs at most one interval
rather than leaving stock unsellable. Re-attachment happens on scene load *and* on
day rollover, both engine-driven callbacks, because a dead update loop cannot heal
itself.

**`HasSetPrice()` reports the price tag, not the price.** It is identical on
`ShelfCompartment` and `InteractableCardCompartment`: both walk
`m_InteractablePriceTagList` and ask each tag whether it is marked set. Those tag
flags reset when a save loads, while the underlying prices in `CPlayerData` persist.

The practical effect is that after every load the whole shop looks unpriced for one
sweep, gets repriced against that day's market, and then goes quiet (setting a price
raises `CEventPlayer_ItemPriceChanged`, which marks the tags again). That is usually
what you want. The side effect worth knowing: **a price you set by hand is
overwritten on the next load**, because it is indistinguishable from unpriced stock
once the tag flags are gone.



**The BepInEx manager object gets no Unity update loop.** `Awake()` runs, but
`Start()` and `Update()` on a `BaseUnityPlugin` never fire. Anything frame-driven
has to live on its own `GameObject` (see `Runner.cs`). Listener binding therefore
happens in `Awake()`.

**`PatchAll(typeof(T))` with per-method `[HarmonyPatch]` attributes can silently
register nothing.** No error, no exception, plugin loads looking healthy, hooks
simply do not exist. Patches here are applied explicitly by method lookup and
report `patches applied: N/4` at startup, with `PATCH MISS:` if a target is gone.
Never trust a clean load as evidence that patches took.

**BepInEx's disk log buffers everything after chainloader startup.** Messages
logged from the Unity loop can be lost entirely if the process does not exit
cleanly, which makes the log misleading during development. `Plugin.Diag()` writes
unbuffered to `BepInEx/plugins/KitsunePricer.diag.log` for lifecycle tracing.

Also note the game's launcher exits immediately and relaunches through Steam as a
separate process. During testing, target the surviving `Card Shop Simulator`
process, not the one you spawned.

## Building

Requires the .NET SDK. The csproj references the game and BepInEx assemblies
directly; adjust `GameDir` if your install is elsewhere.

```
dotnet build -c Release
```

Then copy `bin/Release/KitsunePricer.dll` into `BepInEx/plugins/`.

## Configuration

Generated at `BepInEx/config/gg.goodtimes.kitsunepricer.cfg` on first run.
Everything here can also be changed in game from the F8 settings panel.

| Section | Setting | Default |
|---|---|---|
| Triggers | `AutoPriceOnNewDay` | true |
| Triggers | `PriceOnPlacement` | true |
| Triggers | `RepriceAlreadyPriced` | true |
| Markup | `CardMarkup` | 15 |
| Markup | `GradedCardMarkup` | 15 |
| Markup | `ItemMarkup` | 10 |
| Markup | `BulkBoxMarkup` | 5 |
| Markup | `HighValueTierEnabled` | false |
| Markup | `HighValueThreshold` | 100 |
| Markup | `HighValueMarkup` | 10 |
| Rounding | `RoundingMode` | Up |
| Rounding | `CardRoundTo` | 0.5 |
| Rounding | `ItemRoundTo` | 0.5 |
| Rounding | `NeverBelowMarket` | true |
| Rounding | `AbsoluteMinPrice` | 0.5 |
| Hotkeys | `PriceAll` / `PriceItems` / `PriceCards` | F5 / F6 / F7 |
| Hotkeys | `ConfigPanel` | F8 |
| TradeGuard | `TradeGuardEnabled` | true |
| TradeGuard | `TradeGuardMultiplier` | 3 |
| TradeGuard | `TradeGuardAbsolute` | 0 (off) |
| Lights | `AutoLights` | true |
| Lights | `LightsOnHour` | 17 |
| Lights | `LightsOffHour` | -1 (never) |
| Customers | `NoSmellyCustomers` | false |
| Workers | `ReducedWages` | false |
| Workers | `WagePercent` | 50 |

Avoid binding hotkeys to numpad keys. Typing numbers into a trade offer will
otherwise trigger a repricing run.

## Status

**Working, verified across a full in-game day.** Clock tracked 08:00 through 21:00
and rolled over cleanly, with no errors in the trace.

Pricing, at market + 20% rounded up to the dollar:

```
card: market 20.53 -> 25.00 (markup 20%)
card: market 61.82 -> 75.00 (markup 20%)
BulkBox_TetramonBaseGradePG: market 238.63 -> 251.00 (markup 5%)
```

That last line is the point of the classifier: graded bulk boxes take bulk markup
rather than being mis-priced as ordinary items.

Auto lights, firing on the hour:

```
lights: hour 16 -> 17
lights on at hour 17
```

The safety sweep also picks up cards as they are stocked, a slot at a time, without
any hotkey or day rollover involved.

**Not yet exercised: the trade guard.** It patches cleanly and is registered, but no
overpriced offer has been made to trigger it in play.
