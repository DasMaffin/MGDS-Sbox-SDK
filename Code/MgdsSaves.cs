using System.Net.Http;

namespace DasMaffin.Mgds;

/// <summary>A save file as it is listed: without its entries.</summary>
public sealed class SaveFileInfo
{
	public string Id { get; set; }
	public string Name { get; set; }
	public bool Public { get; set; }
	public string CreatedBy { get; set; }
	public DateTime UpdatedAt { get; set; }
}

/// <summary>An uploaded file inside a save file.</summary>
public sealed class SaveFileUpload
{
	public string Url { get; set; }
	public string Name { get; set; }
	public long Bytes { get; set; }
	public string Mime { get; set; }
}

public sealed class SaveFileOwner
{
	public string PlayerId { get; set; }
	public DateTime AddedAt { get; set; }
}

/// <summary>
/// A whole save file. Entries are under their key, so <c>save.Get&lt;int&gt;( "level" )</c> or
/// <c>save.Entries["inventory"][3]["count"]</c> read them directly.
/// </summary>
public sealed class SaveFile
{
	public string Id { get; set; }
	public string Name { get; set; }
	public bool Public { get; set; }
	public string CreatedBy { get; set; }
	public List<string> Owners { get; set; } = new();
	public DateTime CreatedAt { get; set; }
	public DateTime UpdatedAt { get; set; }

	/// <summary>Every entry's value by its key.</summary>
	public JsonObject Entries { get; set; } = new();

	/// <summary>What each entry is: int, string, json or file.</summary>
	public Dictionary<string, string> Types { get; set; } = new();

	public bool Has( string key ) => Entries.ContainsKey( key );

	/// <summary>int, string, json or file; null when there is no such entry.</summary>
	public string TypeOf( string key ) => Types.TryGetValue( key, out string type ) ? type : null;

	/// <summary>An entry as <typeparamref name="T"/>, or <paramref name="fallback"/> when it is not there.</summary>
	public T Get<T>( string key, T fallback = default ) =>
		Entries.TryGetPropertyValue( key, out JsonNode value ) && value is not null ? MgdsJson.To<T>( value ) : fallback;

	/// <summary>A file entry's details - its <c>Url</c> downloads it - or null.</summary>
	public SaveFileUpload GetUpload( string key ) => TypeOf( key ) == "file" ? Get<SaveFileUpload>( key ) : null;
}

/// <summary>
/// Several changes to one save file, saved together or not at all: its name, whether it is public,
/// entries to set and entries to remove.
/// </summary>
public sealed class SaveChanges
{
	private readonly JsonObject _entries = new();
	private readonly List<string> _remove = new();

	/// <summary>A new name, or null to keep it.</summary>
	public string Name { get; set; }

	/// <summary>Public or private, or null to keep it.</summary>
	public bool? Public { get; set; }

	/// <summary>A whole number.</summary>
	public SaveChanges SetInt( string key, long value ) => Set( key, "int", JsonValue.Create( value ) );

	public SaveChanges SetString( string key, string value ) => Set( key, "string", JsonValue.Create( value ) );

	/// <summary>Any value, kept as JSON: a class of yours, a list, a number with a fraction.</summary>
	public SaveChanges SetJson( string key, object value ) => Set( key, "json", MgdsJson.From( value ) );

	public SaveChanges Remove( string key )
	{
		_remove.Add( key );
		return this;
	}

	internal bool HasEntries => _entries.Count > 0;

	internal JsonObject EntriesJson => (JsonObject)_entries.DeepClone();

	internal JsonObject ToJson()
	{
		var body = new JsonObject();
		if ( Name is not null ) body["name"] = Name;
		if ( Public is not null ) body["public"] = Public.Value;
		if ( _entries.Count > 0 ) body["entries"] = EntriesJson;
		if ( _remove.Count > 0 ) body["remove"] = new JsonArray( _remove.Select( r => (JsonNode)JsonValue.Create( r ) ).ToArray() );
		return body;
	}

	private SaveChanges Set( string key, string type, JsonNode value )
	{
		_entries[key] = Entry( type, value );
		return this;
	}

	internal static JsonObject Entry( string type, JsonNode value ) => new() { ["type"] = type, ["value"] = value };
}

/// <summary>
/// Save files: named sets of entries that belong to the signed-in player, on every computer, and
/// that can be shared with other players or made public.
/// </summary>
/// <remarks>
/// The quickest way is <see cref="SaveAsync{T}"/> and <see cref="LoadAsync{T}"/>: one object of
/// yours per slot. Everything else - several entries, sharing, uploads - is here too.
/// </remarks>
public sealed class MgdsSaves
{
	/// <summary>The entry <see cref="SaveAsync{T}"/> keeps its object under.</summary>
	public const string SlotEntry = "data";

	private readonly MgdsManager _manager;
	private readonly Dictionary<string, string> _slots = new();

	internal MgdsSaves( MgdsManager manager )
	{
		_manager = manager;
		manager.Auth.SignedOut += _slots.Clear;
	}

	// --- the quick way ---------------------------------------------------------------------------

	/// <summary>
	/// Saves <paramref name="data"/> in the player's save file called <paramref name="slot"/>,
	/// making the file the first time. Anything System.Text.Json can write.
	/// </summary>
	public async Task SaveAsync<T>( string slot, T data, CancellationToken cancel = default )
	{
		string id = await SlotIdAsync( slot, cancel );

		if ( id is null )
		{
			SaveFile made = await CreateAsync( slot, new SaveChanges().SetJson( SlotEntry, data ), cancel: cancel );
			_slots[slot] = made.Id;
			return;
		}

		try
		{
			await SetJsonAsync( id, SlotEntry, data, cancel );
		}
		catch ( MgdsException e ) when ( e.IsNotFound )
		{
			// Left on another computer: start it again.
			_slots.Remove( slot );
			await SaveAsync( slot, data, cancel );
		}
	}

	/// <summary>What <see cref="SaveAsync{T}"/> saved in <paramref name="slot"/>, or <paramref name="fallback"/> when there is nothing yet.</summary>
	public async Task<T> LoadAsync<T>( string slot, T fallback = default, CancellationToken cancel = default )
	{
		string id = await SlotIdAsync( slot, cancel );
		if ( id is null )
			return fallback;

		try
		{
			return (await GetAsync( id, cancel )).Get( SlotEntry, fallback );
		}
		catch ( MgdsException e ) when ( e.IsNotFound )
		{
			_slots.Remove( slot );
			return fallback;
		}
	}

	/// <summary>Whether the player has a save file called <paramref name="slot"/>.</summary>
	public async Task<bool> HasSlotAsync( string slot, CancellationToken cancel = default ) => await SlotIdAsync( slot, cancel ) is not null;

	/// <summary>Removes the player from the slot's file - deleting it, when nobody else owns it.</summary>
	public async Task DeleteSlotAsync( string slot, CancellationToken cancel = default )
	{
		string id = await SlotIdAsync( slot, cancel );
		if ( id is not null )
			await LeaveAsync( id, cancel );

		_slots.Remove( slot );
	}

	// --- files -----------------------------------------------------------------------------------

	/// <summary>Every file the signed-in player owns, or only those called <paramref name="name"/>.</summary>
	public async Task<List<SaveFileInfo>> ListAsync( string name = null, CancellationToken cancel = default )
	{
		string query = name is null ? "" : "?name=" + MgdsHttp.Escape( name );
		MgdsResponse answer = await _manager.Http.JsonAsync( "GET", "/me/saves" + query, null, Token.Required, cancel );
		return MgdsJson.To<List<SaveFileInfo>>( answer.Json?["files"] ) ?? new();
	}

	/// <summary>Another player's public files (all of them, when it is the signed-in player).</summary>
	public async Task<List<SaveFileInfo>> ListForPlayerAsync( string playerId, CancellationToken cancel = default )
	{
		MgdsResponse answer = await _manager.Http.JsonAsync( "GET", $"/players/{MgdsHttp.Escape( playerId )}/saves", null, Token.IfSignedIn, cancel );
		return MgdsJson.To<List<SaveFileInfo>>( answer.Json?["files"] ) ?? new();
	}

	/// <summary>
	/// Makes a save file, owned by the signed-in player. Names are letters, digits, <c>.</c>,
	/// <c>-</c> and <c>_</c>, and two files may share one. 409 when the player is at the project's limit.
	/// </summary>
	public async Task<SaveFile> CreateAsync( string name, SaveChanges entries = null, bool isPublic = false, CancellationToken cancel = default )
	{
		var body = new JsonObject { ["name"] = name, ["public"] = isPublic };
		if ( entries is not null && entries.HasEntries )
			body["entries"] = entries.EntriesJson;

		return (await _manager.Http.JsonAsync( "POST", "/me/saves", body, Token.Required, cancel )).As<SaveFile>();
	}

	/// <summary>A whole file. A private one that is not the player's answers 404, like one that does not exist.</summary>
	public async Task<SaveFile> GetAsync( string id, CancellationToken cancel = default ) =>
		(await _manager.Http.JsonAsync( "GET", "/saves/" + MgdsHttp.Escape( id ), null, Token.IfSignedIn, cancel )).As<SaveFile>();

	/// <summary>Applies several changes at once - all of them, or none. Returns the file as it now is.</summary>
	public async Task<SaveFile> UpdateAsync( string id, SaveChanges changes, CancellationToken cancel = default ) =>
		(await _manager.Http.JsonAsync( "PUT", "/saves/" + MgdsHttp.Escape( id ), changes.ToJson(), Token.Required, cancel )).As<SaveFile>();

	// --- single entries --------------------------------------------------------------------------

	public Task SetIntAsync( string id, string key, long value, CancellationToken cancel = default ) =>
		SetAsync( id, key, "int", JsonValue.Create( value ), cancel );

	public Task SetStringAsync( string id, string key, string value, CancellationToken cancel = default ) =>
		SetAsync( id, key, "string", JsonValue.Create( value ), cancel );

	/// <summary>Any value, kept as JSON.</summary>
	public Task SetJsonAsync( string id, string key, object value, CancellationToken cancel = default ) =>
		SetAsync( id, key, "json", MgdsJson.From( value ), cancel );

	/// <summary>
	/// Changes part of a JSON entry by a JSON Pointer - <c>/inventory/3/count</c> - without sending
	/// the rest. <c>-</c> as the last part appends to an array.
	/// </summary>
	public Task PatchJsonAsync( string id, string key, string path, object value, CancellationToken cancel = default ) =>
		_manager.Http.JsonAsync( "PATCH", Entry( id, key ), new JsonObject { ["path"] = path, ["value"] = MgdsJson.From( value ) }, Token.Required, cancel );

	/// <summary>Removes part of a JSON entry by a JSON Pointer.</summary>
	public Task RemoveFromJsonAsync( string id, string key, string path, CancellationToken cancel = default ) =>
		_manager.Http.JsonAsync( "PATCH", Entry( id, key ), new JsonObject { ["path"] = path, ["remove"] = true }, Token.Required, cancel );

	public Task RemoveEntryAsync( string id, string key, CancellationToken cancel = default ) =>
		_manager.Http.JsonAsync( "DELETE", Entry( id, key ), null, Token.Required, cancel );

	/// <summary>Uploads a file as an entry, up to 64 MB. Executables are refused (415).</summary>
	public Task UploadAsync( string id, string key, byte[] content, string fileName, string mime = "application/octet-stream",
		CancellationToken cancel = default ) =>
		_manager.Http.FormAsync( "PUT", $"/saves/{MgdsHttp.Escape( id )}/files/{MgdsHttp.Escape( key )}",
			() => MgdsForm.Build( new List<HttpContent> { MgdsForm.File( "file", content, fileName, mime ) } ), Token.Required, cancel );

	/// <summary>Downloads an uploaded file entry.</summary>
	public async Task<byte[]> DownloadAsync( SaveFileUpload upload, CancellationToken cancel = default ) =>
		(await _manager.Http.GetBytesAsync( upload.Url, Token.IfSignedIn, cancel )).Bytes;

	// --- owners ----------------------------------------------------------------------------------

	public async Task<List<SaveFileOwner>> GetOwnersAsync( string id, CancellationToken cancel = default ) =>
		MgdsJson.To<List<SaveFileOwner>>( (await _manager.Http.JsonAsync( "GET", $"/saves/{MgdsHttp.Escape( id )}/owners", null, Token.Required, cancel )).Json?["owners"] )
		?? new();

	/// <summary>Makes another player of this project an owner, with full control.</summary>
	public Task AddOwnerAsync( string id, string playerId, CancellationToken cancel = default ) =>
		_manager.Http.JsonAsync( "PUT", $"/saves/{MgdsHttp.Escape( id )}/owners/{MgdsHttp.Escape( playerId )}", null, Token.Required, cancel );

	public Task RemoveOwnerAsync( string id, string playerId, CancellationToken cancel = default ) =>
		_manager.Http.JsonAsync( "DELETE", $"/saves/{MgdsHttp.Escape( id )}/owners/{MgdsHttp.Escape( playerId )}", null, Token.Required, cancel );

	/// <summary>The signed-in player leaves the file. When the last owner leaves, the file is gone.</summary>
	public Task LeaveAsync( string id, CancellationToken cancel = default )
	{
		foreach ( string slot in _slots.Where( s => s.Value == id ).Select( s => s.Key ).ToList() )
			_slots.Remove( slot );

		return RemoveOwnerAsync( id, _manager.Auth.PlayerId ?? "", cancel );
	}

	// ---------------------------------------------------------------------------------------------

	private Task SetAsync( string id, string key, string type, JsonNode value, CancellationToken cancel ) =>
		_manager.Http.JsonAsync( "PUT", Entry( id, key ), SaveChanges.Entry( type, value ), Token.Required, cancel );

	private static string Entry( string id, string key ) => $"/saves/{MgdsHttp.Escape( id )}/entries/{MgdsHttp.Escape( key )}";

	private async Task<string> SlotIdAsync( string slot, CancellationToken cancel )
	{
		if ( _slots.TryGetValue( slot, out string known ) )
			return known;

		SaveFileInfo found = (await ListAsync( slot, cancel )).OrderByDescending( f => f.UpdatedAt ).FirstOrDefault();
		if ( found is not null )
			_slots[slot] = found.Id;

		return found?.Id;
	}
}

/// <summary>
/// multipart/form-data, by hand: s&amp;box allows MultipartContent but not MultipartFormDataContent,
/// so each part says its own name.
/// </summary>
internal static class MgdsForm
{
	public static HttpContent Build( List<HttpContent> parts )
	{
		var form = new MultipartContent( "form-data", "mgds-" + Guid.NewGuid().ToString( "N" ) );
		foreach ( HttpContent part in parts )
			form.Add( part );

		return form;
	}

	public static HttpContent Text( string name, string value )
	{
		var part = new ByteArrayContent( System.Text.Encoding.UTF8.GetBytes( value ?? "" ) );
		part.Headers.TryAddWithoutValidation( "Content-Disposition", $"form-data; name=\"{name}\"" );
		return part;
	}

	public static HttpContent File( string name, byte[] content, string fileName, string mime )
	{
		var part = new ByteArrayContent( content );
		part.Headers.TryAddWithoutValidation( "Content-Disposition", $"form-data; name=\"{name}\"; filename=\"{fileName.Replace( "\"", "" )}\"" );
		part.Headers.TryAddWithoutValidation( "Content-Type", mime );
		return part;
	}
}
