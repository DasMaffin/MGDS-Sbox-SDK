namespace DasMaffin.Mgds;

/// <summary>The signed-in player, as MGDS describes them.</summary>
public sealed class MgdsPlayer
{
	public string PlayerId { get; set; }
	public string DisplayName { get; set; }
	public DateTime CreatedAt { get; set; }

	/// <summary>The ways this player signs in: anonymous, steam, sbox, dasmaffin.</summary>
	public List<string> Logins { get; set; } = new();

	public DateTime AccessExpiresAt { get; set; }
	public DateTime SessionExpiresAt { get; set; }
}

internal sealed class MgdsSession
{
	public string PlayerId { get; set; }
	public string SessionToken { get; set; }
	public DateTime ExpiresAt { get; set; }
	public string RefreshToken { get; set; }
	public DateTime RefreshExpiresAt { get; set; }
	public string DisplayName { get; set; }
	public bool Created { get; set; }
	public string DeviceKey { get; set; }
	public string IdToken { get; set; }
}

internal sealed class BrowserSignIn
{
	public string RequestId { get; set; }
	public string PollToken { get; set; }
	public string Url { get; set; }
	public DateTime ExpiresAt { get; set; }
	public double PollEverySeconds { get; set; } = 2;
}

/// <summary>
/// Signing the player in and out. The session is kept on the device and refreshed by itself, so a
/// player who plays regularly never signs in twice; every other part of the SDK uses it.
/// </summary>
public sealed class MgdsAuth
{
	private const string RefreshKey = "refresh";
	private const string DeviceKey = "device";

	private readonly MgdsManager _manager;
	private MgdsSession _session;
	private Task _refreshing;

	internal MgdsAuth( MgdsManager manager ) => _manager = manager;

	/// <summary>Raised when a player is signed in: by any way in, or when a session is picked up again.</summary>
	public event Action SignedIn;

	/// <summary>Raised when the player is signed out: by the game, or because the session ended.</summary>
	public event Action SignedOut;

	public bool IsSignedIn => _session is not null;

	/// <summary>The signed-in player's id, or null.</summary>
	public string PlayerId => _session?.PlayerId;

	/// <summary>The signed-in player's name, when they have one.</summary>
	public string DisplayName => _session?.DisplayName;

	/// <summary>True when the last sign-in made a new player.</summary>
	public bool IsNewPlayer => _session?.Created ?? false;

	/// <summary>
	/// Says who the player is, signed by MGDS, for your own game server to check without asking
	/// MGDS. Renewed with every refresh; lasts an hour.
	/// </summary>
	public string IdToken => _session?.IdToken;

	/// <summary>When the session ends unless it is refreshed (the SDK refreshes it by itself while the game runs).</summary>
	public DateTime? SessionExpiresAt => _session?.RefreshExpiresAt;

	/// <summary>Whether a session from an earlier run is kept on this device.</summary>
	public bool HasStoredSession => _manager.Store.Get( RefreshKey ) is not null;

	internal string SessionToken => _session?.SessionToken;

	// --- signing in ------------------------------------------------------------------------------

	/// <summary>
	/// Picks up the session from the last run, if one was kept. True when it did. Called by the
	/// manager when the game starts, unless that is turned off.
	/// </summary>
	public async Task<bool> RestoreAsync( CancellationToken cancel = default )
	{
		if ( IsSignedIn )
			return true;

		string refresh = _manager.Store.Get( RefreshKey );
		if ( refresh is null )
			return false;

		try
		{
			await RefreshWithAsync( refresh, cancel );
			return true;
		}
		catch ( MgdsException e ) when ( e.IsUnauthorized )
		{
			_manager.Store.Set( RefreshKey, null );
			return false;
		}
	}

	/// <summary>
	/// Signs in with the player's s&amp;box account (their SteamID, proven by s&amp;box). The same
	/// player every time, on every computer. s&amp;box must be added as a provider on the project's
	/// Authenticate page; there is nothing else to set up.
	/// </summary>
	public async Task SignInWithSboxAsync( CancellationToken cancel = default )
	{
		MgdsResponse answer = await _manager.Http.JsonAsync( "POST", "/auth/sbox", await SboxProofAsync( cancel ), Token.None, cancel );
		Accept( answer.As<MgdsSession>() );
	}

	/// <summary>Signs in as this device's anonymous player, made the first time.</summary>
	/// <exception cref="MgdsException">401 when the device's key is unknown to the project.</exception>
	public async Task SignInAnonymouslyAsync( CancellationToken cancel = default )
	{
		var body = new JsonObject();
		string key = _manager.Store.Get( DeviceKey );
		if ( key is not null )
			body["deviceKey"] = key;

		MgdsResponse answer = await _manager.Http.JsonAsync( "POST", "/auth/anonymous", body, Token.None, cancel );
		Accept( answer.As<MgdsSession>() );
	}

	/// <summary>
	/// Signs in with a dasmaffin.com account: the player opens <c>url</c> in a browser and allows
	/// it, and this waits for them. s&amp;box cannot open a browser for the game, so
	/// <paramref name="showUrl"/> shows the address (and a button to copy it).
	/// </summary>
	public async Task SignInWithDasmaffinAsync( Action<string> showUrl, CancellationToken cancel = default )
	{
		JsonNode session = await BrowserAsync( "/auth/dasmaffin", Token.None, showUrl, cancel );
		Accept( MgdsJson.To<MgdsSession>( session ) );
	}

	// --- the session -----------------------------------------------------------------------------

	/// <summary>Gets a new access token now. The SDK does this by itself when one is about to run out.</summary>
	public Task RefreshAsync( CancellationToken cancel = default ) =>
		_session is null ? throw new MgdsException( 401, "not signed in" ) : RefreshWithAsync( _session.RefreshToken, cancel );

	/// <summary>Signs out: this session, or with <paramref name="everywhere"/> every session the player has.</summary>
	public async Task SignOutAsync( bool everywhere = false, CancellationToken cancel = default )
	{
		if ( _session is null )
			return;

		try
		{
			await _manager.Http.JsonAsync( "POST", everywhere ? "/auth/logout/all" : "/auth/logout", null, Token.Required, cancel );
		}
		finally
		{
			Forget();
		}
	}

	/// <summary>Who is signed in, and how they sign in, from MGDS.</summary>
	public async Task<MgdsPlayer> GetPlayerAsync( CancellationToken cancel = default ) =>
		(await _manager.Http.JsonAsync( "GET", "/me", null, Token.Required, cancel )).As<MgdsPlayer>();

	// --- more ways in ----------------------------------------------------------------------------

	/// <summary>Adds the s&amp;box account to the signed-in player. Returns their ways in now. 409 if it is another player's.</summary>
	public async Task<List<string>> LinkSboxAsync( CancellationToken cancel = default ) =>
		Logins( (await _manager.Http.JsonAsync( "POST", "/me/logins/sbox", await SboxProofAsync( cancel ), Token.Required, cancel )).Json );

	/// <summary>Adds a dasmaffin.com account to the signed-in player, through the browser.</summary>
	public async Task<List<string>> LinkDasmaffinAsync( Action<string> showUrl, CancellationToken cancel = default ) =>
		Logins( await BrowserAsync( "/me/logins/dasmaffin", Token.Required, showUrl, cancel ) );

	/// <summary>Removes a way in: anonymous, steam, sbox or dasmaffin. Never the last one (409).</summary>
	public async Task<List<string>> UnlinkAsync( string provider, CancellationToken cancel = default ) =>
		Logins( (await _manager.Http.JsonAsync( "DELETE", "/me/logins/" + MgdsHttp.Escape( provider ), null, Token.Required, cancel )).Json );

	// --- the player's own data -------------------------------------------------------------------

	/// <summary>Everything this project holds about the signed-in player, as one JSON document (GDPR).</summary>
	public async Task<JsonObject> ExportDataAsync( CancellationToken cancel = default ) =>
		(await _manager.Http.JsonAsync( "GET", "/me/export", null, Token.Required, cancel )).Json as JsonObject;

	/// <summary>
	/// Deletes the signed-in player and everything of theirs in this project, for good, and signs
	/// out. Ask the player to confirm first: it cannot be undone.
	/// </summary>
	public async Task DeletePlayerAsync( CancellationToken cancel = default )
	{
		await _manager.Http.JsonAsync( "DELETE", "/me", null, Token.Required, cancel );
		_manager.Store.Set( DeviceKey, null );
		Forget();
	}

	// --- inside the SDK --------------------------------------------------------------------------

	/// <summary>Refreshes first when the access token runs out within a minute.</summary>
	internal Task EnsureFreshAsync( CancellationToken cancel )
	{
		if ( _session is null || _session.ExpiresAt.ToUniversalTime() - DateTime.UtcNow > TimeSpan.FromMinutes( 1 ) )
			return Task.CompletedTask;

		return SharedRefreshAsync( cancel );
	}

	/// <summary>After a 401 with <paramref name="rejected"/>: true when there is a working token to try again with.</summary>
	internal async Task<bool> RecoverAsync( string rejected, CancellationToken cancel )
	{
		if ( _session is null )
			return false;

		if ( _session.SessionToken != rejected )
			return true;

		try
		{
			await SharedRefreshAsync( cancel );
			return _session is not null;
		}
		catch ( MgdsException e ) when ( e.IsUnauthorized )
		{
			return false;
		}
	}

	/// <summary>One refresh at a time: a refresh token is good once.</summary>
	private Task SharedRefreshAsync( CancellationToken cancel )
	{
		if ( _refreshing is null || _refreshing.IsCompleted )
			_refreshing = RefreshWithAsync( _session.RefreshToken, cancel );

		return _refreshing;
	}

	private async Task RefreshWithAsync( string refresh, CancellationToken cancel )
	{
		try
		{
			MgdsResponse answer = await _manager.Http.JsonAsync( "POST", "/auth/refresh", new JsonObject { ["refreshToken"] = refresh }, Token.None, cancel );
			Accept( answer.As<MgdsSession>() );
		}
		catch ( MgdsException e ) when ( e.IsUnauthorized )
		{
			Forget();
			throw;
		}
	}

	private void Accept( MgdsSession session )
	{
		bool same = _session is not null && _session.PlayerId == session.PlayerId;
		_session = session;

		if ( !string.IsNullOrEmpty( session.DeviceKey ) )
			_manager.Store.Set( DeviceKey, session.DeviceKey );

		_manager.Store.Set( RefreshKey, session.RefreshToken );

		if ( !same )
			SignedIn?.Invoke();
	}

	private void Forget()
	{
		bool was = _session is not null;
		_session = null;
		_manager.Store.Set( RefreshKey, null );

		if ( was )
			SignedOut?.Invoke();
	}

	/// <summary>A token from s&amp;box for the service "mgds", with the SteamID it is for. It lasts two minutes, so it is asked for just before use.</summary>
	private static async Task<JsonObject> SboxProofAsync( CancellationToken cancel )
	{
		string token = await Sandbox.Services.Auth.GetToken( "mgds", cancel );
		if ( string.IsNullOrEmpty( token ) )
			throw new MgdsException( 401, "s&box gave no sign-in token; is the player signed in to Steam?" );

		return new JsonObject { ["steamId"] = ((ulong)Game.SteamId).ToString(), ["token"] = token };
	}

	/// <summary>The browser flow: start, show the page, poll until they allow it. Returns the final answer.</summary>
	private async Task<JsonNode> BrowserAsync( string path, Token token, Action<string> showUrl, CancellationToken cancel )
	{
		if ( showUrl is null )
			throw new ArgumentNullException( nameof( showUrl ) );

		BrowserSignIn started = (await _manager.Http.JsonAsync( "POST", path, new JsonObject(), token, cancel )).As<BrowserSignIn>();
		showUrl( started.Url );

		var poll = new JsonObject { ["requestId"] = started.RequestId, ["pollToken"] = started.PollToken };
		int every = (int)(Math.Max( 1, started.PollEverySeconds ) * 1000);

		while ( true )
		{
			await Task.Delay( every, cancel );

			MgdsResponse answer = await _manager.Http.JsonAsync( "POST", path + "/poll", poll, token, cancel, 202 );
			if ( answer.Status != 202 )
				return answer.Json;
		}
	}

	private static List<string> Logins( JsonNode answer ) =>
		answer?["logins"]?.Deserialize<List<string>>( MgdsJson.Options ) ?? new List<string>();
}
