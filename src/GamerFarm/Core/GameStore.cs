using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GamerFarm.Core;

/// <summary>One game as the Steam store prices it right now, for deciding whether it's worth buying.</summary>
/// <param name="PriceCents">What it costs in <paramref name="Currency"/> (the store's own price for the account's country); 0 when it shows none.</param>
/// <param name="Currency">The store's currency id (SteamKit's ECurrencyCode), or 0 when the price didn't say.</param>
/// <param name="Free">Free to play, or free for good - not a paid game on a giveaway.</param>
/// <param name="AgeGated">The store asks for an age check before it will show the game.</param>
/// <param name="BundleOnly">Not sold as a game on its own: a bundle, DLC, demo, or a game with no price of its own.</param>
/// <param name="Owned">The account already has it (its library, or the store's own answer).</param>
/// <param name="CountsForProfile">Whether the game counts toward the profile's game count and its badge: a paid game,
/// not one with Steam's "Profile features limited" notice. Free-to-play games and bundles don't count.</param>
/// <param name="RegionBlocked">Not sold in this account's region: the store gave no price and no package, but it is a
/// paid game - Steam prices regionally, so this is "not available here", not "priced in another currency".</param>
/// <param name="BundleApps">When it's a bundle rather than a game: every game it includes (Steam's own list). Empty or
/// null for a game.</param>
/// <param name="PackageId">When it's a Steam package sold as a pack rather than one app: the package to put in the cart.
/// Null for a game, which the checkout looks up a package for.</param>
public sealed record StoreGame(uint App, string Name, long PriceCents, int Currency, bool Free, bool AgeGated, bool BundleOnly, bool Owned,
	bool CountsForProfile = true, bool RegionBlocked = false, IReadOnlyList<uint>? BundleApps = null, uint? PackageId = null);

/// <summary>One Steam package sold on its own: the pack's name, its price, and every app it grants.</summary>
public sealed record StorePackage(uint Id, string Name, long PriceCents, int Currency, IReadOnlyList<uint> Apps);

/// <summary>How one game purchase went. Anything but <see cref="Done"/> spent nothing, or says in its reason that it may have.</summary>
public enum GameBuyResult {
	/// <summary>Bought: the wallet balance fell by what Steam took.</summary>
	Done,

	/// <summary>Steam said no, readably, before anything was finalized.</summary>
	Refused,

	/// <summary>Steam wants the payment made somewhere else (a card, PayPal, a bank page): never followed, nothing spent.</summary>
	ExternalPayment,

	/// <summary>The wallet didn't hold enough for the final price - or Steam said so - so nothing was finalized.</summary>
	InsufficientFunds,

	/// <summary>Steam wants the purchase reviewed, approved or challenged (3D Secure, a bank check): never bypassed.</summary>
	Review,

	/// <summary>It didn't work, and what it did isn't certain: <see cref="GamePurchase.Spent"/> says whether money went.</summary>
	Failed,

	/// <summary>A dry run: the real checkout was followed to its final price, then the transaction was cancelled - nothing bought.</summary>
	Probed
}

/// <summary>
/// One game purchase's answer: how it went, why when not simply bought, what the wallet actually paid, and Steam's
/// transaction id once it gave one.
/// </summary>
/// <param name="PaidCents">What the wallet fell by, in <paramref name="Currency"/> - 0 when nothing was spent.</param>
public sealed record GamePurchase(GameBuyResult Result, string Reason = "", long PaidCents = 0, int Currency = 0, string? TransId = null, bool Sent = false,
	IReadOnlyList<uint>? Apps = null, string? PackageName = null) {
	/// <summary>
	/// Whether any money may have gone: once finalizing has been sent it counts as spent, even when the wallet page hasn't
	/// caught up yet or Steam left the purchase for review.
	/// </summary>
	public bool Spent => (PaidCents > 0) || Sent;
}

/// <summary>What buying games needs from the Steam store. A fake in the tests - no test ever reaches Steam.</summary>
public interface IGameStore {
	/// <summary>The game's price and state now, in the wallet's currency (best effort). Null when the store didn't answer.</summary>
	Task<StoreGame?> GameAsync(uint app, int currency, CancellationToken ct);

	/// <summary>
	/// Buy the game, from the Steam wallet balance only. Spends money. Never adds funds, never follows an external
	/// payment page, never falls back to another payment method: anything but the wallet is <see cref="GameBuyResult.ExternalPayment"/>
	/// or <see cref="GameBuyResult.Review"/> and spends nothing.
	/// </summary>
	Task<GamePurchase> BuyAsync(StoreGame game, CancellationToken ct);

	/// <summary>
	/// Whether the game really counts toward the profile's game count: Steam's appdetails can't say it, but its store
	/// page shows "Profile features limited" (or "Steam is Learning About This Game") for titles that don't. Unreadable
	/// page: true - it can't prove otherwise, and the paid/free/bundle checks already did their part.
	/// </summary>
	Task<bool> CountsForProfileAsync(StoreGame game, CancellationToken ct) => Task.FromResult(game.CountsForProfile);

	/// <summary>
	/// The bundle ids Steam lists on the game's own store page - bundles a single game can't be found in through the
	/// store's search. Empty when none, or when the store didn't answer.
	/// </summary>
	Task<IReadOnlyList<uint>> BundlesForAsync(uint app, CancellationToken ct) => Task.FromResult<IReadOnlyList<uint>>([]);

	/// <summary>
	/// One bundle's price and the games it includes, in the wallet's money. Null when the store didn't answer or it isn't
	/// a bundle of games.
	/// </summary>
	Task<StoreGame?> BundleAsync(uint bundleId, int currency, CancellationToken ct) => Task.FromResult<StoreGame?>(null);

	/// <summary>
	/// The multi-app packages Steam sells the app through, in the wallet's money - a pack of several games is one cart
	/// item, and Steam only names it as a purchase option on the app's own store data. Empty when none, or when the
	/// store didn't answer.
	/// </summary>
	Task<IReadOnlyList<StorePackage>> PackagesForAsync(uint app, int currency, CancellationToken ct) =>
		Task.FromResult<IReadOnlyList<StorePackage>>([]);

	/// <summary>
	/// The cheapest packages the store itself lists for sale, in the wallet's money - a store-wide look for packs, since
	/// the cheapest ones are where several games for one small price are. Empty when none, or when the store didn't answer.
	/// </summary>
	Task<IReadOnlyList<StorePackage>> CheapestPacksAsync(int currency, CancellationToken ct) =>
		Task.FromResult<IReadOnlyList<StorePackage>>([]);
}

/// <summary>
/// The Steam store, read for real: appdetails?filters=basic,price_overview in the account's country, so prices come back
/// in its own wallet currency. <see cref="BuyAsync"/> is the wallet-only checkout (Core/GameCheckout.cs).
/// </summary>
public sealed class SteamGameStore(Bot bot) : IGameStore {
	/// <summary>Steam's store doesn't like being hurried: at least this long between two appdetails reads.</summary>
	private static readonly TimeSpan Gap = TimeSpan.FromSeconds(1.5);

	public async Task<StoreGame?> GameAsync(uint app, int currency, CancellationToken ct) {
		string country = bot.Country.Length == 2 ? bot.Country : "";
		string url = $"https://store.steampowered.com/api/appdetails?appids={app.ToString(CultureInfo.InvariantCulture)}&filters=basic,price_overview&l=english"
			+ (country.Length > 0 ? "&cc=" + country : "");

		(int status, string? body) = await GameCatalog.StoreGetAsync(url, Gap, ct).ConfigureAwait(false);

		return (status != 200) || string.IsNullOrWhiteSpace(body) ? null : Parse(body, app, bot.Library.Find(app) is not null);
	}

	/// <summary>Buy the game with the wallet balance only - see <see cref="GameCheckout"/>. A bundle or a package is one
	/// cart item whose price covers every game in it, so the per-game cap doesn't apply to it.</summary>
	public Task<GamePurchase> BuyAsync(StoreGame game, CancellationToken ct) =>
		new GameCheckout(new BotCheckoutWeb(bot),
			(game.BundleApps is { Count: > 0 }) || game.PackageId is not null ? 0 : GamerFarm.Modules.BuyPlan.Cents(bot.Cfg.BuyGamesMaxPrice)).BuyAsync(game, ct);

	/// <summary>Store page reads for the profile-count notice, kept a week per app - it changes with the app, not a look.</summary>
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, (bool Counts, DateTime At)> ProfileKept = new();

	/// <summary>
	/// The store page's own notice: "Profile features limited" marks a title that doesn't count toward the profile's
	/// game count and badge; "Steam is Learning About This Game" is the same kind of limit. Undecidable from appdetails,
	/// so the page is read once a week per app. Null when the page couldn't be read (the caller decides).
	/// </summary>
	public static bool? ProfileLimited(string? html) => string.IsNullOrWhiteSpace(html) ? null
		: html.Contains("Profile Features Limited", StringComparison.OrdinalIgnoreCase)
			|| html.Contains("Steam is Learning About This Game", StringComparison.OrdinalIgnoreCase);

	/// <summary>A readable page without the notice counts; one with it doesn't; an unreadable page counts (the cheap checks
	/// already ruled out what can be known without it).</summary>
	public static bool CountsOnPage(bool? limited) => limited != true;

	/// <summary>
	/// Whether the game counts toward the profile's game count: a paid, standalone game whose store page doesn't carry
	/// Steam's profile-features notice. The page is read at most once a week per app; unreadable, the game is counted
	/// (the paid/free/bundle checks already ruled out what can be known without it).
	/// </summary>
	public async Task<bool> CountsForProfileAsync(StoreGame game, CancellationToken ct) {
		if (!game.CountsForProfile) {
			return false;
		}

		// A bundle is a cart item of its own: its page has no profile notice, and the games inside it are the paid games
		// the advisor already checked. Don't read the page for it.
		if (game.BundleApps is { Count: > 0 }) {
			return true;
		}

		if (ProfileKept.TryGetValue(game.App, out (bool Counts, DateTime At) kept) && (DateTime.UtcNow - kept.At < TimeSpan.FromDays(7))) {
			return kept.Counts;
		}

		string country = bot.Country.Length == 2 ? "?cc=" + bot.Country : "?";
		string url = $"https://store.steampowered.com/app/{game.App.ToString(CultureInfo.InvariantCulture)}/{country}&l=english";
		(int status, string? body) = await GameCatalog.StoreGetAsync(url, Gap, ct).ConfigureAwait(false);
		bool? limited = status == 200 ? ProfileLimited(body) : null;
		bool counts = CountsOnPage(limited);

		if (limited != null) {
			ProfileKept[game.App] = (counts, DateTime.UtcNow);
		}

		return counts;
	}

	/// <summary>Bundle ids read from a game's store page, kept a week per app - the page changes with the app, not a look.</summary>
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, (IReadOnlyList<uint> Ids, DateTime At)> BundleKept = new();

	/// <summary>The store page's own bundle links: <c>bundleid="231"</c>. Up to four, or none.</summary>
	public static IReadOnlyList<uint> BundleIds(string? html) {
		if (string.IsNullOrWhiteSpace(html)) {
			return [];
		}

		return [.. System.Text.RegularExpressions.Regex.Matches(html, "bundleid=\"(\\d+)\"", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
			.Select(static m => uint.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out uint id) ? id : 0)
			.Where(static id => id != 0).Distinct().Take(4)];
	}

	/// <summary>
	/// The bundles the game's store page names, read at most once a week per app. Empty when the page doesn't name one or
	/// couldn't be read - a bundle is only ever an extra candidate, never a reason to fail a look.
	/// </summary>
	public async Task<IReadOnlyList<uint>> BundlesForAsync(uint app, CancellationToken ct) {
		if (BundleKept.TryGetValue(app, out (IReadOnlyList<uint> Ids, DateTime At) kept) && (DateTime.UtcNow - kept.At < TimeSpan.FromDays(7))) {
			return kept.Ids;
		}

		string country = bot.Country.Length == 2 ? "?cc=" + bot.Country : "?";
		string url = $"https://store.steampowered.com/app/{app.ToString(CultureInfo.InvariantCulture)}/{country}&l=english";
		(int status, string? body) = await GameCatalog.StoreGetAsync(url, Gap, ct).ConfigureAwait(false);
		IReadOnlyList<uint> ids = status == 200 ? BundleIds(body) : [];

		if (status == 200) {
			BundleKept[app] = (ids, DateTime.UtcNow);
		}

		return ids;
	}

	/// <summary>
	/// One bundle: its name, its price in the wallet's money, and the games it includes - from Steam's own store browse
	/// answer, which (unlike appdetails) knows bundles. Null when it isn't a bundle of games or the store didn't answer.
	/// </summary>
	public async Task<StoreGame?> BundleAsync(uint bundleId, int currency, CancellationToken ct) {
		string country = bot.Country.Length == 2 ? bot.Country : "US";
		string json = JsonSerializer.Serialize(new {
			ids = new object[] { new { bundleid = bundleId } },
			context = new { language = "english", country_code = country, steam_realm = 1 },
			data_request = new { include_basic_info = true }
		});
		string url = "https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(json);
		(int status, string? body) = await GameCatalog.StoreGetAsync(url, Gap, ct).ConfigureAwait(false);

		return status == 200 ? ParseBundle(body, bundleId, currency) : null;
	}

	/// <summary>
	/// A store browse answer for one bundle: the first <c>store_items</c> entry of type 2, its name, its final price in
	/// cents, and its <c>included_appids</c>. Null when it isn't that bundle or includes no game.
	/// </summary>
	public static StoreGame? ParseBundle(string? json, uint bundleId, int currency) {
		if (string.IsNullOrWhiteSpace(json)) {
			return null;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(json);

			if (!doc.RootElement.TryGetProperty("response", out JsonElement response) || (response.ValueKind != JsonValueKind.Object)
				|| !response.TryGetProperty("store_items", out JsonElement items) || (items.ValueKind != JsonValueKind.Array)) {
				return null;
			}

			foreach (JsonElement item in items.EnumerateArray()) {
				if ((item.ValueKind != JsonValueKind.Object)
					|| !item.TryGetProperty("item_type", out JsonElement type) || !type.TryGetInt32(out int kind) || (kind != 2)
					|| !item.TryGetProperty("included_appids", out JsonElement included) || (included.ValueKind != JsonValueKind.Array)) {
					continue;
				}

				List<uint> apps = [.. included.EnumerateArray().Select(static a => a.TryGetUInt32(out uint app) ? app : 0).Where(static app => app != 0).Distinct()];

				if (apps.Count == 0) {
					continue;
				}

				string name = item.TryGetProperty("name", out JsonElement n) && (n.ValueKind == JsonValueKind.String) ? (n.GetString() ?? "").Trim() : "";
				long price = 0;

				if (item.TryGetProperty("final_price_in_cents", out JsonElement p) || item.TryGetProperty("final_price", out p)) {
					price = p.ValueKind switch {
						JsonValueKind.Number when p.TryGetInt64(out long cents) => cents,
						JsonValueKind.String when long.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long cents) => cents,
						_ => 0
					};
				}

				if ((price <= 0) && item.TryGetProperty("best_purchase_option", out JsonElement option) && (option.ValueKind == JsonValueKind.Object)
					&& option.TryGetProperty("final_price_in_cents", out JsonElement op)) {
					price = op.ValueKind switch {
						JsonValueKind.Number when op.TryGetInt64(out long cents) => cents,
						JsonValueKind.String when long.TryParse(op.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long cents) => cents,
						_ => 0
					};
				}

				return new StoreGame(bundleId, name, Math.Max(0, price), currency, false, false, false, false,
					CountsForProfile: true, RegionBlocked: price <= 0, BundleApps: apps);
			}

			return null;
		} catch (JsonException) {
			return null;
		}
	}

	/// <summary>Packages read for an app, and what each of them holds, kept a week - the store changes these rarely.</summary>
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, (IReadOnlyList<StorePackage> Packs, DateTime At)> PacksKept = new();

	private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, (PackageInfo Info, DateTime At)> PackageContentsKept = new();

	/// <summary>
	/// The multi-app packages the app is sold through, read at most once a week per app: its full appdetails names the
	/// packages (the filtered ones leave them out), and one batched packagedetails read says what each holds. Empty when
	/// the app is sold on its own, or when the store didn't answer.
	/// </summary>
	public async Task<IReadOnlyList<StorePackage>> PackagesForAsync(uint app, int currency, CancellationToken ct) {
		if (PacksKept.TryGetValue(app, out (IReadOnlyList<StorePackage> Packs, DateTime At) kept) && (DateTime.UtcNow - kept.At < TimeSpan.FromDays(7))) {
			return kept.Packs;
		}

		string country = bot.Country.Length == 2 ? bot.Country : "";
		string url = $"https://store.steampowered.com/api/appdetails?appids={app.ToString(CultureInfo.InvariantCulture)}&l=english"
			+ (country.Length > 0 ? "&cc=" + country : "");
		(int status, string? body) = await GameCatalog.StoreGetAsync(url, Gap, ct).ConfigureAwait(false);

		if ((status != 200) || string.IsNullOrWhiteSpace(body)) {
			return [];
		}

		List<(uint Id, long PriceCents)> offers = [.. PackageOffers(body, app).Where(static s => s.PriceCents > 0).Take(4)];

		if (offers.Count == 0) {
			PacksKept[app] = ([], DateTime.UtcNow);

			return [];
		}

		IReadOnlyDictionary<uint, PackageInfo> contents = await PackageContentsAsync([.. offers.Select(static s => s.Id)], ct).ConfigureAwait(false);
		List<StorePackage> packs = [];

		foreach ((uint id, long price) in offers) {
			// One app is the game's own package, not a pack: the checkout buys those as it always has.
			if (contents.TryGetValue(id, out PackageInfo? info) && (info.Apps.Count > 1)) {
				packs.Add(new StorePackage(id, info.Name, price, currency, info.Apps));
			}
		}

		PacksKept[app] = (packs, DateTime.UtcNow);

		return packs;
	}

	/// <summary>What each package holds, from packagedetails - one batched read for the ones not kept yet.</summary>
	private async Task<IReadOnlyDictionary<uint, PackageInfo>> PackageContentsAsync(IReadOnlyList<uint> ids, CancellationToken ct) {
		Dictionary<uint, PackageInfo> found = [];
		List<uint> missing = [];

		foreach (uint id in ids.Distinct()) {
			if (PackageContentsKept.TryGetValue(id, out (PackageInfo Info, DateTime At) kept) && (DateTime.UtcNow - kept.At < TimeSpan.FromDays(7))) {
				found[id] = kept.Info;
			} else {
				missing.Add(id);
			}
		}

		if (missing.Count == 0) {
			return found;
		}

		string country = bot.Country.Length == 2 ? "&cc=" + bot.Country : "";
		string list = string.Join(",", missing.Select(static id => id.ToString(CultureInfo.InvariantCulture)));
		string url = $"https://store.steampowered.com/api/packagedetails?packageids={list}&l=english{country}";
		(int status, string? body) = await GameCatalog.StoreGetAsync(url, Gap, ct).ConfigureAwait(false);

		if (status != 200) {
			return found;
		}

		foreach ((uint id, PackageInfo info) in PackageContents(body)) {
			PackageContentsKept[id] = (info, DateTime.UtcNow);
			found[id] = info;
		}

		return found;
	}

	private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Country, int Currency), (IReadOnlyList<StorePackage> Packs, DateTime At)> CheapPacksKept = new();

	/// <summary>
	/// The store search, its discounted rows cheapest first. A pack that carries a discount of its own is listed there as
	/// a row of its own (plain rows are games), and each row already names the apps it grants. Two pages of a hundred,
	/// kept six hours per country because discounts move; a failed page stops the scan.
	/// </summary>
	public async Task<IReadOnlyList<StorePackage>> CheapestPacksAsync(int currency, CancellationToken ct) {
		string country = bot.Country.Length == 2 ? bot.Country : "";
		(string Country, int Currency) key = (country, currency);

		if (CheapPacksKept.TryGetValue(key, out (IReadOnlyList<StorePackage> Packs, DateTime At) kept)
			&& (DateTime.UtcNow - kept.At < TimeSpan.FromHours(6))) {
			return kept.Packs;
		}

		List<StorePackage> packs = [];

		for (int page = 0; page < 2; page++) {
			string url = "https://store.steampowered.com/search/results/?query&start="
				+ (page * 100).ToString(CultureInfo.InvariantCulture)
				+ "&count=100&dynamic_data=&sort_by=Price_ASC&specials=1&infinite=1&l=english"
				+ (country.Length > 0 ? "&cc=" + country : "");

			(int status, string? body) = await GameCatalog.StoreGetAsync(url, Gap, ct).ConfigureAwait(false);

			if (status != 200) {
				break;
			}

			packs.AddRange(ParseCheapestPacks(body, currency));
		}

		IReadOnlyList<StorePackage> cheapest = [.. packs.DistinctBy(static p => p.Id)];
		CheapPacksKept[key] = (cheapest, DateTime.UtcNow);

		return cheapest;
	}

	/// <summary>
	/// The packages in a store search answer: each "sub" row's id, name, price in the wallet's money, and every app it
	/// grants (Steam writes them as a comma-separated list on the row). Free rows and single-app subs are left out.
	/// </summary>
	public static IReadOnlyList<StorePackage> ParseCheapestPacks(string? json, int currency) {
		if (string.IsNullOrWhiteSpace(json)) {
			return [];
		}

		string? html;

		try {
			using JsonDocument doc = JsonDocument.Parse(json);

			html = doc.RootElement.TryGetProperty("results_html", out JsonElement rows) && (rows.ValueKind == JsonValueKind.String)
				? rows.GetString()
				: null;
		} catch (JsonException) {
			return [];
		}

		if (string.IsNullOrWhiteSpace(html)) {
			return [];
		}

		Regex row = new("<a\\b[^>]*data-ds-itemkey=\"Sub_(?<id>\\d+)\"[^>]*>(?<body>.*?)</a>",
			RegexOptions.Singleline | RegexOptions.CultureInvariant);
		Regex name = new("class=\"title\"[^>]*>(?<name>[^<]*)<", RegexOptions.CultureInvariant);
		Regex apps = new("data-ds-appid=\"(?<apps>[0-9,]+)\"", RegexOptions.CultureInvariant);
		Regex price = new("data-price-final=\"(?<cents>\\d+)\"", RegexOptions.CultureInvariant);
		List<StorePackage> packs = [];

		foreach (Match m in row.Matches(html)) {
			if (!uint.TryParse(m.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out uint id)) {
				continue;
			}

			Match cents = price.Match(m.Value);
			Match list = apps.Match(m.Value);

			if (!cents.Success || !long.TryParse(cents.Groups["cents"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long priceCents) || (priceCents <= 0)) {
				continue;
			}

			List<uint> included = [];

			if (list.Success) {
				foreach (string part in list.Groups["apps"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries)) {
					if (uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out uint app) && (app != 0) && !included.Contains(app)) {
						included.Add(app);
					}
				}
			}

			// A single app's package is the game's own buy button - the advisor already prices those.
			if (included.Count < 2) {
				continue;
			}

			string title = name.Match(m.Groups["body"].Value) is { Success: true } found
				? System.Net.WebUtility.HtmlDecode(found.Groups["name"].Value).Trim()
				: "";

			packs.Add(new StorePackage(id, title, priceCents, currency, included));
		}

		return packs;
	}

	/// <summary>
	/// The packages an app is sold through, from a full appdetails answer: each one's id and the price it shows there.
	/// Free licences are left out. Empty when the answer isn't about that app or names none.
	/// </summary>
	public static IReadOnlyList<(uint Id, long PriceCents)> PackageOffers(string? json, uint app) {
		if (string.IsNullOrWhiteSpace(json)) {
			return [];
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(json);

			if (!doc.RootElement.TryGetProperty(app.ToString(CultureInfo.InvariantCulture), out JsonElement node) || (node.ValueKind != JsonValueKind.Object)
				|| !node.TryGetProperty("success", out JsonElement ok) || (ok.ValueKind != JsonValueKind.True)
				|| !node.TryGetProperty("data", out JsonElement data) || (data.ValueKind != JsonValueKind.Object)
				|| !data.TryGetProperty("package_groups", out JsonElement groups) || (groups.ValueKind != JsonValueKind.Array)) {
				return [];
			}

			List<(uint Id, long PriceCents)> subs = [];

			foreach (JsonElement group in groups.EnumerateArray()) {
				if ((group.ValueKind != JsonValueKind.Object) || !group.TryGetProperty("subs", out JsonElement list) || (list.ValueKind != JsonValueKind.Array)) {
					continue;
				}

				foreach (JsonElement sub in list.EnumerateArray()) {
					if ((sub.ValueKind != JsonValueKind.Object) || !sub.TryGetProperty("packageid", out JsonElement id) || !id.TryGetUInt32(out uint package) || (package == 0)) {
						continue;
					}

					if (sub.TryGetProperty("is_free_license", out JsonElement free) && (free.ValueKind == JsonValueKind.True)) {
						continue;
					}

					long price = sub.TryGetProperty("price_in_cents_with_discount", out JsonElement p) && p.TryGetInt64(out long cents) ? cents : 0;

					if (!subs.Any(s => s.Id == package)) {
						subs.Add((package, Math.Max(0, price)));
					}
				}
			}

			return subs;
		} catch (JsonException) {
			return [];
		}
	}

	/// <summary>
	/// Every package a packagedetails answer describes, by id: its name and the apps it grants (a pack has several).
	/// Packages with no apps are left out.
	/// </summary>
	public static IReadOnlyDictionary<uint, PackageInfo> PackageContents(string? json) {
		Dictionary<uint, PackageInfo> contents = [];

		if (string.IsNullOrWhiteSpace(json)) {
			return contents;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(json);

			if (!doc.RootElement.TryGetProperty("data", out JsonElement data) || (data.ValueKind != JsonValueKind.Object)) {
				return contents;
			}

			foreach (JsonProperty property in data.EnumerateObject()) {
				if (!uint.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out uint id) || (id == 0) || (property.Value.ValueKind != JsonValueKind.Object)) {
					continue;
				}

				string name = property.Value.TryGetProperty("name", out JsonElement label) && (label.ValueKind == JsonValueKind.String) ? (label.GetString() ?? "").Trim() : "";
				List<uint> apps = [];

				if (property.Value.TryGetProperty("apps", out JsonElement list) && (list.ValueKind == JsonValueKind.Array)) {
					foreach (JsonElement app in list.EnumerateArray()) {
						uint found = app.ValueKind switch {
							JsonValueKind.Object when app.TryGetProperty("id", out JsonElement aid) => IdOf(aid),
							JsonValueKind.Number or JsonValueKind.String => IdOf(app),
							_ => 0
						};

						if ((found != 0) && !apps.Contains(found)) {
							apps.Add(found);
						}
					}
				}

				if (apps.Count > 0) {
					contents[id] = new PackageInfo(name, apps);
				}
			}
		} catch (JsonException) {
			// Not the answer we know: no package contents.
		}

		return contents;
	}

	/// <summary>An app id as Steam writes it - a number, or a number in text. 0 when it isn't one.</summary>
	public static uint IdOf(JsonElement value) => value.ValueKind switch {
		JsonValueKind.Number when value.TryGetUInt32(out uint n) => n,
		JsonValueKind.String when uint.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out uint n) => n,
		_ => 0
	};

	/// <summary>
	/// The package that sells an app on its own, from a full appdetails answer (the filtered ones leave package_groups
	/// out). The one whose price is the price the plan saw is preferred; one package on its own is that game's, several at
	/// another price are never guessed at. Null when the store names no package that can be added to a cart.
	/// </summary>
	public static uint? ReadPackage(string? json, uint app, long priceCents) {
		IReadOnlyList<(uint Id, long PriceCents)> subs = PackageOffers(json, app);

		if (subs.Count == 0) {
			return null;
		}

		uint exact = subs.FirstOrDefault(s => (priceCents > 0) && (s.PriceCents == priceCents)).Id;

		if (exact > 0) {
			return exact;
		}

		// One package and nothing to confuse it with: that's the app's own. Several at another price (an edition,
		// a DLC, a bundle) are never guessed at - buying the wrong one is worse than not buying.
		return subs.Count == 1 ? subs[0].Id : null;
	}

	/// <summary>
	/// One appdetails answer: the name, the price the buyer pays today (the sale price when there is one, else the regular
	/// one), whether it's free, age-gated, or only sold in a bundle. Null when it isn't an answer about that app.
	/// </summary>
	public static StoreGame? Parse(string? json, uint app, bool owned = false) {
		if (string.IsNullOrWhiteSpace(json)) {
			return null;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(json);

			if (!doc.RootElement.TryGetProperty(app.ToString(CultureInfo.InvariantCulture), out JsonElement node) || (node.ValueKind != JsonValueKind.Object)
				|| !node.TryGetProperty("success", out JsonElement ok) || (ok.ValueKind != JsonValueKind.True)
				|| !node.TryGetProperty("data", out JsonElement data) || (data.ValueKind != JsonValueKind.Object)) {
				return null;
			}

			string name = data.TryGetProperty("name", out JsonElement n) && (n.ValueKind == JsonValueKind.String) ? (n.GetString() ?? "").Trim() : "";
			string type = data.TryGetProperty("type", out JsonElement t) && (t.ValueKind == JsonValueKind.String) ? (t.GetString() ?? "") : "";
			bool free = data.TryGetProperty("is_free", out JsonElement f) && (f.ValueKind == JsonValueKind.True);
			bool age = Age(data);
			long price = 0;
			int currency = 0;

			if (data.TryGetProperty("price_overview", out JsonElement p) && (p.ValueKind == JsonValueKind.Object)) {
				long initial = p.TryGetProperty("initial", out JsonElement i) && i.TryGetInt64(out long iv) ? iv : 0;
				long final = p.TryGetProperty("final", out JsonElement v) && v.TryGetInt64(out long fv) ? fv : 0;

				// What the buyer pays today: a sale is the game's real price, and the package the cart adds is the one at
				// this price (not the regular one).
				price = Math.Max(0, final > 0 ? final : initial);
				currency = p.TryGetProperty("currency", out JsonElement c) && (c.ValueKind == JsonValueKind.String) ? Code(c.GetString()) : 0;
			}

			// A game is sold as a game. Anything else here (a bundle, a DLC, a demo) isn't bought on its own. A paid game
			// with no price and no package is one this account's region can't buy - Steam prices regionally, so it never
			// comes back in another currency.
			bool bundle = (type.Length > 0) && !type.Equals("game", StringComparison.OrdinalIgnoreCase);
			bool region = (type.Length == 0 || type.Equals("game", StringComparison.OrdinalIgnoreCase))
				&& !free && !age && (price <= 0) && !HasPackage(data);

			return new StoreGame(app, name, price, currency, free, age, bundle, owned, CountsForProfile: !free && !bundle && !region, RegionBlocked: region);
		} catch (JsonException) {
			return null;
		}
	}

	/// <summary>required_age, as a number or as text - 0 when the store doesn't ask.</summary>
	private static bool Age(JsonElement data) {
		if (!data.TryGetProperty("required_age", out JsonElement age)) {
			return false;
		}

		return age.ValueKind switch {
			JsonValueKind.Number when age.TryGetInt64(out long n) => n > 0,
			JsonValueKind.String when long.TryParse(age.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) => n > 0,
			_ => false
		};
	}

	/// <summary>Whether the app names a package that can sell it (the store answer's own purchase options).</summary>
	private static bool HasPackage(JsonElement data) {
		if (!data.TryGetProperty("package_groups", out JsonElement groups) || (groups.ValueKind != JsonValueKind.Array)) {
			return false;
		}

		return groups.EnumerateArray().Any(group => group.ValueKind == JsonValueKind.Object
			&& group.TryGetProperty("subs", out JsonElement subs) && (subs.ValueKind == JsonValueKind.Array) && (subs.GetArrayLength() > 0));
	}

	/// <summary>A store currency code ("CAD") as Steam's currency id, or 0 when it isn't one.</summary>
	private static int Code(string? code) =>
		!string.IsNullOrWhiteSpace(code) && Enum.TryParse(code, ignoreCase: true, out SteamKit2.ECurrencyCode parsed) && (parsed != SteamKit2.ECurrencyCode.Invalid)
			? (int) parsed
			: 0;
}
