using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// What buying games has spent, per account - for the day's and the week's limits, and so they survive a restart - and
/// whether the owner has turned it on (its go-ahead). Both in config/state/buygame-&lt;account&gt;.json, beside the
/// account's spending, never in its settings: a settings file or a backup can't bring the go-ahead with it.
/// </summary>
/// <remarks>
/// Named buygame-, not buy-games-: the account's other spending list is buy-&lt;account&gt;.json, and a file starting
/// "buy-" would be read as belonging to an account called "games-&lt;name&gt;" when that account was removed.
/// </remarks>
public sealed class BuyGamesLedger(string path) {
	/// <param name="At">When, on Steam's clock.</param>
	/// <param name="Cents">What it cost, in the wallet's currency (a purchase whose outcome isn't known is counted all the same).</param>
	/// <param name="Currency">The wallet's currency when it was spent.</param>
	/// <param name="App">The game's app id.</param>
	/// <param name="Name">The game's name, as the store gave it.</param>
	/// <param name="Pending">Not known yet whether it was bought: counted as spent until a look finds out.</param>
	/// <param name="TransId">Steam's transaction id, once it gave one.</param>
	public sealed record Entry(DateTime At, long Cents, int Currency, uint App, string Name, bool Pending = false, string? TransId = null);

	internal sealed class Stored {
		public bool Ok { get; set; }
		public List<Entry> Entries { get; set; } = [];
	}

	private List<Entry>? _entries;
	private bool _ok, _onDisk, _unreadable;
	private readonly Lock _gate = new();

	private static readonly Dictionary<string, BuyGamesLedger> Loaded = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>One per account for the whole program, so 'games on' typed while a look runs isn't saved over.</summary>
	public static BuyGamesLedger Of(string account) {
		string file = Path.Combine(ConfigStore.ConfigDir, "state", $"buygame-{account}.json");

		lock (Loaded) {
			if (Loaded.TryGetValue(file, out BuyGamesLedger? held) && (!held._onDisk || File.Exists(file))) {
				return held;
			}

			BuyGamesLedger fresh = new(file);
			Loaded[file] = fresh;

			return fresh;
		}
	}

	/// <summary>As if the program had just started: every ledger is read from its file again.</summary>
	public static void ForgetLoaded() {
		lock (Loaded) {
			Loaded.Clear();
		}
	}

	/// <summary>Whether buying games has its go-ahead: turned on through its preview ('games &lt;account&gt; on yes').</summary>
	public bool Ok {
		get {
			lock (_gate) {
				Load();

				return _ok;
			}
		}
	}

	/// <summary>
	/// The file is there and can't be read. Nothing is bought - what was spent isn't known - and the file is never written
	/// over, so it can be looked at; fixed or deleted, the next look reads it again.
	/// </summary>
	public bool Unreadable {
		get {
			lock (_gate) {
				Load();

				return _unreadable;
			}
		}
	}

	/// <summary><see cref="Unreadable"/> in plain words (English, for the console).</summary>
	public string UnreadableText => $"the list of what it spent on games (config/state/{Path.GetFileName(path)}) can't be read, so nothing is bought until that file is fixed or deleted";

	public void SetOk(bool ok) {
		lock (_gate) {
			Load();
			_ok = ok;
			Save();
		}
	}

	/// <summary>What was spent since then, pending purchases included. An unreadable list counts as everything spent.</summary>
	public long SpentSince(DateTime utc) {
		lock (_gate) {
			List<Entry> all = Load();

			return _unreadable ? long.MaxValue / 4 : all.Where(e => e.At >= utc).Sum(static e => e.Cents);
		}
	}

	/// <summary>Write one down. False when it couldn't be saved (kept in memory either way).</summary>
	public bool Add(Entry entry) {
		lock (_gate) {
			List<Entry> all = Load();
			all.Add(entry);
			all.RemoveAll(static e => e.At < DateTime.UtcNow.AddDays(-60));   // two months is plenty for a week's limit

			return Save();
		}
	}

	/// <summary>Change one entry (found by its transaction id, else by value), or take it out (null). False when it couldn't be saved.</summary>
	public bool Replace(Entry old, Entry? now) {
		lock (_gate) {
			List<Entry> all = Load();
			int at = all.FindIndex(e => (old.TransId != null) && (e.TransId == old.TransId) ? true : e == old);

			if (at < 0) {
				return Save();
			}

			if (now == null) {
				all.RemoveAt(at);
			} else {
				all[at] = now;
			}

			return Save();
		}
	}

	/// <summary>Everything written down, the oldest first as it was added.</summary>
	public IReadOnlyList<Entry> All {
		get {
			lock (_gate) {
				return [.. Load()];
			}
		}
	}

	private bool Save() {
		if (_unreadable) {
			return false;   // never written over: whatever is in it stays for the owner to look at
		}

		try {
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			AtomicFile.Write(path, JsonSerializer.Serialize(new Stored { Ok = _ok, Entries = _entries ?? [] }));
			_onDisk = true;

			return true;
		} catch (Exception e) {
			// Kept in memory either way, so this run's limits still hold.
			Log.Debug(new Said("couldn't save the game buying list: {0}", Log.Describe(e)));

			return false;
		}
	}

	private List<Entry> Load() {
		if (_entries != null) {
			return _entries;
		}

		try {
			Stored? read = File.Exists(path) ? JsonSerializer.Deserialize<Stored>(File.ReadAllText(path)) ?? throw new JsonException("empty") : null;
			_onDisk = read != null;
			// A null in the list (a hand edit) is nothing written down; a purchase with no name is still money spent.
			_entries = [.. (read?.Entries ?? []).Where(static e => e != null).Select(static e => e.Name == null ? e with { Name = "" } : e)];
			_ok = read?.Ok ?? false;
		} catch (Exception e) {
			// A list that can't be read counts as everything already spent, rather than nothing spent - and no go-ahead.
			Log.Debug(new Said("couldn't read the game buying list: {0}", Log.Describe(e)));
			_entries = [];
			_unreadable = true;
			_onDisk = true;   // deleted, the next ask starts a fresh list
			_ok = false;
		}

		return _entries;
	}
}
