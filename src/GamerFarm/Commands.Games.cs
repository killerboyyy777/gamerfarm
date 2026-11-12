using System.Globalization;
using NocatFarm.Config;
using NocatFarm.Core;
using NocatFarm.Modules;

namespace NocatFarm;

/// <summary>
/// 'games': buying Steam games with the wallet balance - Modules/GameAdvisor.cs decides what, Core/GameCheckout.cs pays
/// for it. It spends real money, so it previews first, and an account buys nothing until its preview is accepted
/// ('games &lt;account&gt; on yes') and "Buy games without asking" is on. 'games preview' is always read only.
/// </summary>
public static partial class Commands {
	internal const string GamesUsage = "games [account|all] | games preview [account|all] | games probe <account> <app> | games on <account> yes | games off <account> | games do <account>";

	/// <summary>What a 'games' line asks for.</summary>
	public enum GamesAsk { List, Preview, Probe, On, Off, Do, Bad }

	/// <summary>
	/// Read a 'games' line's words, in any order - from Steam chat the account's name comes straight after 'games'.
	/// preview, on, off and do ask; yes is the owner's word for turning it on. Anything else is the account (or 'all').
	/// </summary>
	public static (GamesAsk Ask, string? Who, bool Yes) ParseGames(string[] args) {
		GamesAsk ask = GamesAsk.List;
		bool all = false, yes = false;
		string? who = null;

		foreach (string word in args) {
			GamesAsk? said = word.ToLowerInvariant() switch {
				"preview" => GamesAsk.Preview,
				"probe" => GamesAsk.Probe,
				"on" => GamesAsk.On,
				"off" => GamesAsk.Off,
				"do" or "now" => GamesAsk.Do,
				_ => null
			};

			if (said is { } a) {
				if (ask != GamesAsk.List) {
					return (GamesAsk.Bad, null, false);
				}

				ask = a;
			} else if (word.Equals("yes", StringComparison.OrdinalIgnoreCase) || word.Equals("ok", StringComparison.OrdinalIgnoreCase)) {
				yes = true;
			} else if (word.Equals("all", StringComparison.OrdinalIgnoreCase) && !all && (who == null)) {
				all = true;
			} else if ((ask == GamesAsk.Probe) && uint.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out _)) {
				// 'games probe <account> <appid>': the app id is found again by the handler.
			} else if ((who == null) && !all) {
				who = word;
			} else {
				return (GamesAsk.Bad, null, false);
			}
		}

		// Turning it on, off, probing or buying now: one account by name, never every account at once.
		if ((ask is GamesAsk.On or GamesAsk.Off or GamesAsk.Do or GamesAsk.Probe) && (all || (who == null))) {
			return (GamesAsk.Bad, null, false);
		}

		return (ask, who, yes);
	}

	/// <summary>
	/// Turning buying games on, or buying now, is the owner's word - not something a Steam-chat master gives for them.
	/// Looking and turning it off are fine from anywhere.
	/// </summary>
	internal static bool GamesNeedsOwner(string[] args) =>
		args.Any(static a => a.ToLowerInvariant() is "on" or "do" or "now" or "yes" or "ok" or "probe");

	private static async Task<string> BuyGamesAsync(BotManager mgr, string[] args) {
		(GamesAsk ask, string? who, bool yes) = ParseGames(args);

		if (ask == GamesAsk.Bad) {
			return GamesUsage;
		}

		if (ask is GamesAsk.On or GamesAsk.Off or GamesAsk.Do) {
			if (mgr.Get(who!) is not { } bot) {
				return NoSuchAccount(mgr, who!);
			}

			BuyGamesLedger ledger = BuyGamesLedger.Of(bot.Name);

			if (ask == GamesAsk.Off) {
				SetBuyGames(bot, false);

				return $"{bot.Name}: buying games is off - nothing is bought.";
			}

			if (ask == GamesAsk.Do) {
				if (!bot.Cfg.BuyGames || !ledger.Ok) {
					return $"{bot.Name}: buying games is off. 'games preview {bot.Name}' shows what it would buy; 'games on {bot.Name} yes' turns it on.";
				}

				// There is no interactive confirmation in the console: without "Buy games without asking" nothing is
				// spent, and the reply says which switch turns the unattended path on.
				if (!bot.Cfg.BuyGamesUnattended) {
					return $"{bot.Name}: buying games by itself is off, and there is no way to ask you here - nothing was bought. "
						+ $"Turn it on first:  set {bot.Name} {nameof(BotConfig.BuyGamesUnattended)} true";
				}

				return await SlowAsync(new Slow("games", "buying games", Key: "games do"), [bot],
					b => GamesBuyAsync(b, CancellationToken.None), ReplyWithin,
					cannot: static b => !b.IsOnline || !b.Web.Ready ? $"{b.Name}: not logged in" : null).ConfigureAwait(false);
			}

			// on
			if (ledger.Unreadable) {
				return $"{bot.Name}: {ledger.UnreadableText}";
			}

			if (!yes) {
				return string.Join(Environment.NewLine,
					$"{bot.Name}: that spends the Steam wallet, so it needs your word.",
					$"{bot.Name}: 'games preview {bot.Name}' shows what it would buy; then type 'games {bot.Name} on yes' to allow it.",
					GamesLimits(bot));
			}

			SetBuyGames(bot, true);

			return string.Join(Environment.NewLine,
				$"{bot.Name}: buying games is on - it may spend the Steam wallet by itself, within its limits. Only the wallet balance is ever spent: no card, no other payment method, no top-up.",
				GamesLimits(bot),
				$"{bot.Name}: 'games preview {bot.Name}' shows what it would buy; 'games do {bot.Name}' buys now (it also needs \"{Settings.FindBot(nameof(BotConfig.BuyGamesUnattended))?.Label ?? "Buy games without asking"}\" on), one look at a time; the automatic look runs every {Math.Clamp(bot.Cfg.BuyGamesCheckHours, 1, 168)}h while that is on; 'games off {bot.Name}' turns it off.");
		}

		if (ask == GamesAsk.Probe) {
			if (mgr.Get(who!) is not { } bot) {
				return NoSuchAccount(mgr, who!);
			}

			uint app = 0;

			foreach (string word in args) {
				if (uint.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out uint id) && (id != 0)) {
					app = id;

					break;
				}
			}

			if (app == 0) {
				return $"{bot.Name}: which game? 'games probe {bot.Name} <appid>' - it follows the real checkout to the final price, then cancels it.";
			}

			return await SlowAsync(new Slow("games", "checking a game's checkout without buying", Key: "games probe"), [bot],
				b => GamesProbeAsync(b, app, CancellationToken.None), ReplyWithin,
				cannot: static b => !b.IsOnline || !b.Web.Ready ? $"{b.Name}: not logged in" : null).ConfigureAwait(false);
		}

		if (Pick(mgr, who == null ? [] : [who], out string? problem) is not { } bots) {
			return problem!;
		}

		if (ask == GamesAsk.Preview) {
			return await GamesLookAsync(bots).ConfigureAwait(false);
		}

		return string.Join(Environment.NewLine, bots.Select(static b => $"{b.Name}: {GamesState(b)}"));
	}

	/// <summary>Whether and how an account buys games, in a few words.</summary>
	public static string GamesState(Bot bot) {
		if (!bot.Cfg.BuyGames) {
			return "buying games is off";
		}

		BuyGamesLedger ledger = BuyGamesLedger.Of(bot.Name);

		if (ledger.Unreadable) {
			return ledger.UnreadableText;
		}

		if (!ledger.Ok) {
			return $"buying games is waiting for its preview's go-ahead - 'games on {bot.Name} yes'";
		}

		int cur = bot.WalletCurrency == SteamKit2.ECurrencyCode.Invalid ? 0 : (int) bot.WalletCurrency;
		DateTime now = DateTimeOffset.FromUnixTimeSeconds(Confirmations.SteamTimeNow).UtcDateTime;   // the limits go by Steam's clock

		return $"buys games: {BuyPlan.Money(ledger.SpentSince(now.AddDays(-1)), cur)} of {BuyPlan.LimitSaid(BuyPlan.Limit(bot.Cfg.BuyGamesDailyLimit), cur)} today, "
			+ $"{BuyPlan.Money(ledger.SpentSince(now.AddDays(-7)), cur)} of {BuyPlan.LimitSaid(BuyPlan.Limit(bot.Cfg.BuyGamesWeeklyLimit), cur)} this week - only from the wallet balance";
	}

	/// <summary>The limits it keeps to for games, in one line.</summary>
	internal static string GamesLimits(Bot bot) {
		int cur = bot.WalletCurrency == SteamKit2.ECurrencyCode.Invalid ? 0 : (int) bot.WalletCurrency;

		string perGame = bot.Cfg.BuyGamesMaxPrice > 0
			? $"at most {BuyPlan.Money(BuyPlan.Cents(bot.Cfg.BuyGamesMaxPrice), cur)} a game"
			: "no per-game cap";
		string keep = bot.Cfg.BuyGamesKeepInWallet > 0
			? $", keeping {BuyPlan.Money(BuyPlan.Cents(bot.Cfg.BuyGamesKeepInWallet), cur)} in the wallet"
			: "";

		return $"{bot.Name}: it spends at most {BuyPlan.LimitSaid(BuyPlan.Limit(bot.Cfg.BuyGamesDailyLimit), cur)} a day and "
			+ $"{BuyPlan.LimitSaid(BuyPlan.Limit(bot.Cfg.BuyGamesWeeklyLimit), cur)} a week, {perGame}{keep}, never more than the wallet holds, and only the wallet balance.";
	}

	/// <summary>
	/// 'games do': one account's look, then its purchases - cheapest first, one at a time, and only ever from the wallet.
	/// The ledger's go-ahead, the switch, "Buy games without asking", the day's and week's limits, "keep in the wallet",
	/// "most per game", the wallet balance, and the paused/market-hold/scam-watch gates are all checked first; each
	/// attempt is written down as pending before it is sent. A failure stops the run rather than trying the next game.
	/// </summary>
	public static Task<string> GamesBuyAsync(Bot bot, CancellationToken ct) => GameBuyer.BuyNowAsync(bot, ct);

	/// <summary>
	/// 'games probe &lt;account&gt; &lt;app&gt;': the real checkout, call for call, up to its final price - then the transaction is
	/// cancelled. Nothing is bought; it exists to prove the form, country, wallet method and price on a live account.
	/// </summary>
	internal static async Task<string> GamesProbeAsync(Bot bot, uint app, CancellationToken ct) {
		if (await new SteamCardMarket(bot).WalletAsync(ct).ConfigureAwait(false) is not { } wallet) {
			return $"{bot.Name}: Steam didn't say what's in its wallet - the checkout can't be checked.";
		}

		if (wallet.Currency <= 0) {
			return $"{bot.Name}: it has no Steam wallet to check the checkout with.";
		}

		IGameStore store = GameBuyer.StoreFor(bot);

		if (await store.GameAsync(app, wallet.Currency, ct).ConfigureAwait(false) is not { } game) {
			return $"{bot.Name}: the store didn't answer about app {app}.";
		}

		if (game.PriceCents <= 0) {
			return $"{bot.Name}: {game.Name} has no price in {MarketQuotes.Code(wallet.Currency)} - free, region-blocked or only sold in a bundle.";
		}

		CheckoutProbe probe = await new GameCheckout(new BotCheckoutWeb(bot), BuyPlan.Cents(bot.Cfg.BuyGamesMaxPrice))
			.ProbeAsync(game, ct).ConfigureAwait(false);

		List<string> lines = [
			$"{bot.Name}: checkout probe for {game.Name} ({game.App}) - nothing is bought.",
			$"  store price: {BuyPlan.Money(game.PriceCents, wallet.Currency)}"
		];

		foreach (CheckoutStep step in probe.Steps) {
			lines.Add($"  {step.Name}: {step.Detail}");
		}

		lines.Add($"  {probe.Answer.Reason.Replace("dry run: ", "", StringComparison.Ordinal)}");

		return string.Join(Environment.NewLine, lines);
	}

	/// <summary>'games preview': each account's look, said plainly. Nothing is bought or reserved.</summary>
	private static Task<string> GamesLookAsync(IReadOnlyList<Bot> bots) {
		static async Task<string> Look(Bot b) {
			GameAdvisor.Plan plan = await GameAdvisor.ForAccountAsync(b, CancellationToken.None).ConfigureAwait(false);

			return GamesSaid(b, plan);
		}

		return SlowAsync(new Slow("games", "looking at which games to buy", Key: "games preview"), bots, Look, ReplyWithin, oneAtATime: true,
			cannot: static b => !b.IsOnline || !b.Web.Ready ? $"{b.Name}: not logged in" : null);
	}

	/// <summary>What to call a candidate: its package, its bundle, or the app.</summary>
	private static string What(GameAdvisor.GameCandidate c) => c.PackageId is { } package ? $"package {package}"
		: c.BundleApps is { Count: > 0 } ? $"bundle {c.App}"
		: $"app {c.App}";

	/// <summary>One account's game look in plain words: what it would buy, for how much and what comes back, and why not when nothing.</summary>
	public static string GamesSaid(Bot b, GameAdvisor.Plan plan) {
		string name = b.Name;
		List<string> lines = [];

		if (plan.Problem.Length > 0) {
			lines.Add($"{name}: {plan.Problem}");
			lines.Add(GamesLimits(b));

			return string.Join(Environment.NewLine, lines);
		}

		int cur = plan.Currency;
		string M(long cents) => BuyPlan.Money(cents, cur);
		string hours = b.Cfg.HoursUntilCardDrops.ToString("0.#", CultureInfo.InvariantCulture);

		lines.Add($"{name}: wallet {M(plan.Wallet)} · {M(plan.DaySpent)} of {BuyPlan.LimitSaid(plan.DayLimit, cur)} spent today · "
			+ $"{M(plan.WeekSpent)} of {BuyPlan.LimitSaid(plan.WeekLimit, cur)} this week - {M(plan.Room)} can be spent now");

		if (plan.Keep > 0) {
			lines.Add($"{name}: keeping {M(plan.Keep)} in the wallet");
		}

		if (plan.Candidates.Count == 0) {
			lines.Add($"{name}: nothing to buy right now - no game fits its price, the money left and its wallet.");
		} else {
			foreach (GameAdvisor.GameCandidate c in plan.Candidates) {
				string drop = c.DropValueCents > 0
					? $"about {M(c.DropValueCents)} back in cards after about {hours}h of play"
					: $"its cards aren't priced here, so what they pay back isn't counted (they drop after about {hours}h of play)";
				string booster = c.BoosterValueCents > 0 ? $" and about {M(c.BoosterValueCents)} a day from booster packs" : "";

				lines.Add($"{name}: would buy {c.Name} ({What(c)}) for {M(c.PriceCents)} - {drop}{booster}; about {c.Points} points"
					+ (c.PackageId is not null
						? $"; a package of {c.BundleApps?.Count ?? 1} apps"
						: c.BundleApps is { Count: > 1 } games ? $"; a bundle of {games.Count} games" : ""));
			}

			lines.Add($"{name}: {plan.Candidates.Count} game(s) would be bought for {M(plan.Candidates.Sum(static c => c.PriceCents))}.");
		}

		foreach (GameAdvisor.GameCandidate c in plan.Skipped) {
			lines.Add($"{name}: skipped {c.Name} ({What(c)}) - {c.Skip ?? "over the limits"}");
		}

		if (!b.Cfg.BuyGames) {
			lines.Add($"{name}: preview only - buying games is off. 'games on {name} yes' turns it on after you have read this.");
		} else if (!BuyGamesLedger.Of(name).Ok) {
			lines.Add($"{name}: preview only - buying games has no go-ahead yet. Type 'games {name} on yes' to allow it.");
		} else if (!b.Cfg.BuyGamesUnattended) {
			lines.Add($"{name}: buying games is on, but buying without asking is off, so nothing is bought by itself. "
				+ $"Turn it on with  set {name} {nameof(BotConfig.BuyGamesUnattended)} true");
		} else {
			lines.Add($"{name}: buying games is on; 'games do {name}' buys these now, cheapest first, one at a time, and the automatic look runs every {Math.Clamp(b.Cfg.BuyGamesCheckHours, 1, 168)}h while \"Buy games without asking\" is on.");
		}

		lines.Add($"{name}: nothing was bought - a preview only looks; buying is only ever paid from the Steam wallet balance.");

		return string.Join(Environment.NewLine, lines);
	}

	/// <summary>The switch and its go-ahead together, saved. The caller has shown what it would do first.</summary>
	private static void SetBuyGames(Bot bot, bool on) {
		lock (bot.CfgGate) {
			bot.Cfg.BuyGames = on;

			if (File.Exists(Path.Combine(ConfigStore.ConfigDir, bot.Name + ".json"))) {
				ConfigStore.SaveBot(bot.Name, bot.Cfg);
			}
		}

		BuyGamesLedger.Of(bot.Name).SetOk(on);
	}
}
