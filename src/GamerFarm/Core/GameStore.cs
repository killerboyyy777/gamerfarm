using System.Globalization;
using System.Text.Json;

namespace NocatFarm.Core;

/// <summary>One game as the Steam store prices it right now, for deciding whether it's worth buying.</summary>
/// <param name="PriceCents">What it costs in <paramref name="Currency"/> (the store's own price for the account's country); 0 when it shows none.</param>
/// <param name="Currency">The store's currency id (SteamKit's ECurrencyCode), or 0 when the price didn't say.</param>
/// <param name="Free">Free to play, or free for good - not a paid game on a giveaway.</param>
/// <param name="AgeGated">The store asks for an age check before it will show the game.</param>
/// <param name="BundleOnly">Not sold as a game on its own: a bundle, DLC, demo, or a game with no price of its own.</param>
/// <param name="Owned">The account already has it (its library, or the store's own answer).</param>
public sealed record StoreGame(uint App, string Name, long PriceCents, int Currency, bool Free, bool AgeGated, bool BundleOnly, bool Owned);

/// <summary>
/// What buying games needs from the Steam store. A fake in the tests - no test ever reaches Steam. Stage 2 adds
/// <c>BuyAsync(StoreGame, ...)</c> here; it is deliberately not here yet.
/// </summary>
public interface IGameStore {
	/// <summary>The game's price and state now, in the wallet's currency (best effort). Null when the store didn't answer.</summary>
	Task<StoreGame?> GameAsync(uint app, int currency, CancellationToken ct);
}

/// <summary>
/// The Steam store, read for real: appdetails?filters=basic,price_overview in the account's country, so prices come back
/// in its own wallet currency. Read only - nothing here buys anything.
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

	/// <summary>
	/// One appdetails answer: the name, the price (today's or the regular one, whichever is higher - a sale is never
	/// taken for the game's real price), whether it's free, age-gated, or only sold in a bundle. Null when it isn't an
	/// answer about that app.
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

				price = Math.Max(0, Math.Max(initial, final));
				currency = p.TryGetProperty("currency", out JsonElement c) && (c.ValueKind == JsonValueKind.String) ? Code(c.GetString()) : 0;
			}

			// A game is sold as a game. Anything else here (a bundle, a DLC, a demo) isn't bought on its own; neither is a
			// game with no price and no package that could sell it.
			bool bundle = (type.Length > 0) && !type.Equals("game", StringComparison.OrdinalIgnoreCase);
			bundle |= !free && !age && (price <= 0) && !HasPackage(data);

			return new StoreGame(app, name, price, currency, free, age, bundle, owned);
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
