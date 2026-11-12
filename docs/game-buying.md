# Buying games

nocat.farm can buy Steam games with the account's **Steam wallet balance only** - never a card, PayPal, a wallet
code or a top-up - and only when the owner has said yes. Buying is **off by default** on every account.

Two reasons to buy:

- **More games**: raise the profile's game count and its "games owned" badge, cheapest first.
- **Levels**: only a game whose card drops are worth more than it costs, so the money comes back in cards.

Steam Points earned by the purchase are counted toward badge levels in levels mode when "Spend Steam Points on levels"
is on: 1,000 points buy one badge level of 100 XP (one Steam profile level only at low levels, where a level costs 100 XP; above them a level costs 200, then 300, and so on), so the points are valued at what the cheapest next badge level costs.

## Turning it on

1. `games preview <account>` shows what it would buy, for how much, what the cards are worth back, and why anything was
   skipped. Nothing is bought or reserved.
2. `games <account> on yes` gives the go-ahead. The go-ahead is kept in `config/state/buygame-<account>.json`, not in the
   settings file, so a settings copy or a backup never carries it.
3. `games do <account>` buys now. It also needs "Buy games without asking".
4. Once "Buy games without asking" is on, the automatic look runs every "Look for games to buy every" hours (24 by
   default), with the same rules as the manual command.

`games off <account>` stops buying at once (the go-ahead is taken back).

## Settings

All under the account's **Buying cards** section.

| Setting | Default | What it does |
|---|---|---|
| Buy games with the Steam wallet | off | The feature's switch; needs the preview's go-ahead |
| Buy games without asking | off | Allows unattended purchases and the automatic look |
| Buy games for | more games | `more games`: cheapest first, as many as the money allows. `levels`: only games whose drops pay for them |
| The cards are counted at | average drops | In levels mode: average drops, or the worst pull (the cheapest card every time) |
| Only buy games with card drops | on | In "more games" mode, skip games with no known trading-card set |
| Look for bundles too | on | Also weigh the bundles Steam lists on a game's page - one cart item of several games |
| Look for packages too | on | Also weigh the packages (Steam's "subs") a game is sold through, and the packs in the store's search, its discounted rows cheapest first - one cart item of several games |
| Also consider these games | empty | Extra app ids besides the games with a known card set |
| Spend at most a day on games | 5.00 | Rolling 24-hour limit, in the wallet's currency; below 0 = no limit |
| Spend at most a week on games | 20.00 | Rolling 7-day limit; below 0 = no limit |
| Always keep in the wallet for games | 0 | Buying never takes the wallet below this |
| Most to pay for one game | 1.00 | Per-game cap; 0 = no cap (the day/week limits still apply) |
| Look for games to buy every | 24h | The automatic look's interval |
| Games per look, human mode | 1 | Human-mode pacing; a robot buys every game that fits |
| Shortest/longest wait between game looks, human mode | 30/90 min | Human-mode spacing |

## What counts as a game

Only a **paid, standalone game** is considered:

- Free-to-play games are skipped: they don't add to the profile's game count or badge.
- DLC, demos, software and videos are skipped: they are not games of their own and don't add to the game count.
- Bundles and multi-game packages are not games either, but they are weighed as a pack of the games inside them (see
  below).
- Age-gated games are skipped: the store asks for an age check.
- Games not sold in the account's region are skipped: the store returns no price or package for them. Steam prices
  regionally, so a game is either in the wallet's currency or not available at all; a price in another currency is only
  a defensive check (a wallet can't be spent in it).
- Games with Steam's **"Profile features limited"** notice (or "Steam is Learning About This Game") are skipped. Steam's
  appdetails answer doesn't expose that flag, so the store page is read once a week per app before a purchase; if the
  page can't be read, the paid/free/bundle checks are all that can be known and the game is allowed.

## Free DLC after a purchase

When "Claim free games" is set to **games and all their free DLC**, a game that was just bought also has the free DLC
Steam lists for it claimed straight away - a free soundtrack, supporter pack or other add-on that costs nothing. The
same mode accepts permanently free DLC seen in Steam's free-games feed, which the other modes leave alone because they
only take temporary giveaways. It never claims a paid DLC, and a failure there never affects the purchase.

## Bundles and packages

Steam sells things two ways that hold several games, and both count as one cart item:

- a **bundle** (`bundleid`), named on the store page of a game inside it;
- a **package** or "sub" (`packageid`), which is what a buy button adds - a game's own package holds one app, but a
  multi-game pack holds several.

Steam only names a bundle on the store page of a game inside it, so the advisor asks the store page of the cheapest few
games it priced for the bundles they sit in. It also asks the cheapest few games for the packages they are sold through
(`packagedetails`), and keeps any package holding more than one app. A pack is then weighed like one purchase:

- Packs are also read straight from the store's own search, its discounted rows cheapest first (the first two pages of
  a hundred): a pack that carries a discount of its own is listed there as a row of its own, and each row already names
  every app the pack grants, so no further read is needed. Packs costing more than what may be spent right now are
  dropped, and the most six of them are weighed per look.
- Everything its games drop, craft and earn counts together: the card drops, the booster profit, and the Steam Points
  from the pack's own price.
- The apps inside it must not already be owned - a soundtrack is fine, it just earns nothing - and its games are not
  also bought on their own in the same look: whichever ranks first is bought, the other waits.
- Everything a bought pack granted is recorded in the spending list and treated as owned from then on, so the same pack
  is never recommended again; the library can lag behind, and a soundtrack never appears in it at all.
- A bundle goes into the cart as a bundle (`bundleid`); a package goes in as its package id, which is the same call a
  single game's purchase makes.
- "Most to pay for one game" does not apply to a bundle or a package, because its price covers several apps; the day's
  and the week's limits, the wallet balance and the kept amount still do.
- "Only buy games with card drops" applies when none of its apps has a known card set.
- At the same price a pack is bought before a single game, because it holds more.

## How the value is estimated

Card drops are an estimate, used only to rank and to allow a levels-mode purchase; the caps decide what may be spent.

- A game drops about **half its set's cards**. Each card is valued at what selling one pays after Steam's fee, from
  prices already held (no market request is made by the advisor).
- **Average drops**: counted exactly like a booster's average pull, just with more cards - the mean of the priced cards
  times the drops, plus a 1% chance one of the drops is a foil instead (`BoosterMath.AverageFor`). A foil with no price
  held counts as no dearer than its normal card.
- **Worst pull**: the cheapest priced card, every time - the same floor the booster option uses.
- In levels mode a game is also taken when it costs less than buying half its cards, and Steam Points count on that
  path too: the game's price is compared with the half-set cost plus what its points are worth.
- **Booster profit** is counted too: the sealed pack's net after Steam's fee, minus what the gems it takes would fetch
  as a sack. It is a real cost, not a free pack.
- **Steam Points**: 100 points per USD spent (the local price converted at the learned rate), 1,000 points per badge level (100 XP - one Steam profile level only at low levels, where a level costs 100 XP, and a fraction above them)
  level. Included in levels mode only while "Spend Steam Points on levels" is on.

A game whose cards aren't priced can still be bought in "more games" mode; in levels mode it waits, because it can't be
proven to pay.

## Paying

The checkout is wallet-only, using Steam's own flow:

1. Add the game's package, the bundle itself, or a pack's package id to the account cart and set the account's store
   country.
2. Start the transaction with `PaymentMethod=steamaccount` (Steam's name for the wallet balance).
3. Read the final price and check it: same currency, no external payment URL, no review, within the per-game cap and
   the wallet balance less what is kept.
4. Finalise, then require the wallet balance to fall by the whole final total before calling it bought.

Anything else stops the look and says why: an external payment page is never followed, another payment method is never
chosen, no funds are added, and a 3-D Secure/bank challenge or a purchase review is left for the owner.

Success is judged by the wallet balance, not by the page. A purchase whose outcome is unknown (sent but not confirmed)
stays in the spending list as pending and keeps counting against the daily and weekly limits until those windows pass.

## Limits and safety

- One look at a time per account, for the command and the automatic loop together.
- Paused accounts, the Steam market hold and ScamWatch stop a look, before and between purchases.
- The day's limit, the week's limit, the kept balance and the per-game cap are re-checked before every purchase; the
  wallet is never taken below the kept amount.
- A failure stops the look rather than trying the next game on a guess.
- There is no refund automation: a bought game stays bought. Cards drop only after the game is played past Steam's
  refund window (see "Hours until card drops"), and a refund can take the cards back.

## Commands

```
games [account|all]              what each account is doing
games preview [account|all]      read-only: what it would buy and why
games on <account> yes           give the go-ahead
games off <account>              stop buying
games do <account>               buy now (needs "Buy games without asking")
```
