using System.Globalization;
using System.Text.Json;
using NocatFarm.Modules;

namespace NocatFarm.Core;

/// <summary>
/// Buying one game from the Steam wallet, the way ASFEnhance does it (October 2026): the account cart, the checkout's
/// inittransaction, the final price, then finalizetransaction - and only ever <c>PaymentMethod=steamaccount</c>.
/// </summary>
/// <remarks>
/// Money first, and no way out of the wallet: an external payment URL, a 3D Secure/bank challenge, a purchase review or
/// any other payment method stops it with nothing spent. The wallet balance is read before finalizing and again
/// afterwards; only a balance that went <em>down</em> is a purchase. Steam sometimes writes ids and money as strings,
/// so every number here is read either way. Nothing personal (the billing form) is ever logged.
/// </remarks>
internal sealed class GameCheckout(IGameCheckoutWeb web, long maxCents = 0) {
	internal static readonly Uri CheckoutRoot = new("https://checkout.steampowered.com");
	internal static readonly Uri StoreCart = new(WebSession.Store, "/cart/");

	private static readonly Uri CheckoutPage = new(CheckoutRoot, "/checkout/");
	private static readonly Uri InitTransaction = new(CheckoutRoot, "/checkout/inittransaction/");
	private static readonly Uri FinalizeTransaction = new(CheckoutRoot, "/checkout/finalizetransaction/");

	/// <summary>How long a finalized purchase is given to show on the wallet page.</summary>
	private static readonly TimeSpan SettleWait = TimeSpan.FromSeconds(4);

	public async Task<GamePurchase> BuyAsync(StoreGame game, CancellationToken ct) => await RunAsync(game, probe: false, steps: null, ct).ConfigureAwait(false);

	/// <summary>
	/// Follow the real checkout to its final price and stop there: no finalizetransaction, the transaction is cancelled.
	/// The same calls a purchase makes, so a live run proves the form, the country, the wallet method and the price before
	/// any money is at stake - ASFEnhance's fake purchase.
	/// </summary>
	public async Task<CheckoutProbe> ProbeAsync(StoreGame game, CancellationToken ct) {
		List<CheckoutStep> steps = [];
		GamePurchase answer = await RunAsync(game, probe: true, steps, ct).ConfigureAwait(false);

		return new CheckoutProbe(answer, steps);
	}

	private async Task<GamePurchase> RunAsync(StoreGame game, bool probe, List<CheckoutStep>? steps, CancellationToken ct) {
		void Step(string what, string detail) => steps?.Add(new CheckoutStep(what, detail));
		if (ct.IsCancellationRequested) {
			return new(GameBuyResult.Failed, "stopped before anything was sent");
		}

		if (game.Owned) {
			return new(GameBuyResult.Refused, "it already owns it");
		}

		if (game.Free) {
			return new(GameBuyResult.Refused, "it's free - there is nothing to buy");
		}

		if (game.AgeGated) {
			return new(GameBuyResult.Refused, "the store asks for an age check");
		}

		if (game.BundleOnly) {
			return new(GameBuyResult.Refused, "it isn't sold as a game on its own (a bundle, DLC or a demo)");
		}

		if (game.PriceCents <= 0) {
			return new(GameBuyResult.Refused, "the store gave no price");
		}

		if (await web.WalletAsync(ct).ConfigureAwait(false) is not { } wallet) {
			return new(GameBuyResult.Failed, "Steam didn't say what's in its wallet");
		}

		if (wallet.Currency <= 0) {
			return new(GameBuyResult.Refused, "it has no Steam wallet to buy with");
		}

		Step("wallet", $"{BuyPlan.Money(wallet.Balance, wallet.Currency)} ({MarketQuotes.Code(wallet.Currency)})");

		if (game.Currency != wallet.Currency) {
			return new(GameBuyResult.Refused, $"the store priced it in {MarketQuotes.Code(game.Currency)}, not the wallet's money");
		}

		if (wallet.Balance < game.PriceCents) {
			return new(GameBuyResult.InsufficientFunds,
				$"the wallet holds {BuyPlan.Money(wallet.Balance, wallet.Currency)}, less than its {BuyPlan.Money(game.PriceCents, wallet.Currency)}");
		}

		// The cart service needs a package, not an app id - unless it's a bundle, which goes in as itself. A package the
		// advisor found is already known, so it isn't looked up again.
		bool pack = game.PackageId is not null;
		bool bundle = !pack && (game.BundleApps is { Count: > 0 });
		uint package = game.PackageId ?? game.App;
		PackageInfo? info = null;

		if (pack) {
			info = await web.PackageInfoAsync(package, ct).ConfigureAwait(false);
		} else if (!bundle) {
			if (await web.PackageAsync(game.App, game.PriceCents, ct).ConfigureAwait(false) is not uint found || (found == 0)) {
				return new(GameBuyResult.Refused, "the store doesn't sell it in a package that can be added to the cart");
			}

			package = found;
			info = await web.PackageInfoAsync(package, ct).ConfigureAwait(false);
		}

		Step(bundle ? "bundle" : "package", bundle ? $"{game.App} (bundle)" : $"{package}" + (info is { Apps.Count: > 1 } multi ? $" ({multi.Name}, {multi.Apps.Count} apps)" : ""));

		string country = CountryOf(web.Country, wallet.Saved?.Country);

		// The checkout page is the referer every step below is asked from.
		if (await web.GetAsync(new Uri(CheckoutRoot, "/checkout/?accountcart=1"), StoreCart, ct).ConfigureAwait(false) == null) {
			return new(GameBuyResult.Failed, "the checkout page didn't answer");
		}

		Step("checkout", "page opened");

		if (!await web.AddToCartAsync(package, bundle, country, ct).ConfigureAwait(false)) {
			return new(GameBuyResult.Failed, "it couldn't add the game to the account cart");
		}

		Step("cart", $"added {(bundle ? "bundle" : "package")} {package}");

		try {
			// The store and the checkout have to agree on the wallet's country, or the final price comes in another money.
			if (country.Length == 2) {
				await web.SetCountryAsync(country, ct).ConfigureAwait(false);
			}

			Step("country", country.Length == 2 ? country : "not known");

			string? initBody = await web.PostAsync(InitTransaction, InitForm(wallet, country), CheckoutPage, ct).ConfigureAwait(false);
			InitAnswer init = ReadInit(initBody);

			if (!init.Ok) {
				return new(init.Stop ?? GameBuyResult.Failed, init.Reason);
			}

			string transId = init.TransId;
			Step("inittransaction", $"transaction {transId} with PaymentMethod=steamaccount");

			string? priceBody = await web.GetAsync(FinalPriceUri(transId), CheckoutPage, ct).ConfigureAwait(false);
			PriceAnswer price = ReadPrice(priceBody);

			if (price.ExternalUrl.Length > 0) {
				return new(GameBuyResult.ExternalPayment, "Steam wants the payment made outside the wallet - never followed, nothing was spent", 0, wallet.Currency, transId);
			}

			if (!price.Ok) {
				return new(GameBuyResult.Failed, price.Reason, 0, wallet.Currency, transId);
			}

			Step("getfinalprice", $"{BuyPlan.Money(price.TotalCents, price.Currency)} in {MarketQuotes.Code(price.Currency)}");

			if (price.Currency != wallet.Currency) {
				return new(GameBuyResult.Refused,
					price.Currency > 0
						? $"the checkout priced it in {MarketQuotes.Code(price.Currency)}, not the wallet's money"
						: "the checkout didn't say which money its price is in",
					0, wallet.Currency, transId);
			}

			// The cap is on what is really charged (tax included), not just the store's price.
			if ((maxCents > 0) && (price.TotalCents > maxCents)) {
				return new(GameBuyResult.Refused,
					$"the final price is {BuyPlan.Money(price.TotalCents, wallet.Currency)}, over the {BuyPlan.Money(maxCents, wallet.Currency)} most it may pay for one game - nothing was finalized",
					0, wallet.Currency, transId);
			}

			// The wallet is read afresh just before finalizing: this is what success is measured from.
			if (await web.WalletAsync(ct).ConfigureAwait(false) is not { } before) {
				return new(GameBuyResult.Failed, "Steam didn't say what's in its wallet before finalizing - nothing was finalized", 0, wallet.Currency, transId);
			}

			if (before.Currency != wallet.Currency) {
				return new(GameBuyResult.Refused, "the wallet changed money since the price was read - nothing was finalized", 0, wallet.Currency, transId);
			}

			if (before.Balance < price.TotalCents) {
				return new(GameBuyResult.InsufficientFunds,
					$"the wallet holds {BuyPlan.Money(before.Balance, before.Currency)}, less than the {BuyPlan.Money(price.TotalCents, before.Currency)} checkout asks - nothing was finalized",
					0, before.Currency, transId);
			}

			Step("wallet check", $"{BuyPlan.Money(before.Balance, before.Currency)} covers it");

			// A probe stops here: the transaction is cancelled instead of finalized. Nothing can be charged.
			if (probe) {
				bool cancelled = await web.CancelAsync(transId, ct).ConfigureAwait(false);
				Step("cancel", cancelled ? "transaction cancelled, nothing was bought" : "Steam didn't confirm the cancellation");

				return new(GameBuyResult.Probed,
					cancelled
						? $"dry run: the real checkout reached {BuyPlan.Money(price.TotalCents, price.Currency)} on the wallet, then the transaction was cancelled - nothing was bought"
						: $"dry run: the real checkout reached {BuyPlan.Money(price.TotalCents, price.Currency)}, but the transaction couldn't be cancelled - check it on Steam",
					0, wallet.Currency, transId, Apps: info?.Apps ?? game.BundleApps, PackageName: info?.Name ?? (bundle || pack ? game.Name : null));
			}

			// From here the purchase may go through: whatever the answer, it counts as spent until the wallet says otherwise.
			bool sent = true;
			string? finalizeBody = await web.PostAsync(FinalizeTransaction,
				new Dictionary<string, string>(StringComparer.Ordinal) {
					["transid"] = transId,
					["CardCVV2"] = "",
					["browserInfo"] = "{}"
				}, CheckoutPage, ct).ConfigureAwait(false);

			if (finalizeBody != null) {
				OutcomeAnswer finalize = ReadOutcome(finalizeBody);

				if (finalize.External) {
					return new(GameBuyResult.ExternalPayment, finalize.Text, 0, wallet.Currency, transId, Sent: sent);
				}
			}

			// Sent: finding out what it did runs on its own clock now, whatever the caller does.
			await web.DelayAsync(SettleWait, CancellationToken.None).ConfigureAwait(false);

			string? statusBody = await web.GetAsync(StatusUri(transId), CheckoutPage, CancellationToken.None).ConfigureAwait(false);
			OutcomeAnswer status = ReadOutcome(statusBody);

			// The balance alone is not proof: another purchase on the same wallet could fall in the settle window. Steam's own
			// answer for this transaction must say it went through too; a review may well have charged, so it stays Review.
			var after = await web.WalletAsync(CancellationToken.None).ConfigureAwait(false);

			if (status.Ok && after is { } then && (then.Currency == before.Currency) && (before.Balance - then.Balance >= price.TotalCents)) {
				// The finalized total is what this game cost; the rest of the drop may be another purchase.
				return new(GameBuyResult.Done, "", price.TotalCents, then.Currency, transId,
					Apps: info?.Apps ?? game.BundleApps, PackageName: info?.Name ?? (bundle || pack ? game.Name : null));
			}

			// Sent but not confirmed: PaidCents stays 0 (nothing proven), and the caller keeps the planned amount counted
			// against its limits until a later run settles it.
			return status.Review
				? new(GameBuyResult.Review, $"Steam wants this purchase reviewed or approved - {status.Text}", 0, wallet.Currency, transId, Sent: sent)
				: new(GameBuyResult.Failed, $"the wallet didn't go down by the whole price - {status.Text}", 0, wallet.Currency, transId, Sent: sent);
		} finally {
			// Best effort, and never the reason a purchase looks failed: an empty cart is the next look's starting point.
			try {
				await web.ClearCartAsync(CancellationToken.None).ConfigureAwait(false);
			} catch (Exception e) {
				Log.Debug(new Said("couldn't clear the account cart after buying a game: {0}", Log.Describe(e)));
			}
		}
	}

	/// <summary>The inittransaction form, field for field as Steam's own checkout sends it - wallet only, billing details from the account.</summary>
	internal static Dictionary<string, string> InitForm(CardMarket.Wallet wallet, string country) {
		CardMarket.Billing billing = wallet.Saved ?? new CardMarket.Billing();

		return new(StringComparer.Ordinal) {
			["gidShoppingCart"] = "-1",
			["gidReplayOfTransID"] = "-1",
			["bUseAccountCart"] = "1",
			["PaymentMethod"] = "steamaccount",
			["abortPendingTransactions"] = "0",
			["bHasCardInfo"] = "0",
			["CardNumber"] = "",
			["CardExpirationYear"] = "",
			["CardExpirationMonth"] = "",
			["FirstName"] = billing.FirstName,
			["LastName"] = billing.LastName,
			["Address"] = billing.Address,
			["AddressTwo"] = billing.AddressTwo,
			["Country"] = billing.Country.Length > 0 ? billing.Country : country,
			["City"] = billing.City,
			["State"] = billing.State,
			["PostalCode"] = billing.PostalCode,
			["Phone"] = "",
			["ShippingFirstName"] = "",
			["ShippingLastName"] = "",
			["ShippingAddress"] = "",
			["ShippingAddressTwo"] = "",
			["ShippingCountry"] = "",
			["ShippingCity"] = "",
			["ShippingState"] = "",
			["ShippingPostalCode"] = "",
			["ShippingPhone"] = "",
			["bIsGift"] = "0",
			["GifteeAccountID"] = "",
			["GifteeEmail"] = "",
			["GifteeName"] = "",
			["GiftMessage"] = "",
			["Sentiment"] = "",
			["Signature"] = "",
			["ScheduledSendOnDate"] = "0",
			["BankCode"] = "",
			["BankIBAN"] = "",
			["BankBIC"] = "",
			["TPBankID"] = "",
			["BankAccountID"] = "",
			["bSaveBillingAddress"] = "1",
			["gidPaymentID"] = "",
			["bUseRemainingSteamAccount"] = "0",
			["bPreAuthOnly"] = "0"
		};
	}

	/// <summary>Which country both hosts are set to: the sign-in's, else the billing one. Empty when neither is known.</summary>
	internal static string CountryOf(string signIn, string? billing) {
		string country = signIn.Trim();

		if (country.Length != 2) {
			country = (billing ?? "").Trim();
		}

		return country.Length == 2 ? country.ToUpperInvariant() : "";
	}

	private static Uri FinalPriceUri(string transId) =>
		new(CheckoutRoot, $"/checkout/getfinalprice/?count=1&transid={Uri.EscapeDataString(transId)}&purchasetype=self&microtxnid=-1&cart=-1&gidReplayOfTransID=-1");

	private static Uri StatusUri(string transId) =>
		new(CheckoutRoot, $"/checkout/transactionstatus/?count=1&transid={Uri.EscapeDataString(transId)}");

	/// <summary>An inittransaction answer: the transaction's id, or why it stopped.</summary>
	internal readonly record struct InitAnswer(bool Ok, string TransId, GameBuyResult? Stop, string Reason);

	/// <summary>
	/// An inittransaction answer, read. Steam writes numbers as numbers or as strings. An external URL or a non-wallet
	/// payment method is a stop; so is a review, an unfunded wallet, or a plain refusal.
	/// </summary>
	internal static InitAnswer ReadInit(string? body) {
		if (string.IsNullOrWhiteSpace(body)) {
			return new(false, "", GameBuyResult.Failed, "Steam didn't answer the checkout");
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(body);
			JsonElement root = doc.RootElement;

			if (root.ValueKind != JsonValueKind.Object) {
				return new(false, "", GameBuyResult.Failed, "Steam didn't answer the checkout");
			}

			long success = Code(root, "success") ?? 0;
			long detail = Code(root, "purchaseresultdetail") ?? 0;
			string external = Text(root, "externalurl") + Text(root, "authorizationurl");
			string method = Text(root, "paymentmethod");

			if (external.Length > 0) {
				return new(false, "", GameBuyResult.ExternalPayment, "Steam wants the payment made outside the wallet - never followed");
			}

			// The method is usually a number here (not a name); a name is only ever accepted when it's the wallet's own.
			if ((method.Length > 0) && !long.TryParse(method, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
				&& !method.Equals("steamaccount", StringComparison.OrdinalIgnoreCase)) {
				return new(false, "", GameBuyResult.Review, $"Steam chose another payment method ({method}) - nothing was finalized");
			}

			if ((detail == 2) || (success == 107)) {
				return new(false, "", GameBuyResult.InsufficientFunds, "the wallet doesn't hold enough for it - nothing was finalized");
			}

			if ((success == 22) || (detail is 1 or 11 or 29)) {
				return new(false, "", GameBuyResult.Review, $"Steam wants the purchase reviewed or approved ({Detail(detail)}) - nothing was finalized");
			}

			if (success != 1) {
				return new(false, "", GameBuyResult.Refused, detail > 0 ? $"Steam refused it: {Detail(detail)}" : $"Steam refused it (result {success})");
			}

			string transId = Text(root, "transid");

			if (transId.Length == 0) {
				transId = Text(root, "transactionid");
			}

			return transId.Length == 0
				? new(false, "", GameBuyResult.Failed, "Steam gave no transaction id")
				: new(true, transId, null, "");
		} catch (JsonException) {
			return new(false, "", GameBuyResult.Failed, "Steam didn't answer the checkout");
		}
	}

	/// <summary>A getfinalprice answer: the total, its money, and an external payment URL when Steam wants one.</summary>
	internal readonly record struct PriceAnswer(bool Ok, long TotalCents, int Currency, string ExternalUrl, string Reason);

	/// <summary>
	/// A getfinalprice answer, read. The total is base + tax - discount, in the smallest unit. A missing total, or an
	/// external URL, is said rather than guessed at.
	/// </summary>
	internal static PriceAnswer ReadPrice(string? body) {
		if (string.IsNullOrWhiteSpace(body)) {
			return new(false, 0, 0, "", "Steam didn't give the final price");
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(body);
			JsonElement root = doc.RootElement;

			if ((root.ValueKind != JsonValueKind.Object) || (Code(root, "success") != 1)) {
				return new(false, 0, 0, "", "Steam didn't give the final price");
			}

			int currency = (int) Math.Clamp(Code(root, "currencycode") ?? 0, 0, int.MaxValue);
			string external = Text(root, "externalurl") + Text(root, "authorizationurl");
			long? basePrice = Money(root, "base");

			if (external.Length > 0) {
				return new(true, basePrice ?? 0, currency, external, "");
			}

			if (basePrice is not long total) {
				return new(false, 0, currency, "", "the final price is missing");
			}

			return new(true, Math.Max(0, total + (Money(root, "tax") ?? 0) - (Money(root, "discount") ?? 0)), currency, "", "");
		} catch (JsonException) {
			return new(false, 0, 0, "", "Steam didn't give the final price");
		}
	}

	/// <summary>What finalizetransaction or transactionstatus said, in one line, and whether it wants a review or another payment route.</summary>
	internal readonly record struct OutcomeAnswer(bool External, bool Review, string Text, bool Ok = false);

	internal static OutcomeAnswer ReadOutcome(string? body) {
		if (string.IsNullOrWhiteSpace(body)) {
			return new(false, false, "Steam didn't say how the transaction ended");
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(body);
			JsonElement root = doc.RootElement;

			if (root.ValueKind != JsonValueKind.Object) {
				return new(false, false, "Steam didn't say how the transaction ended");
			}

			long success = Code(root, "success") ?? 0;
			long detail = Code(root, "purchaseresultdetail") ?? 0;
			bool ok = (success == 1) && (detail == 0);
			string external = Text(root, "externalurl") + Text(root, "authorizationurl");
			long receipt = 0;
			string total = "";

			if (TryGet(root, "purchasereceipt", out JsonElement r) && (r.ValueKind == JsonValueKind.Object)) {
				receipt = Code(r, "purchasestatus") ?? 0;
				total = Text(r, "formattedTotal");
			}

			bool review = (success == 22) || (detail is 1 or 11 or 29);

			if (external.Length > 0) {
				return new(true, review, "Steam wants the payment made outside the wallet");
			}

			List<string> said = [$"result {success}"];

			if (detail > 0) {
				said.Add($"detail {detail} ({Detail(detail)})");
			}

			if (receipt > 0) {
				said.Add($"purchase status {receipt}");
			}

			if (total.Length > 0) {
				said.Add($"total {total}");
			}

			return new(false, review, string.Join(", ", said), ok);
		} catch (JsonException) {
			return new(false, false, "Steam didn't say how the transaction ended");
		}
	}

	/// <summary>Steam's purchase-result detail in a few words (the ones that decide what happened here).</summary>
	internal static string Detail(long detail) => detail switch {
		1 => "the card issuer's check failed",
		2 => "not enough money in the wallet",
		6 => "that payment method can't be used",
		9 => "it's already owned",
		11 => "Steam's fraud check",
		13 => "not sold in this country",
		16 => "Steam wants another payment method",
		19 => "the region isn't supported",
		23 => "the store's billing country doesn't match",
		29 => "Steam wants the purchase approved first",
		31 => "the wallet's money doesn't match the price",
		53 => "Steam is rate-limiting purchases",
		_ => $"reason {detail}"
	};

	private static bool TryGet(JsonElement root, string name, out JsonElement value) {
		foreach (JsonProperty property in root.EnumerateObject()) {
			if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) {
				value = property.Value;

				return true;
			}
		}

		value = default;

		return false;
	}

	/// <summary>A JSON value Steam may send as text, a number or a flag; empty when it isn't there.</summary>
	private static string Text(JsonElement root, string name) {
		if (!TryGet(root, name, out JsonElement value)) {
			return "";
		}

		return value.ValueKind switch {
			JsonValueKind.String => (value.GetString() ?? "").Trim(),
			JsonValueKind.Number => value.GetRawText(),
			_ => ""
		};
	}

	/// <summary>A number Steam may send as a number or as text. Null when it isn't there.</summary>
	private static long? Code(JsonElement root, string name) {
		if (!TryGet(root, name, out JsonElement value)) {
			return null;
		}

		return value.ValueKind switch {
			JsonValueKind.Number when value.TryGetInt64(out long n) => n,
			JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) => n,
			JsonValueKind.True => 1,
			_ => null
		};
	}

	/// <summary>Money as Steam writes it ("999", "9.99", "$9.99") in cents. Null when there's no number in it.</summary>
	private static long? Money(JsonElement root, string name) {
		string text = Text(root, name);

		if (text.Length == 0) {
			return null;
		}

		string digits = new([.. text.Where(char.IsAsciiDigit)]);

		return (digits.Length == 0) || (digits.Length > 15) || !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long n)
			? null
			: n;
	}
}

/// <summary>One call a checkout probe made, and what came back.</summary>
public sealed record CheckoutStep(string Name, string Detail);

/// <summary>A Steam package: the name it is sold under, and every app it grants - a multi-game pack has several.</summary>
public sealed record PackageInfo(string Name, IReadOnlyList<uint> Apps);

/// <summary>A dry run of the checkout: the never-finalized answer, and every call it made.</summary>
public sealed record CheckoutProbe(GamePurchase Answer, IReadOnlyList<CheckoutStep> Steps);

/// <summary>What buying a game needs from Steam's web session and the account's wallet. A scripted fake in the tests.</summary>
internal interface IGameCheckoutWeb {
	/// <summary>The country Steam signed the account in to ("US", "CA"...), or empty.</summary>
	string Country { get; }

	/// <summary>The wallet, read now, with the billing details saved on the account. Null when Steam wouldn't say.</summary>
	Task<CardMarket.Wallet?> WalletAsync(CancellationToken ct);

	/// <summary>The package that sells the app at that price, or null when the store names none.</summary>
	Task<uint?> PackageAsync(uint app, long priceCents, CancellationToken ct);

	/// <summary>The package's name and every app it grants (a multi-game pack has several). Null when unknown.</summary>
	Task<PackageInfo?> PackageInfoAsync(uint packageId, CancellationToken ct) => Task.FromResult<PackageInfo?>(null);

	Task<string?> GetAsync(Uri url, Uri referer, CancellationToken ct);

	Task<string?> PostAsync(Uri url, Dictionary<string, string> form, Uri referer, CancellationToken ct);

	/// <summary>Cancel a transaction the probe started instead of finalizing it. True when Steam said it was cancelled.</summary>
	Task<bool> CancelAsync(string transId, CancellationToken ct) => Task.FromResult(false);

	/// <summary>Add the package (or the bundle) to the account cart. False when Steam didn't take it.</summary>
	Task<bool> AddToCartAsync(uint id, bool bundle, string country, CancellationToken ct);

	/// <summary>Empty the account cart, best effort.</summary>
	Task ClearCartAsync(CancellationToken ct);

	/// <summary>Set the wallet's country on the checkout and the store.</summary>
	Task SetCountryAsync(string country, CancellationToken ct);

	Task DelayAsync(TimeSpan wait, CancellationToken ct);
}

/// <summary>The account's own web session, wallet and store - the real checkout.</summary>
internal sealed class BotCheckoutWeb(Bot bot) : IGameCheckoutWeb {
	public string Country => bot.Country;

	public Task<CardMarket.Wallet?> WalletAsync(CancellationToken ct) => new SteamCardMarket(bot).WalletAsync(ct);

	public async Task<uint?> PackageAsync(uint app, long priceCents, CancellationToken ct) {
		string country = bot.Country.Length == 2 ? "&cc=" + bot.Country : "";
		string url = $"https://store.steampowered.com/api/appdetails?appids={app.ToString(CultureInfo.InvariantCulture)}&l=english{country}";
		(int status, string? body) = await GameCatalog.StoreGetAsync(url, TimeSpan.FromSeconds(1.5), ct).ConfigureAwait(false);

		return status == 200 ? SteamGameStore.ReadPackage(body, app, priceCents) : null;
	}

	private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, (DateTime At, PackageInfo Info)> PackageInfoKept = new();

	/// <summary>
	/// The package's name and every app it grants, from packagedetails: a 0.49 pack can be four games. Kept a week per
	/// package; null when the store didn't answer.
	/// </summary>
	public async Task<PackageInfo?> PackageInfoAsync(uint packageId, CancellationToken ct) {
		if (PackageInfoKept.TryGetValue(packageId, out (DateTime At, PackageInfo Info) kept) && (DateTime.UtcNow - kept.At < TimeSpan.FromDays(7))) {
			return kept.Info;
		}

		string country = bot.Country.Length == 2 ? "&cc=" + bot.Country : "";
		string url = $"https://store.steampowered.com/api/packagedetails?packageids={packageId.ToString(CultureInfo.InvariantCulture)}&l=english{country}";
		(int status, string? body) = await GameCatalog.StoreGetAsync(url, TimeSpan.FromSeconds(1.5), ct).ConfigureAwait(false);

		if ((status != 200) || (ParsePackage(body, packageId) is not { } info)) {
			return null;
		}

		PackageInfoKept[packageId] = (DateTime.UtcNow, info);

		return info;
	}

	/// <summary>The name a package is sold under and the apps it grants, from a packagedetails answer. Null when unknown.</summary>
	public static PackageInfo? ParsePackage(string? json, uint packageId) =>
		SteamGameStore.PackageContents(json).TryGetValue(packageId, out PackageInfo? info) ? info : null;

	public Task<string?> GetAsync(Uri url, Uri referer, CancellationToken ct) => bot.Web.GetFromAsync(url, referer, ct);

	public Task<string?> PostAsync(Uri url, Dictionary<string, string> form, Uri referer, CancellationToken ct) =>
		bot.Web.PostAllowingFailureAsync(url, form, referer, ct);

	public async Task<bool> CancelAsync(string transId, CancellationToken ct) {
		Uri page = new(GameCheckout.CheckoutRoot, "/checkout/");
		string? body = await bot.Web.PostAllowingFailureAsync(new Uri(GameCheckout.CheckoutRoot, "/checkout/canceltransaction/"),
			new Dictionary<string, string>(StringComparer.Ordinal) { ["transid"] = transId }, page, ct).ConfigureAwait(false);

		Log.Debug(new Said("canceltransaction for {0}: {1}", transId, Log.Scrub(body ?? "(no answer)")));

		if (body == null) {
			return false;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(body);

			if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("success", out JsonElement success)) {
				return success.ValueKind switch {
					JsonValueKind.True => true,
					JsonValueKind.False => false,
					JsonValueKind.Number when success.TryGetInt64(out long n) => n != 0,
					JsonValueKind.String => !success.GetString()!.Equals("false", StringComparison.OrdinalIgnoreCase),
					_ => true
				};
			}
		} catch (JsonException) {
			// Not JSON: Steam answered something, and the transaction is abandoned either way; a missing answer is the failure.
		}

		return true;
	}

	public async Task<bool> AddToCartAsync(uint id, bool bundle, string country, CancellationToken ct) {
		// The cart service takes its JSON as a form field (input_json), so it goes over the ordinary form POST with the
		// account's access token - no separate JSON body is needed. A bundle is its own cart item: bundleid, not packageid.
		object item = bundle
			? new { bundleid = id, flags = new { is_gift = false, is_private = false } }
			: new { packageid = id, flags = new { is_gift = false, is_private = false } };
		string json = JsonSerializer.Serialize(new {
			user_country = country,
			items = new[] { item },
			navdata = new {
				domain = "store.steampowered.com",
				controller = "default",
				method = "default",
				submethod = "",
				feature = "spotlight",
				depth = 1,
				countrycode = country,
				webkey = 0,
				is_client = false,
				curator_data = new { clanid = (string?) null, listid = (string?) null },
				is_likely_bot = false,
				is_utm = false
			}
		});

		string? body = await bot.Web.ApiPostAsync("IAccountCartService", "AddItemsToCart",
			new Dictionary<string, string>(StringComparer.Ordinal) { ["input_json"] = json }, ct).ConfigureAwait(false);

		return (body != null)
			&& (body.Contains("\"cart\"", StringComparison.Ordinal) || body.Contains("\"line_item_ids\"", StringComparison.Ordinal));
	}

	public async Task ClearCartAsync(CancellationToken ct) {
		await bot.Web.ApiPostAsync("IAccountCartService", "DeleteCart", null, ct).ConfigureAwait(false);
	}

	public async Task SetCountryAsync(string country, CancellationToken ct) {
		Dictionary<string, string> form = new(StringComparer.Ordinal) { ["cc"] = country };

		await bot.Web.PostAsync(new Uri(GameCheckout.CheckoutRoot, "/country/setcountry"), form, null, ct).ConfigureAwait(false);
		await bot.Web.PostAsync(new Uri(WebSession.Store, "/country/setcountry"), form, null, ct).ConfigureAwait(false);
	}

	public Task DelayAsync(TimeSpan wait, CancellationToken ct) => Task.Delay(wait, ct);
}
