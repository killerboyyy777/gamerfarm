using System.Collections.Concurrent;
using NocatFarm.Config;
using NocatFarm.Core;
using NocatFarm;

namespace NocatFarm.Modules;

/// <summary>
/// Buying games by itself, every "Look for games to buy every" hours, once buying games has its go-ahead and
/// "Buy games without asking" is on - the same rules buying cards keeps to: limits, pacing, the spending ledger and the
/// market-hold/scam-watch gates. Core/GameCheckout.cs spends, Modules/GameAdvisor.cs decides what.
/// </summary>
/// <remarks>
/// One look at a time per account, across the module and 'games do' (which uses the same routine). A human-mode account
/// looks only in its own day, buys <see cref="BotConfig.BuyGamesPerLook"/> games and waits a random while before the next
/// look; a robot buys every game that fits. Nothing is retried after a failure in the same look: the next look asks again.
/// </remarks>
public sealed class GameBuyer(Bot bot) : BotModule(bot) {
	/// <summary>One look at a time per account - 'games do' and the timer both came due at once and tried the same games.</summary>
	private static readonly ConcurrentDictionary<string, SemaphoreSlim> Busy = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Where a look's plan comes from. The checks replace it with a scripted plan; the real one reads Steam's wallet, the
	/// store and the set finder.
	/// </summary>
	public static Func<Bot, CancellationToken, Task<GameAdvisor.Plan>> PlanFor { get; set; } =
		static (b, ct) => GameAdvisor.ForAccountAsync(b, ct);

	/// <summary>Where purchases go. The checks replace it with a fake store; the real one pays from the wallet.</summary>
	public static Func<Bot, IGameStore> StoreFor { get; set; } = static b => new SteamGameStore(b);

	private HumanGate? _gate;
	private LookTimer? _timer;
	private Said _status = new("");

	public override string Name => "buying games";
	public override string Status => On(Bot) ? _status : "";

	/// <summary>Whether the automatic look runs: the switch, the go-ahead, and buying without asking.</summary>
	public static bool On(Bot bot) => bot.Cfg.BuyGames && bot.Cfg.BuyGamesUnattended && !BuyGamesLedger.Of(bot.Name).Unreadable && BuyGamesLedger.Of(bot.Name).Ok;

	/// <summary>The gate a human-mode account keeps to: its own day (no effect on a robot).</summary>
	internal HumanGate Gate => _gate ??= HumanGate.OwnDay(Bot);

	/// <summary>When the next look is - from "Look for games to buy every" as it's set now.</summary>
	internal LookTimer Timer => _timer ??= new LookTimer(() => Math.Clamp(Bot.Cfg.BuyGamesCheckHours, 1, 168), 1.0, 1.2);

	/// <summary>The same routine 'games do' runs: one account's look, then its purchases.</summary>
	public static async Task<string> BuyNowAsync(Bot bot, CancellationToken ct) {
		SemaphoreSlim busy = Busy.GetOrAdd(bot.Name, static _ => new SemaphoreSlim(1, 1));

		if (!await busy.WaitAsync(0, ct).ConfigureAwait(false)) {
			return $"{bot.Name}: already looking at which games to buy - one look at a time; the log says what it bought.";
		}

		try {
			return await BuyLockedAsync(bot, ct).ConfigureAwait(false);
		} finally {
			busy.Release();
		}
	}

	/// <summary>
	/// One account's look, then its purchases - cheapest first, one at a time, and only ever from the wallet. The
	/// ledger's go-ahead, the switch, "Buy games without asking", the day's and week's limits, "keep in the wallet",
	/// "most per game", the wallet balance, the profile-count check and the paused/market-hold/scam-watch gates are all
	/// checked first; each attempt is written down as pending before it is sent. A failure stops the run rather than
	/// trying the next game.
	/// </summary>
	private static async Task<string> BuyLockedAsync(Bot bot, CancellationToken ct) {
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

		GameAdvisor.Plan plan = await PlanFor(bot, ct).ConfigureAwait(false);

		if (plan.Problem.Length > 0) {
			return Commands.GamesSaid(bot, plan) + Environment.NewLine + $"{name}: nothing was bought.";
		}

		if (plan.Candidates.Count == 0) {
			return Commands.GamesSaid(bot, plan) + Environment.NewLine + $"{name}: nothing was bought - there was nothing to buy.";
		}

		int currency = plan.Currency;
		string M(long cents) => BuyPlan.Money(cents, currency);

		long dayLimit = BuyPlan.Limit(bot.Cfg.BuyGamesDailyLimit);
		long weekLimit = BuyPlan.Limit(bot.Cfg.BuyGamesWeeklyLimit);
		long keep = GameAdvisor.Keep(bot.Cfg);
		// A robot buys every game that fits in the look; the setting paces a human-mode account only.
		int most = bot.Cfg.LegitMode ? Math.Clamp(bot.Cfg.BuyGamesPerLook, 1, 10) : int.MaxValue;
		DateTime now = DateTimeOffset.FromUnixTimeSeconds(Confirmations.SteamTimeNow).UtcDateTime;   // the limits go by Steam's clock
		IGameStore store = StoreFor(bot);
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

			StoreGame game = new(candidate.App, candidate.Name, candidate.PriceCents, candidate.Currency, false, false, false, false);

			// Steam's appdetails can't say whether a title counts toward the profile; its store page can. A title with
			// "Profile features limited" is passed over - buying it wouldn't raise the count it was bought for.
			try {
				if (!await store.CountsForProfileAsync(game, ct).ConfigureAwait(false)) {
					lines.Add($"{name}: {candidate.Name} (app {candidate.App}) doesn't count toward the profile's game count - not bought");

					continue;
				}
			} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
				lines.Add($"{name}: stopped before anything else was sent");

				break;
			} catch (Exception e) {
				// It couldn't be read: the paid/free/bundle checks already did what appdetails can do - go on, and note it.
				Log.Debug(new Said("{0}: couldn't read whether {1} counts toward the profile: {2}", name, candidate.Name, Log.Describe(e)), name);
			}

			// Written down before it is sent: counted as spent until its outcome is known, so a crash mid-purchase can
			// never buy past the limits.
			BuyGamesLedger.Entry pending = new(now, candidate.PriceCents, currency, candidate.App, candidate.Name, Pending: true);

			if (!ledger.Add(pending)) {
				lines.Add($"{name}: the list of what it spent couldn't be saved - nothing was bought");

				break;
			}

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

				// The free DLC that comes with what was just bought, when the account takes all free DLC. Never a reason
				// to fail the purchase: the game is already on the account.
				if ((bot.Cfg.ClaimFree >= FreeClaims.GamesAndAllDlc) && (BotManager.ModuleOf<FreeGames>(bot) is { } free)) {
					IReadOnlyList<uint> boughtApps = candidate.BundleApps is { Count: > 0 } apps ? apps : new[] { candidate.App };

					try {
						int dlc = await free.ClaimDlcAsync(boughtApps[0], bot.Cfg.ClaimFree, null, ct).ConfigureAwait(false);
						dlc += await FreeGames.ClaimDlcForAsync(bot, boughtApps.Skip(1), ct).ConfigureAwait(false);

						if (dlc > 0) {
							lines.Add($"{name}: claimed {dlc} free DLC for it");
						}
					} catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) {
						Log.Debug(new Said("couldn't claim free DLC for {0}: {1}", candidate.Name, Log.Describe(e)), name);
					}
				}
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

	protected override async Task RunAsync(CancellationToken ct) {
		if (!await Sleep(Rng.Minutes(10, 25), ct).ConfigureAwait(false)) {
			return;
		}

		while (!ct.IsCancellationRequested) {
			if (On(Bot) && Bot.IsOnline && Bot.Web.Ready && !Bot.Paused && Gate.Open && Timer.Due(DateTime.UtcNow)) {
				Timer.Looked(DateTime.UtcNow);

				try {
					string said = await BuyNowAsync(Bot, ct).ConfigureAwait(false);
					Log.Debug(new Said("buying games: {0}", Log.Scrub(said.Length > 400 ? said[..400] : said)), Bot.Name);
					_status = new Said("buying games: next look around {0}", (Func<string>) (() => Fmt.Clock(Timer.Next)));
					Gate.Space(15, 90);
				} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
					throw;
				} catch (Exception e) {
					Log.Debug(new Said("couldn't look at buying games: {0}", Log.Describe(e)), Bot.Name);
					Timer.LookAt(DateTime.UtcNow + Rng.Minutes(45, 90));
				}
			}

			// Asked again every minute, so a changed setting moves the next look straight away.
			if (!await Sleep(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false)) {
				return;
			}
		}
	}
}
