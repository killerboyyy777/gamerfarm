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
	internal const string GamesUsage = "games [account|all] | games preview [account|all] | games on <account> yes | games off <account> | games do <account>";

	/// <summary>What a 'games' line asks for.</summary>
	public enum GamesAsk { List, Preview, On, Off, Do, Bad }

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
			} else if ((who == null) && !all) {
				who = word;
			} else {
				return (GamesAsk.Bad, null, false);
			}
		}

		// Turning it on, off or buying now: one account by name, never every account at once.
		if ((ask is GamesAsk.On or GamesAsk.Off or GamesAsk.Do) && (all || (who == null))) {
			return (GamesAsk.Bad, null, false);
		}

		return (ask, who, yes);
	}

	/// <summary>
	/// Turning buying games on, or buying now, is the owner's word - not something a Steam-chat master gives for them.
	/// Looking and turning it off are fine from anywhere.
	/// </summary>
	internal static bool GamesNeedsOwner(string[] args) =>
		args.Any(static a => a.ToLowerInvariant() is "on" or "do" or "now" or "yes" or "ok");

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
				$"{bot.Name}: 'games preview {bot.Name}' shows what it would buy; 'games do {bot.Name}' buys now (it also needs \"{Settings.FindBot(nameof(BotConfig.BuyGamesUnattended))?.Label ?? "Buy games without asking"}\" on), one look at a time - there is no automatic loop yet; 'games off {bot.Name}' turns it off.");
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
	private static async Task<string> GamesBuyAsync(Bot bot, CancellationToken ct) {
		string name = bot.Name;
		BuyGamesLedger ledger = BuyGamesLedger.Of(name);

		if (!bot.Cfg.BuyGames || !ledger.Ok) {
			return $"{name}: buying games is off - nothing was bought. 'games preview {name}' shows what it would buy; 'games on {name} yes' turns it on.";
		}

		if (ledger.Unreadable) {
			return $"{name}: {ledger.UnreadableText}";
		}

		if (!bot.Cfg.BuyGamesUnattended) {
			return $"{name}: buying games by itself is off, and there is no way to ask you here - nothing was bought. "
				+ $"Turn it on first:  set {name} {nameof(BotConfig.BuyGamesUnattended)} true";
		}

		if (bot.Paused) {
			return $"{name}: it's paused - nothing was bought.";
		}

		if (MarketHold.Blocked(bot) is { } market) {
			return $"{name}: {market.ToEnglish()}";
		}

		if (ScamWatch.HeldBack(bot) is { } held) {
			return $"{name}: {held}";
		}

		GameAdvisor.Plan plan = await GameAdvisor.ForAccountAsync(bot, ct).ConfigureAwait(false);

		if (plan.Problem.Length > 0) {
			return GamesSaid(bot, plan) + Environment.NewLine + $"{name}: nothing was bought.";
		}

		if (plan.Candidates.Count == 0) {
			return GamesSaid(bot, plan) + Environment.NewLine + $"{name}: nothing was bought - there was nothing to buy.";
		}

		int currency = plan.Currency;
		string M(long cents) => BuyPlan.Money(cents, currency);

		long dayLimit = BuyPlan.Limit(bot.Cfg.BuyGamesDailyLimit);
		long weekLimit = BuyPlan.Limit(bot.Cfg.BuyGamesWeeklyLimit);
		long keep = GameAdvisor.Keep(bot.Cfg);
		// A robot buys every game that fits in the look; the setting paces a human-mode account only.
		int most = bot.Cfg.LegitMode ? Math.Clamp(bot.Cfg.BuyGamesPerLook, 1, 10) : int.MaxValue;
		DateTime now = DateTimeOffset.FromUnixTimeSeconds(Confirmations.SteamTimeNow).UtcDateTime;   // the limits go by Steam's clock
		IGameStore store = new SteamGameStore(bot);
		List<string> lines = [];
		int bought = 0;
		long paid = 0;

		// Cheapest first: several purchases in one look spend as little as they can.
		foreach (GameAdvisor.GameCandidate candidate in plan.Candidates.OrderBy(static c => c.PriceCents).ThenBy(static c => c.App).Take(most)) {
			if (ct.IsCancellationRequested) {
				lines.Add($"{name}: stopped before anything else was sent");

				break;
			}

			// The same gates again before every purchase: a pause or a market block can land while the last one runs.
			if (bot.Paused) {
				lines.Add($"{name}: paused before the next game - nothing else was bought");

				break;
			}

			if (MarketHold.Blocked(bot) is { } hold) {
				lines.Add($"{name}: {hold.ToEnglish()}");

				break;
			}

			if (ScamWatch.HeldBack(bot) is { } scam) {
				lines.Add($"{name}: {scam}");

				break;
			}

			long left = Math.Min(Math.Min(dayLimit - ledger.SpentSince(now.AddDays(-1)), weekLimit - ledger.SpentSince(now.AddDays(-7))),
				plan.Wallet - keep - paid);

			if (candidate.PriceCents > left) {
				lines.Add($"{name}: {candidate.Name} (app {candidate.App}) waits - {M(Math.Max(0, left))} is what may be spent now");

				break;
			}

			// Written down before it is sent: counted as spent until its outcome is known, so a crash mid-purchase can
			// never buy past the limits.
			BuyGamesLedger.Entry pending = new(now, candidate.PriceCents, currency, candidate.App, candidate.Name, Pending: true);

			if (!ledger.Add(pending)) {
				lines.Add($"{name}: the list of what it spent couldn't be saved - nothing was bought");

				break;
			}

			StoreGame game = new(candidate.App, candidate.Name, candidate.PriceCents, candidate.Currency, false, false, false, false);
			GamePurchase answer;

			try {
				answer = await store.BuyAsync(game, ct).ConfigureAwait(false);
			} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
				// Cut off mid-request: it may have reached Steam, so the entry stays pending and keeps counting as spent.
				lines.Add($"{name}: stopped while buying {candidate.Name} (app {candidate.App}) - it's counted as spent until a later look finds out");

				break;
			} catch (Exception e) {
				// The request may have reached Steam: the entry stays pending and keeps counting as spent.
				Log.Info(new Said("{0}: buying {1} (app {2}) failed: {3}", name, candidate.Name, candidate.App, Log.Describe(e)), name);
				lines.Add($"{name}: buying {candidate.Name} (app {candidate.App}) failed - {Log.Describe(e)}; it's counted as spent until a later look finds out");

				break;
			}

			if (answer.Result == GameBuyResult.Done) {
				long cost = answer.PaidCents > 0 ? answer.PaidCents : candidate.PriceCents;
				ledger.Replace(pending, pending with { Cents = cost, Pending = false, TransId = answer.TransId });
				bought++;
				paid += cost;
				lines.Add($"{name}: bought {candidate.Name} (app {candidate.App}) for {M(cost)} - paid from the wallet balance");
				Log.Info(new Said("{0}: bought {1} (app {2}) for {3}", name, candidate.Name, candidate.App, M(cost)), name);
			} else {
				// Money first: anything that may have spent stays in the list as pending.
				if (answer.Spent) {
					// Sent but not confirmed: keep the planned amount when Steam didn't say what it charged, so the
					// day's and week's limits still count it.
					ledger.Replace(pending, pending with { Cents = answer.PaidCents > 0 ? answer.PaidCents : pending.Cents, Pending = true, TransId = answer.TransId });
				} else {
					ledger.Replace(pending, null);
				}

				lines.Add($"{name}: didn't buy {candidate.Name} (app {candidate.App}) - {answer.Reason}");
				Log.Info(new Said("{0}: didn't buy {1} (app {2}) - {3}", name, candidate.Name, candidate.App, answer.Reason), name);

				break;   // one failure a look: nothing else is tried on a guess
			}
		}

		DateTime after = DateTimeOffset.FromUnixTimeSeconds(Confirmations.SteamTimeNow).UtcDateTime;

		lines.Add(bought == 0
			? $"{name}: nothing was bought this time."
			: $"{name}: bought {bought} game(s) for {M(paid)} - {M(ledger.SpentSince(after.AddDays(-1)))} of {BuyPlan.LimitSaid(dayLimit, currency)} today, "
				+ $"{M(ledger.SpentSince(after.AddDays(-7)))} of {BuyPlan.LimitSaid(weekLimit, currency)} this week");

		if (plan.Candidates.Count > most) {
			lines.Add($"{name}: {plan.Candidates.Count - most} more would follow at the next look");
		}

		Log.Info(new Said("{0}: buying games now bought {1} for {2}", name, bought, M(paid)), name);

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

				lines.Add($"{name}: would buy {c.Name} (app {c.App}) for {M(c.PriceCents)} - {drop}{booster}; about {c.Points} points");
			}

			lines.Add($"{name}: {plan.Candidates.Count} game(s) would be bought for {M(plan.Candidates.Sum(static c => c.PriceCents))}.");
		}

		foreach (GameAdvisor.GameCandidate c in plan.Skipped) {
			lines.Add($"{name}: skipped {c.Name} (app {c.App}) - {c.Skip ?? "over the limits"}");
		}

		if (!b.Cfg.BuyGames) {
			lines.Add($"{name}: preview only - buying games is off. 'games on {name} yes' turns it on after you have read this.");
		} else if (!BuyGamesLedger.Of(name).Ok) {
			lines.Add($"{name}: preview only - buying games has no go-ahead yet. Type 'games {name} on yes' to allow it.");
		} else if (!b.Cfg.BuyGamesUnattended) {
			lines.Add($"{name}: buying games is on, but buying without asking is off, so nothing is bought by itself. "
				+ $"Turn it on with  set {name} {nameof(BotConfig.BuyGamesUnattended)} true");
		} else {
			lines.Add($"{name}: buying games is on; 'games do {name}' buys these now, cheapest first, one at a time (there is no automatic loop yet).");
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
