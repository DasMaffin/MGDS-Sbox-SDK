namespace DasMaffin.Mgds;

public sealed class LeaderboardTier
{
	public string Name { get; set; }
	public double Cutoff { get; set; }
}

/// <summary>A board and its settings.</summary>
public sealed class Leaderboard
{
	public string Id { get; set; }
	public string Name { get; set; }

	/// <summary>desc: highest first; asc: lowest first.</summary>
	public string SortOrder { get; set; }

	/// <summary>keepBest, keepLatest or aggregate.</summary>
	public string UpdateType { get; set; }

	/// <summary>Scores come from the game's own server only; players cannot submit.</summary>
	public bool ServerOnly { get; set; }

	public int? BucketSize { get; set; }

	/// <summary>never, daily, weekly or monthly.</summary>
	public string Reset { get; set; }

	public DateTime? NextResetAt { get; set; }
	public int Version { get; set; }
	public DateTime VersionStartedAt { get; set; }
	public List<LeaderboardTier> Tiers { get; set; } = new();
}

/// <summary>One player's place on a board.</summary>
public sealed class LeaderboardEntry
{
	public string PlayerId { get; set; }
	public string PlayerName { get; set; }

	/// <summary>0 for the top.</summary>
	public int Rank { get; set; }

	public double Score { get; set; }

	/// <summary>The best tier the score reaches, or null.</summary>
	public string Tier { get; set; }

	public int? Bucket { get; set; }

	/// <summary>The JSON sent with the score, or null.</summary>
	public JsonNode Metadata { get; set; }

	public DateTime UpdatedAt { get; set; }

	/// <summary>After submitting: false when a keepBest board kept the earlier, better score.</summary>
	public bool? Updated { get; set; }

	/// <summary>After submitting: the board's version the score went into.</summary>
	public int? Version { get; set; }

	public T GetMetadata<T>( string key, T fallback = default ) =>
		Metadata is JsonObject fields && fields.TryGetPropertyValue( key, out JsonNode value ) && value is not null ? MgdsJson.To<T>( value ) : fallback;
}

/// <summary>A page of standings, best first.</summary>
public sealed class LeaderboardPage
{
	public int Version { get; set; }
	public int? Bucket { get; set; }

	/// <summary>How many entries the board (or bucket) has in all.</summary>
	public int Total { get; set; }

	public int Offset { get; set; }
	public int Limit { get; set; }
	public List<LeaderboardEntry> Results { get; set; } = new();
}

public sealed class LeaderboardVersion
{
	public int Version { get; set; }
	public DateTime StartedAt { get; set; }
	public DateTime? EndedAt { get; set; }
}

public sealed class LeaderboardVersions
{
	public LeaderboardVersion Current { get; set; }
	public List<LeaderboardVersion> Archived { get; set; } = new();
}

/// <summary>
/// Leaderboards, set up on the dashboard. Reading needs nobody signed in; submitting is the
/// signed-in player's own score.
/// </summary>
public sealed class MgdsLeaderboards
{
	/// <summary>The most entries one page holds.</summary>
	public const int MostPerPage = 100;

	private readonly MgdsManager _manager;

	internal MgdsLeaderboards( MgdsManager manager ) => _manager = manager;

	// --- the signed-in player --------------------------------------------------------------------

	/// <summary>
	/// Submits the signed-in player's score. What happens to it is the board's rule: kept when
	/// better, always kept, or added up. <paramref name="metadata"/> is any JSON kept with it, up to 2 KB.
	/// </summary>
	public async Task<LeaderboardEntry> SubmitScoreAsync( string board, double score, object metadata = null, CancellationToken cancel = default )
	{
		var body = new JsonObject { ["score"] = score };
		if ( metadata is not null )
			body["metadata"] = MgdsJson.From( metadata );

		return (await _manager.Http.JsonAsync( "POST", Board( board ) + "/scores/me", body, Token.Required, cancel )).As<LeaderboardEntry>();
	}

	/// <summary>The signed-in player's entry and rank, or null when they have no score yet.</summary>
	public Task<LeaderboardEntry> GetMyScoreAsync( string board, CancellationToken cancel = default ) =>
		OrNullAsync( Board( board ) + "/scores/me", Token.Required, cancel );

	/// <summary>The signed-in player and up to <paramref name="around"/> entries either side.</summary>
	public async Task<List<LeaderboardEntry>> GetMyRangeAsync( string board, int around = 5, CancellationToken cancel = default ) =>
		Results( await _manager.Http.JsonAsync( "GET", $"{Board( board )}/scores/me/range?rangeLimit={around}", null, Token.Required, cancel ) );

	/// <summary>The standings of the signed-in player's bucket, on a bucketed board.</summary>
	public async Task<LeaderboardPage> GetMyBucketAsync( string board, int offset = 0, int limit = 10, CancellationToken cancel = default ) =>
		(await _manager.Http.JsonAsync( "GET", $"{Board( board )}/scores/me/bucket?offset={offset}&limit={limit}", null, Token.Required, cancel ))
			.As<LeaderboardPage>();

	// --- anybody ---------------------------------------------------------------------------------

	/// <summary>Every board and its settings.</summary>
	public async Task<List<Leaderboard>> GetBoardsAsync( CancellationToken cancel = default ) =>
		(await _manager.Http.JsonAsync( "GET", "/leaderboards", null, Token.None, cancel )).As<List<Leaderboard>>();

	public async Task<Leaderboard> GetBoardAsync( string board, CancellationToken cancel = default ) =>
		(await _manager.Http.JsonAsync( "GET", Board( board ), null, Token.None, cancel )).As<Leaderboard>();

	/// <summary>
	/// A page of standings, best first. On a bucketed board, <paramref name="bucket"/> picks one;
	/// left out, a signed-in player gets their own. <paramref name="version"/> reads an archived one.
	/// </summary>
	public async Task<LeaderboardPage> GetScoresAsync( string board, int offset = 0, int limit = 10, int? bucket = null, int? version = null,
		CancellationToken cancel = default )
	{
		string path = version is null ? Board( board ) + "/scores" : $"{Board( board )}/versions/{version}/scores";
		path += $"?offset={offset}&limit={Math.Min( limit, MostPerPage )}" + (bucket is null ? "" : "&bucket=" + bucket);
		return (await _manager.Http.JsonAsync( "GET", path, null, Token.IfSignedIn, cancel )).As<LeaderboardPage>();
	}

	/// <summary>One player's entry and rank, or null when they have none.</summary>
	public Task<LeaderboardEntry> GetPlayerScoreAsync( string board, string playerId, int? version = null, CancellationToken cancel = default ) =>
		OrNullAsync( $"{Board( board )}/scores/players/{MgdsHttp.Escape( playerId )}" + Version( version, "?" ), Token.None, cancel );

	/// <summary>A player and up to <paramref name="around"/> entries either side.</summary>
	public async Task<List<LeaderboardEntry>> GetPlayerRangeAsync( string board, string playerId, int around = 5, int? version = null,
		CancellationToken cancel = default ) =>
		Results( await _manager.Http.JsonAsync( "GET",
			$"{Board( board )}/scores/players/{MgdsHttp.Escape( playerId )}/range?rangeLimit={around}" + Version( version, "&" ), null, Token.None, cancel ) );

	/// <summary>Several players at once - a friends list - each with their rank. Those without a score are left out.</summary>
	public async Task<List<LeaderboardEntry>> GetPlayersScoresAsync( string board, IEnumerable<string> playerIds, int? version = null,
		CancellationToken cancel = default )
	{
		string ids = string.Join( ",", playerIds.Take( MostPerPage ).Select( MgdsHttp.Escape ) );
		return Results( await _manager.Http.JsonAsync( "GET", $"{Board( board )}/scores/players?ids={ids}" + Version( version, "&" ), null, Token.None, cancel ) );
	}

	/// <summary>The current version and every archived one.</summary>
	public async Task<LeaderboardVersions> GetVersionsAsync( string board, CancellationToken cancel = default ) =>
		(await _manager.Http.JsonAsync( "GET", Board( board ) + "/versions", null, Token.None, cancel )).As<LeaderboardVersions>();

	// ---------------------------------------------------------------------------------------------

	private async Task<LeaderboardEntry> OrNullAsync( string path, Token token, CancellationToken cancel )
	{
		MgdsResponse answer = await _manager.Http.JsonAsync( "GET", path, null, token, cancel, 404 );
		if ( answer.Status != 404 )
			return answer.As<LeaderboardEntry>();

		// "no such leaderboard" (or project) is a mistake; no score yet is not.
		MgdsException failure = MgdsHttp.Failure( answer );
		if ( failure.Error.StartsWith( "no such", StringComparison.Ordinal ) )
			throw failure;

		return null;
	}

	private static List<LeaderboardEntry> Results( MgdsResponse answer ) =>
		MgdsJson.To<List<LeaderboardEntry>>( answer.Json?["results"] ) ?? new();

	private static string Board( string board ) => "/leaderboards/" + MgdsHttp.Escape( board );

	private static string Version( int? version, string separator ) => version is null ? "" : separator + "version=" + version;
}
