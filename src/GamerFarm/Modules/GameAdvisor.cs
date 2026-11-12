using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Which games buying would buy now, read only: the wallet balance, the day's and the week's limits, and every game it
/// can find with a trading-card set - priced at the store, ranked by what its cards are worth back, and capped by the
/// settings. Nothing here spends anything; Core/GameCheckout.cs acts on this plan.
/// </summary>
/// <remarks>
/// Prices for cards are only ever the ones already held (<see cref="MarketQuotes.Of"/> and
/// <see cref="SparePrices.Cached"/>): a look never asks the market. A game whose cards aren't priced ranks low rather
/// than being skipped. The card maths is an estimate - half a set's cards drop, at what selling one pays after Steam's
/// fee - and is only used to order the games; the caps decide what may be spent.
/// </remarks>
public static class GameAdvisor {
	/// <summary>One game it looked at: what it costs, what its cards are worth back, and why it's out when it is.</summary>
	/// <param name="WorstValueCents">1.9.x: in levels mode, the worst the drops can be worth - the cheapest card every time,
	/// the same floor the booster option uses. 0 when no card is priced.</param>
	public sealed record GameCandidate(uint App, string Name, long PriceCents, int Currency, long DropValueCents, long BoosterValueCents, int Points, string? Skip,
		long WorstValueCents = 0, long PointsValueCents = 0, IReadOnlyList<uint>? BundleApps = null, long CardCostCents = 0);

	/// <summary>What buying games would do now, within its settings - and, when nothing, why.</summary>
	public sealed record Plan(int Currency, long Wallet, long Room, long DayLimit, long WeekLimit, long DaySpent, long WeekSpent, long Keep,
		IReadOnlyList<GameCandidate> Candidates, IReadOnlyList<GameCandidate> Skipped, string Problem);

	/// <summary>Store lookups one plan makes at most - it answers in about half a minute.</summary>
	public const int MaxLookups = 20;

	/// <summary>How fresh a card's price has to be to rank a game by it.</summary>
	public static readonly TimeSpan CardFresh = TimeSpan.FromDays(3);

	/// <summary>How fresh a game's US-dollar shortlist and a sack's price have to be to be used.</summary>
	public static readonly TimeSpan ShortlistFresh = TimeSpan.FromDays(2);

	/// <summary>The fewest sales a card's price needs behind it to be believed.</summary>
	public const int MinSales = 3;

	/// <summary>Games whose store pages are read for bundles, and bundles weighed, in one plan - about half a minute.</summary>
	public const int MaxBundleApps = 5, MaxBundles = 6;

	/// <summary>
	/// One account's plan: its wallet read the way buying cards reads it, its library for what it owns, the set finder for
	/// games with a known card set, and the Steam store for prices. Read only.
	/// </summary>
	public static async Task<Plan> ForAccountAsync(Bot bot, CancellationToken ct, DateTime? nowUtc = null, IGameStore? store = null) {
		// The limits go by Steam's clock, as buying cards' do.
		DateTime now = nowUtc ?? DateTimeOffset.FromUnixTimeSeconds(Confirmations.SteamTimeNow).UtcDateTime;
		SteamCardMarket market = new(bot);

		if (await market.WalletAsync(ct).ConfigureAwait(false) is not { } wallet) {
			return new Plan(0, 0, 0, BuyPlan.Limit(bot.Cfg.BuyGamesDailyLimit), BuyPlan.Limit(bot.Cfg.BuyGamesWeeklyLimit), 0, 0,
				Keep(bot.Cfg), [], [], "Steam didn't say what's in its wallet");
		}

		long levelCents = bot.Cfg.SpendPoints ? GemsChoice.CostsOf(bot.Name, wallet.Currency, now)?.FirstOrDefault() ?? 0 : 0;

		// A multi-game package grants apps the library may not list yet (or a soundtrack it never will): treat everything
		// the ledger says was in a bought package as owned, or the advisor recommends the same pack again.
		HashSet<uint> packed = [.. BuyGamesLedger.Of(bot.Name).All
			.Where(static e => !e.Pending && (e.Apps != null))
			.SelectMany(static e => e.Apps!)];

		return await MakeAsync(bot.Cfg, wallet.Currency, wallet.Balance, BuyGamesLedger.Of(bot.Name), store ?? new SteamGameStore(bot),
			app => (bot.Library.Find(app) is not null) || packed.Contains(app), SetFinder.Shared.Known(now), now, ct, levelCents).ConfigureAwait(false);
	}

	/// <summary>"Always keep in the wallet" for games, in cents (0: off).</summary>
	public static long Keep(BotConfig cfg) => cfg.BuyGamesKeepInWallet > 0 ? BuyPlan.Cents(cfg.BuyGamesKeepInWallet) : 0;

	/// <summary>
	/// Steam Points a purchase earns, about: Steam gives 100 points for every $1.00 spent, and a price in another
	/// currency is converted to dollars at its purchase-time rate. <paramref name="rate"/> is the wallet's money per
	/// dollar (<see cref="WalletRate"/>); null - no rate learned yet - leaves the price as it is. A display-only
	/// estimate: it never decides a purchase. Tax treatment is Steam's own and isn't modelled here.
	/// </summary>
	public static int Points(long priceCents, double? rate) {
		double usd = (rate is double r) && (r > 0) ? priceCents / r : priceCents;

		return (int) Math.Clamp(Math.Floor(usd), 0, int.MaxValue);
	}

	/// <summary>Every Points Shop badge level costs this many points, whatever badge it is.</summary>
	public const int PointsPerLevel = 1000;

	/// <summary>
	/// The XP one Points Shop badge level gives: the same 100 XP as one crafted badge. A Steam profile level costs 100
	/// XP only at low levels (200 for the next ten, then 300, and so on), so this is badge progress, not a profile level.
	/// </summary>
	public const int XpPerLevel = PointsShop.XpPerLevel;

	/// <summary>
	/// What <paramref name="points"/> are worth in badge levels: every 1,000 points buy one level (100 XP, the same as
	/// one crafted badge), valued at <paramref name="levelCents"/> - what the cheapest next badge level costs. 1,000
	/// points are one Steam profile level only while a level costs 100 XP (the first ten); above that they are a fraction
	/// of one. 0 when that cost isn't known, so an unpriced level never makes a game look cheaper than it is.
	/// </summary>
	public static long PointsWorth(int points, long levelCents) =>
		levelCents > 0 ? (long) Math.Round(points * (decimal) levelCents / PointsPerLevel, MidpointRounding.ToZero) : 0;

	/// <summary>
	/// The plan, without touching Steam itself: <paramref name="owned"/> says whether the account already has a game,
	/// <paramref name="known"/> are the games with a known card set (the set finder's), and <paramref name="store"/> prices
	/// them. The tests use a fake store and an empty set finder; nothing here reads the network.
	/// </summary>
	public static async Task<Plan> MakeAsync(BotConfig cfg, int currency, long wallet, BuyGamesLedger ledger, IGameStore store,
		Func<uint, bool> owned, IReadOnlyList<SetFinder.KnownSet> known, DateTime nowUtc, CancellationToken ct, long levelCents = 0) {
		long dayLimit = BuyPlan.Limit(cfg.BuyGamesDailyLimit);
		long weekLimit = BuyPlan.Limit(cfg.BuyGamesWeeklyLimit);
		long keep = Keep(cfg);
		List<GameCandidate> buy = [];
		List<GameCandidate> skipped = [];

		// A spending list that can't be read: nothing is bought, and no made-up number is shown for what was spent.
		if (ledger.Unreadable) {
			return new Plan(currency, Math.Max(0, wallet), 0, dayLimit, weekLimit, 0, 0, keep, buy, skipped, ledger.UnreadableText);
		}

		long daySpent = ledger.SpentSince(nowUtc.AddDays(-1));
		long weekSpent = ledger.SpentSince(nowUtc.AddDays(-7));

		if (currency <= 0) {
			return new Plan(currency, Math.Max(0, wallet), 0, dayLimit, weekLimit, daySpent, weekSpent, keep, buy, skipped, "it has no Steam wallet to buy with");
		}

		long room = Math.Max(0, Math.Min(Math.Min(dayLimit - daySpent, weekLimit - weekSpent), wallet - keep));
		Dictionary<uint, SetFinder.KnownSet> sets = known.ToDictionary(static k => k.App);

		// What to look at: the games the owner named, then every game with a known card set - cheapest set first.
		List<uint> apps = [];
		HashSet<uint> seen = [];

		foreach (uint app in cfg.BuyGamesApps.Where(static a => a != 0)) {
			if (seen.Add(app)) {
				apps.Add(app);
			}
		}

		foreach (SetFinder.KnownSet set in known.OrderBy(static k => k.UsdCents).ThenBy(static k => k.App)) {
			if (seen.Add(set.App)) {
				apps.Add(set.App);
			}
		}

		long maxPrice = cfg.BuyGamesMaxPrice > 0 ? BuyPlan.Cents(cfg.BuyGamesMaxPrice) : 0;
		List<GameCandidate> priced = [];
		int looked = 0;

		foreach (uint app in apps) {
			// A handful of lookups answers at once: the rest wait for the next look.
			if (looked >= MaxLookups) {
				break;
			}

			looked++;
			string name = sets.TryGetValue(app, out SetFinder.KnownSet? set) ? set.Game : GameNames.Of(app);

			if (owned(app)) {
				skipped.Add(new GameCandidate(app, name, 0, currency, 0, 0, 0, "it already owns it"));

				continue;
			}

			StoreGame? game;

			try {
				game = await store.GameAsync(app, currency, ct).ConfigureAwait(false);
			} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
				throw;
			} catch (Exception e) {
				Log.Debug(new Said("store lookup for game {0} failed: {1}", app, Log.Describe(e)));
				game = null;
			}

			if (game == null) {
				skipped.Add(new GameCandidate(app, name, 0, currency, 0, 0, 0, "the store didn't answer"));

				continue;
			}

			name = game.Name.Length > 0 ? game.Name : name;

			if (game.Owned) {
				skipped.Add(new GameCandidate(app, name, 0, currency, 0, 0, 0, "it already owns it"));

				continue;
			}

			if (game.Free) {
				skipped.Add(new GameCandidate(app, name, 0, currency, 0, 0, 0, "it's free to play"));

				continue;
			}

			if (game.AgeGated) {
				skipped.Add(new GameCandidate(app, name, 0, currency, 0, 0, 0, "the store asks for an age check"));

				continue;
			}

			if (game.BundleOnly) {
				skipped.Add(new GameCandidate(app, name, 0, currency, 0, 0, 0, "it isn't sold as a game on its own (a bundle, DLC or a demo)"));

				continue;
			}

			// Steam prices regionally: a game this account's region can't buy comes back with no price and no package, not
			// in another currency. It can't be bought here.
			if (game.RegionBlocked) {
				skipped.Add(new GameCandidate(app, name, 0, currency, 0, 0, 0, "it isn't available in this account's region"));

				continue;
			}

			// A paid game of its own, unless Steam marks it "Profile features limited": those, and free games, don't add
			// to the profile's game count or its badge, so buying one wouldn't do what the owner asked for.
			if (!game.CountsForProfile) {
				skipped.Add(new GameCandidate(app, name, 0, currency, 0, 0, 0, "it doesn't count toward the profile's game count (free to play, or profile features limited)"));

				continue;
			}

			if (game.PriceCents <= 0) {
				skipped.Add(new GameCandidate(app, name, 0, currency, 0, 0, 0, "the store gave no price, so it can't be bought here"));

				continue;
			}

			// Defensive: Steam prices regionally, so a price in another currency shouldn't happen - but a wallet can only
			// be spent in its own currency, and a wrong-currency charge is worse than skipping a game.
			if ((game.PriceCents > 0) && (game.Currency != currency)) {
				skipped.Add(new GameCandidate(app, name, game.PriceCents, game.Currency, 0, 0, 0,
					game.Currency > 0 ? $"the store priced it in {MarketQuotes.Code(game.Currency)}, not the wallet's money" : "the store didn't say which money its price is in"));

				continue;
			}

			if ((maxPrice > 0) && (game.PriceCents > maxPrice)) {
				skipped.Add(new GameCandidate(app, name, game.PriceCents, currency, 0, 0, 0,
					$"it costs more than the most per game ({BuyPlan.Money(maxPrice, currency)})"));

				continue;
			}

			// "More games" with the card-drops switch on: only a game with a known trading-card set is worth buying -
			// no set, no drops, nothing to sell or craft.
			if ((cfg.BuyGamesPriority == 0) && cfg.BuyGamesOnlyCardDrops && ((set == null) || (set.Cards.Count == 0))) {
				skipped.Add(new GameCandidate(app, name, game.PriceCents, currency, 0, 0, 0,
					"it has no trading-card drops, and only games with card drops are bought"));

				continue;
			}

			(long drop, long worst, long booster) = Value(cfg, app, set, currency, nowUtc);
			int points = Points(game.PriceCents, WalletRate.ForDeciding(currency));

			// Points earned count toward badge levels when the account spends them there: all Points Shop levels cost
			// 1,000 points, and each gives 100 XP (one crafted badge), so a point is worth a thousandth of what the
			// cheapest next badge level costs. A Steam profile level costs 100 XP only at low levels, so this is badge
			// progress, not a profile level. Display and levels mode only - never money off the price, and never a reason
			// to buy a game on its own.
			long pointsValue = cfg.SpendPoints ? PointsWorth(points, levelCents) : 0;

			// "Levels": a game that pays for itself in drops (average or the worst pull) and the badge levels its points
			// buy - or one that costs less than buying the cards it drops: half its set, counted at the average card
			// (average drops) or the cheapest card three times (the worst pull, as the booster floor counts it).
			// An unpriced set can't prove either, so it waits.
			long cardCost = CardCost(set, currency, cfg.BuyGamesDropBasis == 1);
			if (cfg.BuyGamesPriority == 1) {
				long pays = (cfg.BuyGamesDropBasis == 1 ? worst : drop) + pointsValue;
				bool cardsCheaper = (cardCost > 0) && (game.PriceCents < cardCost + pointsValue);

				if ((pays <= 0) && !cardsCheaper) {
					skipped.Add(new GameCandidate(app, name, game.PriceCents, currency, drop, booster, points,
						"its cards aren't priced here, so it can't tell whether the drops would pay for it"));

					continue;
				}

				if ((game.PriceCents >= pays) && !cardsCheaper) {
					skipped.Add(new GameCandidate(app, name, game.PriceCents, currency, drop, booster, points,
						$"it costs {BuyPlan.Money(game.PriceCents, currency)}, and its cards are worth about {BuyPlan.Money(pays, currency)} back "
						+ (cfg.BuyGamesDropBasis == 1 ? "at worst" : "on average")
						+ (pointsValue > 0 ? ", with the points it earns" : "")
						+ (cardCost > 0
							? $"; buying half its cards would cost {BuyPlan.Money(cardCost, currency)}"
								+ (pointsValue > 0 ? $", and the points it earns are worth {BuyPlan.Money(pointsValue, currency)}" : "")
							: ""), worst, pointsValue));

					continue;
				}
			}

			priced.Add(new GameCandidate(app, name, game.PriceCents, currency, drop, booster, points, null, worst, pointsValue, CardCostCents: cardCost));
		}

		// Bundles: Steam only names them on a game's own store page, so the cheapest few games priced are asked for the
		// bundles they sit in. A bundle is one cart item: its drops, crafts and points are the sum of its games', and the
		// per-game cap doesn't apply to it (the day's and the week's limits still do).
		if (cfg.BuyGamesBundles && (priced.Count > 0)) {
			List<uint> ids = [];

			foreach (GameCandidate game in priced.OrderBy(static c => c.PriceCents).ThenBy(static c => c.App).Take(MaxBundleApps)) {
				IReadOnlyList<uint> found;

				try {
					found = await store.BundlesForAsync(game.App, ct).ConfigureAwait(false);
				} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
					throw;
				} catch (Exception e) {
					Log.Debug(new Said("bundle lookup for game {0} failed: {1}", game.App, Log.Describe(e)));
					continue;
				}

				foreach (uint id in found) {
					if (!ids.Contains(id)) {
						ids.Add(id);
					}
				}
			}

			foreach (uint id in ids.Take(MaxBundles)) {
				StoreGame? bundle;

				try {
					bundle = await store.BundleAsync(id, currency, ct).ConfigureAwait(false);
				} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
					throw;
				} catch (Exception e) {
					Log.Debug(new Said("bundle {0} lookup failed: {1}", id, Log.Describe(e)));
					continue;
				}

				if (bundle is not { PriceCents: > 0 } pack || pack.RegionBlocked || pack.BundleApps is not { Count: > 0 }) {
					continue;
				}

				// Something in it already owned: the bundle would pay for a game the account has.
				if (pack.BundleApps.Any(owned)) {
					continue;
				}

				long drop = 0, worst = 0, booster = 0, cardCost = 0;
				bool anySet = false;

				foreach (uint included in pack.BundleApps) {
					if (!sets.TryGetValue(included, out SetFinder.KnownSet? includedSet) || (includedSet.Cards.Count == 0)) {
						continue;
					}

					anySet = true;
					(long d, long w, long b) = Value(cfg, included, includedSet, currency, nowUtc);
					drop += d;
					worst += w;
					booster += b;
					cardCost += CardCost(includedSet, currency, cfg.BuyGamesDropBasis == 1);
				}

				if ((cfg.BuyGamesPriority == 0) && cfg.BuyGamesOnlyCardDrops && !anySet) {
					skipped.Add(new GameCandidate(id, pack.Name, pack.PriceCents, currency, 0, 0, 0,
						"none of its games has trading-card drops, and only games with card drops are bought"));

					continue;
				}

				int bundlePoints = Points(pack.PriceCents, WalletRate.ForDeciding(currency));
				long bundlePointsValue = cfg.SpendPoints ? PointsWorth(bundlePoints, levelCents) : 0;

				if (cfg.BuyGamesPriority == 1) {
					long pays = (cfg.BuyGamesDropBasis == 1 ? worst : drop) + bundlePointsValue;
					bool cardsCheaper = (cardCost > 0) && (pack.PriceCents < cardCost + bundlePointsValue);

					if ((pays <= 0) && !cardsCheaper) {
						skipped.Add(new GameCandidate(id, pack.Name, pack.PriceCents, currency, drop, booster, bundlePoints,
							"none of its games' cards is priced, so it can't tell whether they would pay for it", worst, bundlePointsValue, pack.BundleApps,
							CardCostCents: cardCost));

						continue;
					}

					if ((pack.PriceCents >= pays) && !cardsCheaper) {
						skipped.Add(new GameCandidate(id, pack.Name, pack.PriceCents, currency, drop, booster, bundlePoints,
							$"it costs {BuyPlan.Money(pack.PriceCents, currency)}, and its games' cards are worth about {BuyPlan.Money(pays, currency)} back "
							+ (cfg.BuyGamesDropBasis == 1 ? "at worst" : "on average")
							+ (bundlePointsValue > 0 ? ", with the points it earns" : "")
							+ (cardCost > 0
								? $"; buying half its games' cards would cost {BuyPlan.Money(cardCost, currency)}"
									+ (bundlePointsValue > 0 ? $", and the points it earns are worth {BuyPlan.Money(bundlePointsValue, currency)}" : "")
								: ""), worst, bundlePointsValue, pack.BundleApps,
							CardCostCents: cardCost));

						continue;
					}
				}

				priced.Add(new GameCandidate(id, pack.Name, pack.PriceCents, currency, drop, booster, bundlePoints, null, worst, bundlePointsValue, pack.BundleApps,
					CardCostCents: cardCost));
			}
		}

		// "More games" buys the cheapest first, so the money buys the most games. "Levels" buys the best value first - the
		// game that costs least against what its drops and the badge levels its points buy are worth; a game whose drops
		// aren't priced falls back to its half-set card cost. Then the caps decide.
		long left = room;
		IOrderedEnumerable<GameCandidate> ranked = cfg.BuyGamesPriority == 1
			? priced.OrderBy(c => c.PriceCents - Payback(cfg, c)).ThenBy(static c => c.PriceCents - c.CardCostCents)
			: priced.OrderBy(static c => c.PriceCents);

		foreach (GameCandidate c in ranked.ThenBy(static c => c.PriceCents).ThenBy(static c => c.App)) {
			// A bundle and its games are never bought twice: whichever ranks first wins, the other waits for a later look.
			if ((c.BundleApps is { Count: > 0 } included) && included.Any(app => buy.Any(g => g.App == app))) {
				skipped.Add(c with { Skip = "its games are already being bought on their own" });

				continue;
			}

			if (buy.Any(g => g.BundleApps?.Contains(c.App) == true)) {
				skipped.Add(c with { Skip = "it's already in a bundle being bought" });

				continue;
			}

			if (c.PriceCents > left) {
				skipped.Add(c with { Skip = $"it costs {BuyPlan.Money(c.PriceCents, currency)} and {BuyPlan.Money(left, currency)} is what may be spent now" });

				continue;
			}

			buy.Add(c);
			left -= c.PriceCents;
		}

		return new Plan(currency, Math.Max(0, wallet), room, dayLimit, weekLimit, daySpent, weekSpent, keep, buy, skipped, "");
	}

	/// <summary>
	/// What a game's cards are worth back, in the wallet's money: about half its set's cards drop, at what selling one
	/// pays after Steam's fee, from prices already held. <paramref name="Worst"/> is the floor - the cheapest card every
	/// time, the same way the booster option counts its worst pull - so "levels" mode can ask for a game cheaper than even
	/// that. A booster pack it could craft is one craft a day - not a free pack - so its figure is the profit: the sealed
	/// pack's net after Steam's fee, less what the gems it takes would fetch as a sack. 0 when that is a loss, or when
	/// nothing is priced. All estimates, used only to rank and gate. A card with no fresh sale and no shortlist row is
	/// priced from the set finder's own card prices (a day or two old) at the learned rate, so a known set still has one.
	/// </summary>
	/// <summary>
	/// What a game gives back: its priced drops and the points it earns, or - when no card of its set is priced - what
	/// buying the cards it drops would cost, so such a game is still ordered by something real.
	/// </summary>
	private static long Payback(BotConfig cfg, GameCandidate c) {
		long pays = (cfg.BuyGamesDropBasis == 1 ? c.WorstValueCents : c.DropValueCents) + c.PointsValueCents;

		return pays > 0 ? pays : c.CardCostCents;
	}

	/// <summary>
	/// What buying the cards the game drops would cost on the market, in the wallet's money - half its set (at least one
	/// card), the half a game's drops cover. <paramref name="worst"/>: the cheapest card three times, as the booster floor
	/// counts a pull; otherwise the average of the drops counted exactly as a booster's average pull is (<see cref="BoosterMath.AverageFor"/>):
	/// the average card times the drops, plus the 1% chance one of them is a foil instead. 0 when the set or its prices
	/// aren't known.
	/// </summary>
	public static long CardCost(SetFinder.KnownSet? set, int currency, bool worst) {
		if ((set is null) || (set.Cards.Count == 0) || set.Usd.Values.Any(static usd => usd <= 0)) {
			return 0;
		}

		if (WalletRate.ForDeciding(currency) is not double rate) {
			return 0;
		}

		int drops = Math.Max(1, set.Cards.Count / 2);

		if (worst) {
			return 3L * WalletRate.ToWallet(set.Usd.Values.Min(), rate);
		}

		long[] normal = [.. set.Cards.Select(card => (long) WalletRate.ToWallet(set.Usd[card], rate))];
		SparePrices.Game? shortlist = SparePrices.Cached(set.App);
		bool shortlistFresh = (shortlist != null) && (DateTime.UtcNow - shortlist.When <= ShortlistFresh);

		long? FoilBuyer(string card) {
			string hash = SetSale.FoilHash(card);

			if (MarketQuotes.Of(hash, currency)?.Lowest is int held and > 0) {
				return held;
			}

			return shortlistFresh && (SparePrices.UsdCents(hash) is int usd and > 0) ? WalletRate.ToWallet(usd, rate) : null;
		}

		long[] foil = [.. set.Cards.Select(FoilBuyer).Where(static buyer => buyer is > 0).Select(static buyer => (long) buyer!.Value)];

		return BoosterMath.AverageFor(drops, normal, foil);
	}

	public static (long Drop, long Worst, long Booster) Value(BotConfig cfg, uint app, SetFinder.KnownSet? set, int currency, DateTime nowUtc) {
		if ((set == null) || (set.Cards.Count == 0)) {
			return (0, 0, 0);
		}

		int drops = Math.Max(1, set.Cards.Count / 2);
		SparePrices.Game? shortlist = SparePrices.Cached(app);
		bool shortlistFresh = (shortlist != null) && (nowUtc - shortlist.When <= ShortlistFresh);
		double? rate = WalletRate.ForDeciding(currency);
		long cheapest = 0;
		List<long> normal = [], foil = [];

		int Net(string hash) {
			if ((MarketQuotes.Of(hash, currency) is { Lowest: int buyer and > 0, Sales: >= MinSales } quote) && (nowUtc - quote.When <= CardFresh)) {
				return Seller.ReceiveFor(buyer);
			}

			if (shortlistFresh && (SparePrices.UsdCents(hash) is int listed and > 0) && (rate is double r)) {
				return Seller.ReceiveFor(WalletRate.ToWallet(listed, r));
			}

			// The set finder's own card prices are the last fallback: already read from the market a day or two ago, and
			// the learned rate turns them into the wallet's money - so every known set has a figure without a request.
			return (set.Usd.GetValueOrDefault(hash) is int usd and > 0) && (rate is double known)
				? Seller.ReceiveFor(WalletRate.ToWallet(usd, known))
				: 0;
		}

		foreach (string hash in set.Cards) {
			int net = Net(hash);

			if (net > 0) {
				normal.Add(net);
				cheapest = cheapest == 0 ? net : Math.Min(cheapest, net);
			}

			// A card drop is a foil as often as a booster's card is: one in a hundred, counted on the same average.
			int shiny = Net(SetSale.FoilHash(hash));

			if (shiny > 0) {
				foil.Add(shiny);
			}
		}

		long drop = BoosterMath.AverageFor(drops, normal, foil);
		long worst = cheapest > 0 ? cheapest * drops : 0;
		long booster = 0;

		if (cfg.BoosterMake != 0) {
			int dumpPct = SpareRules.From(cfg).DumpPct;
			booster = GemsChoice.PackProfit(app, set.Game, set.Cards.Count, currency, dumpPct, nowUtc) is long profit and > 0 ? profit : 0;
		}

		return (drop, worst, booster);
	}
}
