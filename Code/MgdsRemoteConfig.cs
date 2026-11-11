using System.IO;
using System.IO.Compression;

namespace DasMaffin.Mgds;

/// <summary>A picture or video setting. Fetch it with <see cref="MgdsRemoteConfig.DownloadAsync(string, CancellationToken)"/>.</summary>
public sealed class ConfigMedia
{
	public string Url { get; set; }

	/// <summary>What the file is once unpacked: image/png, video/mp4...</summary>
	public string ContentType { get; set; }

	/// <summary>none, or gzip (the SDK unpacks it).</summary>
	public string Compression { get; set; }

	public long Bytes { get; set; }
	public string FileName { get; set; }
	public int? Width { get; set; }
	public int? Height { get; set; }
	public double? Seconds { get; set; }
}

public sealed class ConfigRule
{
	public string Id { get; set; }
	public string Name { get; set; }
}

/// <summary>
/// Remote config: values the game fetches, changed on the dashboard without a new build. Fetch once
/// (the manager can do it when the game starts), then read with <see cref="Get{T}"/>.
/// </summary>
public sealed class MgdsRemoteConfig
{
	private readonly MgdsManager _manager;
	private readonly Dictionary<string, byte[]> _media = new();
	private JsonObject _values = new();
	private Dictionary<string, string> _types = new();

	internal MgdsRemoteConfig( MgdsManager manager ) => _manager = manager;

	/// <summary>
	/// What the game tells MGDS about itself, for rules to match on: <c>platform</c>,
	/// <c>app.version</c>, <c>user.level</c>... <c>platform</c> is "sbox" to begin with; add your
	/// own before fetching.
	/// </summary>
	public JsonObject Attributes { get; } = new() { ["platform"] = "sbox" };

	/// <summary>Raised after every successful fetch.</summary>
	public event Action Fetched;

	public bool IsFetched { get; private set; }

	/// <summary>The environment the values came from.</summary>
	public string Environment { get; private set; }

	/// <summary>The rules that changed something for this player.</summary>
	public List<ConfigRule> AppliedRules { get; private set; } = new();

	/// <summary>Every value, by key.</summary>
	public JsonObject Values => _values;

	/// <summary>
	/// Fetches the values for this player. <paramref name="attributes"/>, when given, are added to
	/// <see cref="Attributes"/>; <paramref name="environment"/> overrides the manager's.
	/// </summary>
	public async Task FetchAsync( object attributes = null, string environment = null, CancellationToken cancel = default )
	{
		if ( attributes is not null && MgdsJson.From( attributes ) is JsonObject extra )
		{
			foreach ( var (key, value) in extra.ToList() )
			{
				extra.Remove( key );
				Attributes[key] = value;
			}
		}

		var body = new JsonObject
		{
			["userId"] = _manager.Auth.PlayerId ?? _manager.Store.InstallId,
			["attributes"] = Attributes.DeepClone(),
		};

		string chosen = environment ?? _manager.EnvironmentName;
		if ( !string.IsNullOrEmpty( chosen ) )
			body["environment"] = chosen;

		JsonNode answer = (await _manager.Http.JsonAsync( "POST", "/config", body, Token.IfSignedIn, cancel )).Json;

		_values = answer?["configs"] as JsonObject ?? new JsonObject();
		_types = MgdsJson.To<Dictionary<string, string>>( answer?["types"] ) ?? new();
		AppliedRules = MgdsJson.To<List<ConfigRule>>( answer?["rules"] ) ?? new();
		Environment = answer?["environment"]?.GetValue<string>();
		IsFetched = true;

		Fetched?.Invoke();
	}

	public bool Has( string key ) => _values.ContainsKey( key );

	/// <summary>string, bool, int, long, float, json, image or video; null when there is no such key.</summary>
	public string TypeOf( string key ) => _types.TryGetValue( key, out string type ) ? type : null;

	/// <summary>A value as <typeparamref name="T"/>, or <paramref name="fallback"/> when it is not set (or not fetched yet).</summary>
	public T Get<T>( string key, T fallback = default )
	{
		if ( !_values.TryGetPropertyValue( key, out JsonNode value ) || value is null )
			return fallback;

		try
		{
			return MgdsJson.To<T>( value );
		}
		catch ( Exception )
		{
			return fallback;
		}
	}

	public string GetString( string key, string fallback = "" ) => Get( key, fallback );

	public bool GetBool( string key, bool fallback = false ) => Get( key, fallback );

	public int GetInt( string key, int fallback = 0 ) => Get( key, fallback );

	public long GetLong( string key, long fallback = 0 ) => Get( key, fallback );

	public float GetFloat( string key, float fallback = 0 ) => Get( key, fallback );

	/// <summary>A json setting as it is.</summary>
	public JsonNode GetJson( string key ) => _values.TryGetPropertyValue( key, out JsonNode value ) ? value : null;

	/// <summary>An image or video setting, or null.</summary>
	public ConfigMedia GetMedia( string key ) =>
		TypeOf( key ) is "image" or "video" ? Get<ConfigMedia>( key ) : null;

	/// <summary>An image or video setting's file, unpacked. Kept in memory: an address never changes what it holds.</summary>
	public Task<byte[]> DownloadAsync( string key, CancellationToken cancel = default ) =>
		DownloadAsync( GetMedia( key ) ?? throw new KeyNotFoundException( $"Remote config has no image or video called {key}." ), cancel );

	public async Task<byte[]> DownloadAsync( ConfigMedia media, CancellationToken cancel = default )
	{
		if ( _media.TryGetValue( media.Url, out byte[] kept ) )
			return kept;

		byte[] bytes = (await _manager.Http.GetBytesAsync( media.Url, Token.None, cancel )).Bytes;

		if ( media.Compression == "gzip" )
		{
			using var packed = new GZipStream( new MemoryStream( bytes ), CompressionMode.Decompress );
			using var unpacked = new MemoryStream();
			packed.CopyTo( unpacked );
			bytes = unpacked.ToArray();
		}

		_media[media.Url] = bytes;
		return bytes;
	}
}
