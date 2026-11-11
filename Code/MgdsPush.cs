using System.Net.Http;
using System.Text;

namespace DasMaffin.Mgds;

/// <summary>A push message: the trigger to run, and its arguments.</summary>
public sealed class PushMessage
{
	public long Id { get; set; }

	/// <summary>The trigger's name: the method the game runs.</summary>
	public string Trigger { get; set; }

	public JsonObject Arguments { get; set; } = new();
	public DateTime SentAt { get; set; }
	public DateTime ExpiresAt { get; set; }

	/// <summary>sticky or live.</summary>
	public string Delivery { get; set; }

	/// <summary>An argument as <typeparamref name="T"/>, or <paramref name="fallback"/> when it was not sent.</summary>
	public T Get<T>( string name, T fallback = default ) =>
		Arguments is not null && Arguments.TryGetPropertyValue( name, out JsonNode value ) && value is not null ? MgdsJson.To<T>( value ) : fallback;
}

/// <summary>A message in the player's history.</summary>
public sealed class PushHistoryItem
{
	public long Id { get; set; }
	public string Trigger { get; set; }
	public JsonObject Arguments { get; set; }
	public DateTime SentAt { get; set; }
	public DateTime ExpiresAt { get; set; }
	public string Delivery { get; set; }
	public DateTime? ReceivedAt { get; set; }
	public DateTime? AcknowledgedAt { get; set; }
	public DateTime? RecalledAt { get; set; }
}

/// <summary>
/// Push messages: the dashboard makes every running game run a method - start maintenance, show a
/// banner, hand out a gift. Register a handler per trigger with <see cref="On"/> and
/// <see cref="Connect"/>; the SDK keeps asking, catches up on what it missed, never hands the same
/// message twice, and tells the dashboard each one was run.
/// </summary>
/// <remarks>
/// s&amp;box only hands a game an HTTP answer once it is complete, so the SDK holds a request open
/// until a message arrives (or 25 seconds pass) and then asks again: a long poll. Messages arrive as
/// they are sent all the same. Acknowledging needs a signed-in player.
/// </remarks>
public sealed class MgdsPush
{
	private const string LastIdKey = "push.last";
	private const int WaitSeconds = 25;

	private readonly MgdsManager _manager;
	private readonly Dictionary<string, List<Action<PushMessage>>> _handlers = new();
	private readonly HashSet<long> _seen = new();
	private readonly Queue<long> _seenOrder = new();

	private int _generation;
	private CancellationTokenSource _open;
	private long _lastId;

	internal MgdsPush( MgdsManager manager )
	{
		_manager = manager;
		long.TryParse( manager.Store.Get( LastIdKey ), out _lastId );

		// A token sent along decides what the player gets: reconnect when it changes.
		manager.Auth.SignedIn += Refresh;
		manager.Auth.SignedOut += Refresh;
	}

	/// <summary>Raised for every message, before its trigger's handlers.</summary>
	public event Action<PushMessage> MessageReceived;

	/// <summary>A message that went out was recalled: undo it if the game can. The message id and its trigger.</summary>
	public event Action<long, string> Recalled;

	/// <summary>Whether the game wants to be connected.</summary>
	public bool IsListening { get; private set; }

	public IReadOnlyList<string> Channels { get; private set; } = Array.Empty<string>();

	/// <summary>
	/// What conditions on a message are checked against, as for remote config. Starts as
	/// <see cref="MgdsRemoteConfig.Attributes"/>; change it and call <see cref="Refresh"/>.
	/// </summary>
	public JsonObject Attributes { get; set; }

	/// <summary>Tells the dashboard a message was run when its handlers finished without an exception. On by default.</summary>
	public bool AutoAcknowledge { get; set; } = true;

	/// <summary>Runs <paramref name="handler"/> for every message of <paramref name="trigger"/>.</summary>
	public void On( string trigger, Action<PushMessage> handler )
	{
		if ( !_handlers.TryGetValue( trigger, out List<Action<PushMessage>> list ) )
			_handlers[trigger] = list = new();

		list.Add( handler );
	}

	public void Off( string trigger, Action<PushMessage> handler )
	{
		if ( _handlers.TryGetValue( trigger, out List<Action<PushMessage>> list ) )
			list.Remove( handler );
	}

	/// <summary>
	/// Starts listening, on <paramref name="channels"/> too when given (a region, a guild, a match: up
	/// to 20). Keeps going until <see cref="Disconnect"/>. Calling it again changes the channels.
	/// </summary>
	public void Connect( IEnumerable<string> channels = null )
	{
		Channels = (channels ?? Channels).Where( c => !string.IsNullOrWhiteSpace( c ) ).Distinct().ToList();
		IsListening = true;
		Restart();
	}

	/// <summary>Stops listening.</summary>
	public void Disconnect()
	{
		IsListening = false;
		_generation++;
		_open?.Cancel();
		_open = null;
	}

	/// <summary>Reconnects now, with the current channels, attributes and player.</summary>
	public void Refresh()
	{
		if ( IsListening )
			Restart();
	}

	/// <summary>Tells the dashboard the game ran a message. Done by itself unless <see cref="AutoAcknowledge"/> is off. Needs a signed-in player.</summary>
	public Task AcknowledgeAsync( PushMessage message, CancellationToken cancel = default ) =>
		_manager.Http.JsonAsync( "POST", $"/push/messages/{message.Id}/ack", null, Token.Required, cancel );

	/// <summary>Every message the signed-in player's games were handed, newest first: for an inbox.</summary>
	public async Task<List<PushHistoryItem>> GetHistoryAsync( int limit = 50, long? before = null, CancellationToken cancel = default )
	{
		string path = $"/me/push?limit={limit}" + (before is null ? "" : "&before=" + before);
		return MgdsJson.To<List<PushHistoryItem>>( (await _manager.Http.JsonAsync( "GET", path, null, Token.Required, cancel )).Json?["results"] ) ?? new();
	}

	// --- the connection --------------------------------------------------------------------------

	private void Restart()
	{
		int generation = ++_generation;
		_open?.Cancel();
		_open = CancellationTokenSource.CreateLinkedTokenSource( _manager.Stopping );
		_ = RunAsync( generation, _open.Token );
	}

	private async Task RunAsync( int generation, CancellationToken cancel )
	{
		int delay = 0;

		while ( generation == _generation && !cancel.IsCancellationRequested )
		{
			try
			{
				if ( delay > 0 )
					await Task.Delay( delay, cancel );

				delay = 3000;

				string token = null;
				if ( _manager.Auth.IsSignedIn )
				{
					await _manager.Auth.EnsureFreshAsync( cancel );
					token = _manager.Auth.SessionToken;
				}

				var headers = new Dictionary<string, string> { ["Accept"] = "text/event-stream" };
				if ( token is not null )
					headers["Authorization"] = "Bearer " + token;

				using HttpResponseMessage answer = await Http.RequestAsync( Address(), "GET", null, headers, cancel );
				string text = await answer.Content.ReadAsStringAsync( cancel );
				int status = (int)answer.StatusCode;

				if ( generation != _generation )
					return;

				if ( status == 401 && token is not null )
				{
					// The token stopped working: refresh, or carry on as nobody.
					await _manager.Auth.RecoverAsync( token, cancel );
					delay = 0;
				}
				else if ( status == 503 )
				{
					delay = answer.Headers.TryGetValues( "Retry-After", out IEnumerable<string> retry ) && int.TryParse( retry.FirstOrDefault(), out int seconds )
						? seconds * 1000
						: 30000;
				}
				else if ( status >= 400 )
				{
					Log.Error( $"[MGDS] Push messages refused the connection ({status}): {text}. Not reconnecting." );
					IsListening = false;
					return;
				}
				else
				{
					foreach ( SseEvent e in new SseReader().Feed( text ) )
						Handle( e );

					delay = 0;
				}
			}
			catch ( OperationCanceledException )
			{
				return;
			}
			catch ( Exception e )
			{
				if ( _manager.LogRequests )
					Log.Warning( $"[MGDS] Push connection dropped: {e.Message}" );
			}
		}
	}

	private string Address()
	{
		var query = new List<string> { "wait=" + WaitSeconds };

		if ( !string.IsNullOrEmpty( _manager.EnvironmentName ) )
			query.Add( "environment=" + MgdsHttp.Escape( _manager.EnvironmentName ) );

		if ( Channels.Count > 0 )
			query.Add( "channels=" + MgdsHttp.Escape( string.Join( ",", Channels ) ) );

		JsonObject attributes = Attributes ?? _manager.Config.Attributes;
		if ( attributes is not null && attributes.Count > 0 )
			query.Add( "attributes=" + MgdsHttp.Escape( attributes.ToJsonString() ) );

		if ( _lastId > 0 )
			query.Add( "lastEventId=" + _lastId );

		return _manager.Http.Url( "/push/stream" ) + "?" + string.Join( "&", query );
	}

	private void Handle( SseEvent e )
	{
		JsonNode data;
		try
		{
			data = MgdsJson.Parse( e.Data );
		}
		catch ( Exception )
		{
			return;
		}

		if ( e.Name == "mgds.recalled" )
		{
			Recalled?.Invoke( data?["id"]?.GetValue<long>() ?? 0, data?["trigger"]?.GetValue<string>() );
			return;
		}

		if ( e.Name.StartsWith( "mgds.", StringComparison.Ordinal ) )
			return;

		PushMessage message = MgdsJson.To<PushMessage>( data );
		if ( message is null )
			return;

		// At least once: a message's id never repeats, so one seen is skipped.
		if ( !_seen.Add( message.Id ) )
			return;

		_seenOrder.Enqueue( message.Id );
		while ( _seenOrder.Count > 500 )
			_seen.Remove( _seenOrder.Dequeue() );

		if ( message.Id > _lastId )
		{
			_lastId = message.Id;
			_manager.Store.Set( LastIdKey, _lastId.ToString() );
		}

		bool ran = true;

		try
		{
			MessageReceived?.Invoke( message );
		}
		catch ( Exception ex )
		{
			ran = false;
			Log.Error( ex );
		}

		if ( _handlers.TryGetValue( message.Trigger ?? "", out List<Action<PushMessage>> handlers ) )
		{
			foreach ( Action<PushMessage> handler in handlers.ToList() )
			{
				try
				{
					handler( message );
				}
				catch ( Exception ex )
				{
					ran = false;
					Log.Error( ex );
				}
			}
		}
		else if ( MessageReceived is null )
		{
			Log.Warning( $"[MGDS] Push message {message.Trigger} arrived and nothing handles it: Mgds.Push.On( \"{message.Trigger}\", ... )." );
			ran = false;
		}

		if ( ran && AutoAcknowledge && _manager.Auth.IsSignedIn )
			_ = AcknowledgeQuietlyAsync( message );
	}

	private async Task AcknowledgeQuietlyAsync( PushMessage message )
	{
		try
		{
			await AcknowledgeAsync( message, _manager.Stopping );
		}
		catch ( Exception )
		{
			// Counting it is the dashboard's business; the game has done its part.
		}
	}
}

internal struct SseEvent
{
	public string Name;
	public string Data;
	public long? Id;
}

/// <summary>Server-Sent Events, read line by line.</summary>
internal sealed class SseReader
{
	private readonly StringBuilder _line = new();
	private readonly StringBuilder _data = new();
	private string _name;
	private long? _id;

	public List<SseEvent> Feed( string text )
	{
		var events = new List<SseEvent>();

		foreach ( char c in text )
		{
			if ( c != '\n' )
			{
				_line.Append( c );
				continue;
			}

			string line = _line.ToString().TrimEnd( '\r' );
			_line.Clear();

			if ( line.Length == 0 )
			{
				if ( _data.Length > 0 )
					events.Add( new SseEvent { Name = _name ?? "message", Data = _data.ToString(), Id = _id } );

				_data.Clear();
				_name = null;
				_id = null;
			}
			else if ( !line.StartsWith( ':' ) )
			{
				int colon = line.IndexOf( ':' );
				string field = colon < 0 ? line : line[..colon];
				string value = colon < 0 ? "" : line[(colon + 1)..].TrimStart( ' ' );

				switch ( field )
				{
					case "event": _name = value; break;
					case "data": if ( _data.Length > 0 ) _data.Append( '\n' ); _data.Append( value ); break;
					case "id": _id = long.TryParse( value, out long id ) ? id : null; break;
				}
			}
		}

		return events;
	}
}
