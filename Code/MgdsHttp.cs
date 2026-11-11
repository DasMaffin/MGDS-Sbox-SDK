using System.Net.Http;
using System.Text;

namespace DasMaffin.Mgds;

/// <summary>
/// MGDS said no, or could not be reached. <see cref="StatusCode"/> is the HTTP status (0 when there
/// was no answer at all) and <see cref="Error"/> MGDS's own words for it.
/// </summary>
public sealed class MgdsException : Exception
{
	public MgdsException( int statusCode, string error )
		: base( statusCode == 0 ? $"MGDS could not be reached: {error}" : $"MGDS answered {statusCode}: {error}" )
	{
		StatusCode = statusCode;
		Error = error;
	}

	/// <summary>The HTTP status, or 0 when MGDS could not be reached.</summary>
	public int StatusCode { get; }

	/// <summary>What MGDS said was wrong.</summary>
	public string Error { get; }

	public bool IsNetworkError => StatusCode == 0;

	/// <summary>Not signed in, or the session ended.</summary>
	public bool IsUnauthorized => StatusCode == 401;

	public bool IsNotFound => StatusCode == 404;
}

internal enum Token
{
	/// <summary>Never sends the player's token.</summary>
	None,

	/// <summary>Sends it when somebody is signed in.</summary>
	IfSignedIn,

	/// <summary>Refuses to make the call without a signed-in player.</summary>
	Required,
}

internal sealed class MgdsResponse
{
	private JsonNode _json;
	private bool _parsed;

	public int Status;
	public string Text;
	public byte[] Bytes;
	public string RetryAfter;

	public JsonNode Json
	{
		get
		{
			if ( !_parsed )
			{
				_json = MgdsJson.Parse( Text );
				_parsed = true;
			}

			return _json;
		}
	}

	public T As<T>() => MgdsJson.To<T>( Json );
}

internal static class MgdsJson
{
	/// <summary>camelCase out, any case in: MGDS's names, and the game's own classes as they are.</summary>
	public static readonly JsonSerializerOptions Options = new( JsonSerializerDefaults.Web );

	public static JsonNode Parse( string text ) => string.IsNullOrWhiteSpace( text ) ? null : JsonNode.Parse( text );

	public static T To<T>( JsonNode node ) => node is null ? default : node.Deserialize<T>( Options );

	/// <summary>Any value as JSON. A node is copied, so it can go inside another.</summary>
	public static JsonNode From( object value ) => value switch
	{
		null => null,
		JsonNode node => node.DeepClone(),
		_ => JsonSerializer.SerializeToNode( value, value.GetType(), Options ),
	};
}

/// <summary>Every request to MGDS: the address, the token, one retry after refreshing, and errors as exceptions.</summary>
internal sealed class MgdsHttp
{
	private readonly MgdsManager _manager;

	public MgdsHttp( MgdsManager manager ) => _manager = manager;

	/// <summary>https://www.dasmaffin.com/api/mgds/v1/projects/{projectId}</summary>
	public string ProjectRoot => $"{_manager.Server}/api/mgds/v1/projects/{_manager.Project}";

	public string Url( string path ) => path.StartsWith( "http", StringComparison.OrdinalIgnoreCase ) ? path : ProjectRoot + path;

	public Task<MgdsResponse> JsonAsync( string method, string path, JsonNode body, Token token, CancellationToken cancel, params int[] alsoFine ) =>
		SendAsync( method, path, token, cancel, alsoFine, false,
			() => body is null ? null : new StringContent( body.ToJsonString(), Encoding.UTF8, "application/json" ) );

	public Task<MgdsResponse> FormAsync( string method, string path, Func<HttpContent> form, Token token, CancellationToken cancel ) =>
		SendAsync( method, path, token, cancel, Array.Empty<int>(), false, form );

	public Task<MgdsResponse> GetBytesAsync( string url, Token token, CancellationToken cancel ) =>
		SendAsync( "GET", url, token, cancel, Array.Empty<int>(), true, () => null );

	private async Task<MgdsResponse> SendAsync( string method, string path, Token token, CancellationToken cancel, int[] alsoFine, bool bytes,
		Func<HttpContent> content )
	{
		if ( !Guid.TryParse( _manager.Project, out _ ) )
			throw new InvalidOperationException( "The MGDS Manager has no project id." );

		MgdsAuth auth = _manager.Auth;

		for ( int attempt = 0; ; attempt++ )
		{
			string bearer = null;

			if ( token != Token.None && auth.IsSignedIn )
			{
				await auth.EnsureFreshAsync( cancel );
				bearer = auth.SessionToken;
			}

			if ( token == Token.Required && bearer is null )
				throw new MgdsException( 401, "not signed in: sign a player in first" );

			var headers = new Dictionary<string, string> { ["Accept"] = "application/json" };
			if ( bearer is not null )
				headers["Authorization"] = "Bearer " + bearer;

			string url = Url( path );
			if ( _manager.LogRequests )
				Log.Info( $"[MGDS] {method} {url}" );

			HttpResponseMessage answer;
			try
			{
				answer = await Http.RequestAsync( url, method, content(), headers, cancel );
			}
			catch ( HttpRequestException e )
			{
				throw new MgdsException( 0, e.Message );
			}

			using ( answer )
			{
				int status = (int)answer.StatusCode;

				// An access token that stopped working: refresh once and ask again.
				if ( status == 401 && bearer is not null && attempt == 0 && await auth.RecoverAsync( bearer, cancel ) )
					continue;

				var response = new MgdsResponse { Status = status };

				if ( bytes && status >= 200 && status < 300 )
					response.Bytes = await answer.Content.ReadAsByteArrayAsync( cancel );
				else
					response.Text = await answer.Content.ReadAsStringAsync( cancel );

				if ( answer.Headers.TryGetValues( "Retry-After", out IEnumerable<string> retry ) )
					response.RetryAfter = retry.FirstOrDefault();

				if ( _manager.LogRequests )
					Log.Info( $"[MGDS] {status} {method} {url}" );

				if ( (status >= 200 && status < 300) || alsoFine.Contains( status ) )
					return response;

				throw Failure( response );
			}
		}
	}

	public static MgdsException Failure( MgdsResponse response )
	{
		string error = null;

		try
		{
			error = response.Json?["error"]?.GetValue<string>();
		}
		catch ( Exception )
		{
		}

		return new MgdsException( response.Status, error ?? response.Text ?? "no reason given" );
	}

	public static string Escape( string segment ) => Uri.EscapeDataString( segment ?? "" );
}

/// <summary>What the SDK keeps between runs, per project, in the game's own data folder.</summary>
internal sealed class MgdsStore
{
	private const string Folder = "mgds";

	private readonly string _path;
	private readonly JsonObject _values;

	public MgdsStore( string project )
	{
		_path = $"{Folder}/{(Guid.TryParse( project, out Guid id ) ? id.ToString() : "unset")}.json";

		try
		{
			if ( FileSystem.Data.FileExists( _path ) )
				_values = JsonNode.Parse( FileSystem.Data.ReadAllText( _path ) ) as JsonObject;
		}
		catch ( Exception e )
		{
			Log.Warning( $"[MGDS] Could not read {_path}: {e.Message}" );
		}

		_values ??= new JsonObject();
	}

	public string Get( string key ) => _values[key]?.GetValue<string>();

	public void Set( string key, string value )
	{
		if ( string.IsNullOrEmpty( value ) )
			_values.Remove( key );
		else
			_values[key] = value;

		try
		{
			FileSystem.Data.CreateDirectory( Folder );
			FileSystem.Data.WriteAllText( _path, _values.ToJsonString() );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[MGDS] Could not write {_path}: {e.Message}" );
		}
	}

	/// <summary>An id for this install, for remote config rollouts before anybody signs in.</summary>
	public string InstallId
	{
		get
		{
			string id = Get( "install" );
			if ( id is null )
			{
				id = Guid.NewGuid().ToString();
				Set( "install", id );
			}

			return id;
		}
	}
}
