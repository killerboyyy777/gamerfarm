using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Which games buying would buy now, read only: the wallet balance, the day's and the week's limits, and every game it
/// can find with a trading-card set - priced at the store, ranked by what its cards are worth back, and capped by the
/// settings. Nothing here spends anything; Stage 2 adds the checkout that acts on this plan.
/// </summary>
/// <remarks>
/// Prices for cards are only ever the ones already held (<see cref="MarketQuotes.Of"/> and
/// <see cref="SparePrices.Cached"/>): a look never asks the market. A game whose cards aren't priced ranks low rather
/// than being skipped. The card maths is an estimate - half a set's cards drop, at what selling one pays after Steam's
/// fee - and is only used to order the games; the caps decide what may be spent.
/// </remarks>
public static class GameAdvisor {
	/// <summary>One game it looked at: what it costs, what its cards are worth back, and why it's out when it is.</summary>
	public sealed record GameCandidate(uint App, string Name, long PriceCents, int Currency, long DropValueCents, long BoosterValueCents, int Points, string? Skip);

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

		return await MakeAsync(bot.Cfg, wallet.Currency, wallet.Balance, BuyGamesLedger.Of(bot.Name), store ?? new SteamGameStore(bot),
			app => bot.Library.Find(app) is not null, SetFinder.Shared.Known(now), now, ct).ConfigureAwait(false);
	}

	/// <summary>"Always keep in the wallet" for games, in cents (0: off).</summary>
	public static long Keep(BotConfig cfg) => cfg.BuyGamesKeepInWallet > 0 ? BuyPlan.Cents(cfg.BuyGamesKeepInWallet) : 0;

	/// <summary>
	/// The plan, without touching Steam itself: <paramref name="owned"/> says whether the account already has a game,
	/// <paramref name="known"/> are the games with a known card set (the set finder's), and <paramref name="store"/> prices
	/// them. The tests use a fake store and an empty set finder; nothing here reads the network.
	/// </summary>
	public static async Task<Plan> MakeAsync(BotConfig cfg, int currency, long wallet, BuyGamesLedger ledger, IGameStore store,
		Func<uint, bool> owned, IReadOnlyList<SetFinder.KnownSet> known, DateTime nowUtc, CancellationToken ct) {
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

			if (game.PriceCents > 0 && (game.Currency != currency)) {
				skipped.Add(new GameCandidate(app, name, game.PriceCents, game.Currency, 0, 0, 0,
					game.Currency > 0 ? $"the store priced it in {MarketQuotes.Code(game.Currency)}, not the wallet's money" : "the store didn't say which money its price is in"));

				continue;
			}

			if (game.PriceCents <= 0) {
				skipped.Add(new GameCandidate(app, name, 0, currency, 0, 0, 0, "the store gave no price"));

				continue;
			}

			if ((maxPrice > 0) && (game.PriceCents > maxPrice)) {
				skipped.Add(new GameCandidate(app, name, game.PriceCents, currency, 0, 0, 0,
					$"it costs more than the most per game ({BuyPlan.Money(maxPrice, currency)})"));

				continue;
			}

			(long drop, long booster) = Value(cfg, app, set, currency, nowUtc);
			int points = (int) Math.Min(int.MaxValue, (game.PriceCents / 100) * 100);
			priced.Add(new GameCandidate(app, name, game.PriceCents, currency, drop, booster, points, null));
		}

		// The best value first: what it costs less what its cards are worth back. Then the caps decide.
		long left = room;

		foreach (GameCandidate c in priced.OrderBy(static c => c.PriceCents - c.DropValueCents).ThenBy(static c => c.PriceCents).ThenBy(static c => c.App)) {
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
	/// pays after Steam's fee, from prices already held. Booster packs it could make are one a day for the game, at what
	/// their gems would pay as a sack - a rank bonus only, and 0 unless the account makes packs. Both 0 when nothing is
	/// priced: the game still ranks, just low.
	/// </summary>
	public static (long Drop, long Booster) Value(BotConfig cfg, uint app, SetFinder.KnownSet? set, int currency, DateTime nowUtc) {
		if ((set == null) || (set.Cards.Count == 0)) {
			return (0, 0);
		}

		int drops = Math.Max(1, set.Cards.Count / 2);
		SparePrices.Game? shortlist = SparePrices.Cached(app);
		bool shortlistFresh = (shortlist != null) && (nowUtc - shortlist.When <= ShortlistFresh);
		double? rate = WalletRate.ForDeciding(currency);
		long total = 0;
		int priced = 0;

		foreach (string hash in set.Cards) {
			int net = 0;

			if ((MarketQuotes.Of(hash, currency) is { Lowest: int buyer and > 0, Sales: >= MinSales } quote) && (nowUtc - quote.When <= CardFresh)) {
				net = Seller.ReceiveFor(buyer);
			} else if (shortlistFresh && (SparePrices.UsdCents(hash) is int usd and > 0) && (rate is double r)) {
				net = Seller.ReceiveFor(WalletRate.ToWallet(usd, r));
			}

			if (net > 0) {
				total += net;
				priced++;
			}
		}

		long drop = priced > 0 ? (long) Math.Round(total * (double) drops / priced, MidpointRounding.AwayFromZero) : 0;
		long booster = 0;

		if ((cfg.BoosterMake != 0) && (SparePrices.SackPays(currency, SpareRules.From(cfg).DumpPct) is { Cents: > 0 } sack) && (nowUtc - sack.At <= CardFresh)) {
			int gems = BoosterPick.PackGems(set.Cards.Count);
			booster = gems > 0 ? (long) Math.Round(Spare.GemsWorth(gems, sack.Cents), MidpointRounding.AwayFromZero) : 0;
		}

		return (drop, booster);
	}
}
