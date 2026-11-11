namespace DasMaffin.Mgds;

/// <summary>What the manager does about signing in when the game starts.</summary>
public enum MgdsAutoSignIn
{
	/// <summary>Nothing: the game signs the player in itself.</summary>
	None,

	/// <summary>Picks up the last session, or signs in with the player's s&amp;box account. The usual choice.</summary>
	Sbox,

	/// <summary>Picks up the last session, or signs in anonymously with this device.</summary>
	Anonymous,

	/// <summary>Only picks up the session from the last time the game ran, if there is one.</summary>
	RestoreOnly,
}

/// <summary>
/// The one MGDS object in a game. Put it on a root GameObject in your first scene, fill in the
/// project id, and use it from anywhere as <c>MgdsManager.Instance</c> (or the shorter
/// <see cref="Mgds"/>): its <see cref="Auth"/>, <see cref="Saves"/>, <see cref="Leaderboards"/>,
/// <see cref="Config"/>, <see cref="Reports"/> and <see cref="Push"/> do the rest.
/// </summary>
/// <remarks>
/// It survives scene changes. A second manager - in a scene loaded later - removes itself and the
/// first one carries on, so every scene can have one for testing on its own. Each player's own
/// game runs its own manager and signs its own player in.
/// </remarks>
[Title( "MGDS Manager" )]
[Category( "MGDS" )]
[Icon( "cloud" )]
public sealed class MgdsManager : Component
{
	public const string DefaultServer = "https://www.dasmaffin.com";

	/// <summary>The project's id, from its Configure page on dasmaffin.com/Mgds.</summary>
	[Property, Group( "Project" )] public string ProjectId { get; set; } = "";

	/// <summary>The remote config environment for config and push messages. Empty for the project's default one.</summary>
	[Property, Group( "Project" )] public string Environment { get; set; } = "";

	/// <summary>Whether the manager signs the player in by itself.</summary>
	[Property, Group( "When the game starts" )] public MgdsAutoSignIn AutoSignIn { get; set; } = MgdsAutoSignIn.Sbox;

	/// <summary>Fetches remote config once signing in is done. Needs Remote config activated on the project.</summary>
	[Property, Group( "When the game starts" )] public bool FetchConfigOnStart { get; set; }

	/// <summary>Connects to push messages once signing in is done. Needs Push messages activated on the project.</summary>
	[Property, Group( "When the game starts" )] public bool ConnectPushOnStart { get; set; }

	/// <summary>Channels to listen on when push connects by itself: a region, a guild, a match.</summary>
	[Property, Group( "When the game starts" )] public List<string> PushChannels { get; set; } = new();

	/// <summary>The game's version, sent with reports.</summary>
	[Property, Group( "Reports" )] public string Build { get; set; } = "";

	/// <summary>Skips signing in on a dedicated server, which has no player of its own.</summary>
	[Property, Group( "Advanced" )] public bool SkipOnDedicatedServer { get; set; } = true;

	/// <summary>Where MGDS is. Leave it unless you were told otherwise.</summary>
	[Property, Group( "Advanced" )] public string ServerUrl { get; set; } = DefaultServer;

	/// <summary>Writes every request to the console.</summary>
	[Property, Group( "Advanced" )] public bool LogRequests { get; set; }

	private readonly TaskCompletionSource<bool> _ready = new();
	private readonly CancellationTokenSource _stopping = new();

	/// <summary>The manager, once its scene has started. Null when there is none.</summary>
	public static MgdsManager Instance { get; private set; }

	/// <summary>Whether there is a manager to use.</summary>
	public static bool Exists => Instance.IsValid();

	/// <summary>Signing in, out, linking accounts, and the player's own data.</summary>
	public MgdsAuth Auth { get; private set; }

	/// <summary>The signed-in player's save files.</summary>
	public MgdsSaves Saves { get; private set; }

	public MgdsLeaderboards Leaderboards { get; private set; }

	/// <summary>Remote config: values the game fetches, changed on the dashboard.</summary>
	public MgdsRemoteConfig Config { get; private set; }

	/// <summary>Bug reports and ideas from inside the game, and the answers to them.</summary>
	public MgdsReports Reports { get; private set; }

	/// <summary>Push messages: methods the dashboard makes every running game call.</summary>
	public MgdsPush Push { get; private set; }

	/// <summary>True once the start-up work - signing in, config, push - is done, whether or not it worked.</summary>
	public bool IsReady => _ready.Task.IsCompleted;

	/// <summary>Completes when the start-up work is done. <c>await Mgds.WhenReady;</c> before the first call that needs a player.</summary>
	public Task WhenReady => _ready.Task;

	/// <summary>Raised once the start-up work is done.</summary>
	public event Action Ready;

	internal string Project => ProjectId?.Trim() ?? "";

	internal string Server => string.IsNullOrWhiteSpace( ServerUrl ) ? DefaultServer : ServerUrl.Trim().TrimEnd( '/' );

	internal string EnvironmentName => Environment?.Trim() ?? "";

	internal MgdsHttp Http { get; private set; }

	internal MgdsStore Store { get; private set; }

	/// <summary>Cancelled when the manager goes away.</summary>
	internal CancellationToken Stopping => _stopping.Token;

	protected override void OnAwake()
	{
		if ( Instance.IsValid() && Instance != this )
		{
			// The first one carries on: it has the session.
			GameObject.Destroy();
			return;
		}

		Instance = this;
		GameObject.Flags |= GameObjectFlags.DontDestroyOnLoad;

		Store = new MgdsStore( Project );
		Http = new MgdsHttp( this );

		Auth = new MgdsAuth( this );
		Saves = new MgdsSaves( this );
		Leaderboards = new MgdsLeaderboards( this );
		Config = new MgdsRemoteConfig( this );
		Reports = new MgdsReports( this );
		Push = new MgdsPush( this );
	}

	protected override void OnStart()
	{
		if ( Instance != this )
			return;

		_ = StartAsync();
	}

	protected override void OnDestroy()
	{
		if ( Instance != this )
			return;

		Push?.Disconnect();
		_stopping.Cancel();
		Instance = null;
	}

	private async Task StartAsync()
	{
		try
		{
			if ( !Guid.TryParse( Project, out _ ) )
			{
				Log.Error( "[MGDS] The MGDS Manager has no project id. Copy it from your project's Configure page on dasmaffin.com/Mgds." );
				return;
			}

			bool signIn = AutoSignIn != MgdsAutoSignIn.None && !(SkipOnDedicatedServer && Application.IsDedicatedServer);

			if ( signIn )
			{
				try
				{
					bool restored = await Auth.RestoreAsync( Stopping );

					if ( !restored && AutoSignIn == MgdsAutoSignIn.Sbox )
						await Auth.SignInWithSboxAsync( Stopping );
					else if ( !restored && AutoSignIn == MgdsAutoSignIn.Anonymous )
						await Auth.SignInAnonymouslyAsync( Stopping );
				}
				catch ( Exception e ) when ( e is not OperationCanceledException )
				{
					Log.Warning( $"[MGDS] Signing in when the game started did not work: {e.Message}" );
				}
			}

			if ( FetchConfigOnStart )
			{
				try
				{
					await Config.FetchAsync( cancel: Stopping );
				}
				catch ( Exception e ) when ( e is not OperationCanceledException )
				{
					Log.Warning( $"[MGDS] Fetching remote config did not work: {e.Message}" );
				}
			}

			if ( ConnectPushOnStart )
				Push.Connect( PushChannels );
		}
		catch ( OperationCanceledException )
		{
		}
		finally
		{
			_ready.TrySetResult( true );
			Ready?.Invoke();
		}
	}

	internal static MgdsManager Require()
	{
		if ( !Instance.IsValid() )
			throw new InvalidOperationException( "There is no MGDS Manager. Add the MGDS Manager component to a GameObject in your first scene." );

		return Instance;
	}
}

/// <summary>
/// A shorter way to the <see cref="MgdsManager"/>: <c>Mgds.Saves</c> is
/// <c>MgdsManager.Instance.Saves</c>. Throws a clear error when there is no manager.
/// </summary>
public static class Mgds
{
	public static MgdsManager Manager => MgdsManager.Require();

	public static MgdsAuth Auth => Manager.Auth;

	public static MgdsSaves Saves => Manager.Saves;

	public static MgdsLeaderboards Leaderboards => Manager.Leaderboards;

	public static MgdsRemoteConfig Config => Manager.Config;

	public static MgdsReports Reports => Manager.Reports;

	public static MgdsPush Push => Manager.Push;

	/// <summary>Completes once the manager's start-up work - signing in, config, push - is done.</summary>
	public static Task WhenReady => Manager.WhenReady;
}
