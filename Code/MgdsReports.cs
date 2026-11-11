using System.Net.Http;
using System.Text;

namespace DasMaffin.Mgds;

public enum ReportKind
{
	Bug,
	Idea,
	Other,
}

/// <summary>A report from the player: what happened, in their words, and whatever the game attaches.</summary>
public sealed class MgdsReport
{
	internal readonly List<(string Field, byte[] Content, string Name, string Mime)> Files = new();

	/// <summary>One line, up to 300 characters. Required.</summary>
	public string Summary { get; set; }

	/// <summary>What happened, up to 8,000 characters. Required.</summary>
	public string Description { get; set; }

	public ReportKind Kind { get; set; } = ReportKind.Bug;

	/// <summary>The game's version. The manager's Build when left out.</summary>
	public string Build { get; set; }

	/// <summary>Where the player was. The scene's name when left out.</summary>
	public string Scene { get; set; }

	/// <summary>A name, for when nobody is signed in. A signed-in player's own name is used instead.</summary>
	public string PlayerName { get; set; }

	/// <summary>Facts of your own, shown as a table on the dashboard: any object.</summary>
	public object Info { get; set; }

	/// <summary>A PNG, JPEG, GIF or WebP, shown on the dashboard.</summary>
	public MgdsReport AddImage( byte[] content, string name = "screenshot.png" ) => Add( "images", content, name, MimeOf( name, "image/png" ) );

	/// <summary>An MP4 or WebM, played on the dashboard.</summary>
	public MgdsReport AddVideo( byte[] content, string name = "recording.mp4" ) => Add( "videos", content, name, MimeOf( name, "video/mp4" ) );

	/// <summary>A log, as plain text.</summary>
	public MgdsReport AddLog( string text, string name = "log.txt" ) => Add( "logs", Encoding.UTF8.GetBytes( text ), name, "text/plain" );

	/// <summary>
	/// A log made of entries, shown as a table you can filter: each with a level (Log, Warning,
	/// Error, Exception, Assert), a message, and optionally a stack.
	/// </summary>
	public MgdsReport AddLogEntries( IEnumerable<(string Level, string Message, string Stack)> entries, string name = "log.json" )
	{
		var list = new JsonArray();
		foreach ( var (level, message, stack) in entries )
		{
			var entry = new JsonObject { ["level"] = level, ["message"] = message };
			if ( !string.IsNullOrEmpty( stack ) )
				entry["stack"] = stack;

			list.Add( entry );
		}

		return Add( "logs", Encoding.UTF8.GetBytes( new JsonObject { ["entries"] = list }.ToJsonString() ), name, "application/json" );
	}

	/// <summary>Any other file: a save, a config. Offered as a download.</summary>
	public MgdsReport AddFile( byte[] content, string name, string mime = "application/octet-stream" ) => Add( "files", content, name, mime );

	private MgdsReport Add( string field, byte[] content, string name, string mime )
	{
		Files.Add( (field, content, name, mime) );
		return this;
	}

	private static string MimeOf( string name, string fallback )
	{
		string lower = name.ToLowerInvariant();
		if ( lower.EndsWith( ".png" ) ) return "image/png";
		if ( lower.EndsWith( ".jpg" ) || lower.EndsWith( ".jpeg" ) ) return "image/jpeg";
		if ( lower.EndsWith( ".gif" ) ) return "image/gif";
		if ( lower.EndsWith( ".webp" ) ) return "image/webp";
		if ( lower.EndsWith( ".mp4" ) ) return "video/mp4";
		if ( lower.EndsWith( ".webm" ) ) return "video/webm";
		return fallback;
	}
}

/// <summary>What MGDS answered to a report.</summary>
public sealed class ReportReceipt
{
	public int Id { get; set; }

	/// <summary>The player it belongs to, or null when nobody was signed in.</summary>
	public string PlayerId { get; set; }
}

public sealed class ReportMessage
{
	/// <summary>True for your answers, false for the player's own messages.</summary>
	public bool FromDeveloper { get; set; }

	public string Body { get; set; }
	public DateTime WrittenAt { get; set; }
}

/// <summary>One of the signed-in player's reports.</summary>
public sealed class PlayerReport
{
	public int Id { get; set; }
	public string Kind { get; set; }
	public string Summary { get; set; }
	public string Description { get; set; }
	public DateTime ReceivedAt { get; set; }
	public DateTime? ClosedAt { get; set; }
	public bool Open { get; set; }

	/// <summary>Your answers they have not read yet.</summary>
	public int Unread { get; set; }

	/// <summary>The conversation, when the report was read on its own.</summary>
	[System.Text.Json.Serialization.JsonIgnore]
	public List<ReportMessage> Conversation { get; set; } = new();
}

public sealed class PlayerReports
{
	/// <summary>Unread answers across all of them: what a badge shows.</summary>
	public int Unread { get; set; }

	public List<PlayerReport> Reports { get; set; } = new();
}

/// <summary>
/// Reports: a player tells you something went wrong, or has an idea, from inside the game. Nobody
/// needs to be signed in; when somebody is, the report is theirs and they can read your answers here.
/// </summary>
public sealed class MgdsReports
{
	private readonly MgdsManager _manager;

	internal MgdsReports( MgdsManager manager ) => _manager = manager;

	/// <summary>Sends a report. 429 after 12 in an hour.</summary>
	public async Task<ReportReceipt> SendAsync( MgdsReport report, CancellationToken cancel = default )
	{
		if ( string.IsNullOrWhiteSpace( report.Summary ) || string.IsNullOrWhiteSpace( report.Description ) )
			throw new ArgumentException( "A report needs a summary and a description.", nameof( report ) );

		var info = new JsonObject { ["engine"] = "s&box", ["dedicatedServer"] = Application.IsDedicatedServer };
		if ( report.Info is not null && MgdsJson.From( report.Info ) is JsonObject own )
		{
			foreach ( var (key, value) in own.ToList() )
			{
				own.Remove( key );
				info[key] = value;
			}
		}

		string build = report.Build ?? _manager.Build;
		string scene = report.Scene ?? _manager.Scene?.Name;
		string infoJson = info.ToJsonString();

		HttpContent Form()
		{
			var parts = new List<HttpContent>
			{
				MgdsForm.Text( "summary", report.Summary ),
				MgdsForm.Text( "description", report.Description ),
				MgdsForm.Text( "kind", report.Kind.ToString() ),
			};

			if ( !string.IsNullOrEmpty( build ) ) parts.Add( MgdsForm.Text( "build", build ) );
			if ( !string.IsNullOrEmpty( scene ) ) parts.Add( MgdsForm.Text( "scene", scene ) );
			if ( !string.IsNullOrEmpty( report.PlayerName ) ) parts.Add( MgdsForm.Text( "playerName", report.PlayerName ) );

			parts.Add( MgdsForm.File( "info", Encoding.UTF8.GetBytes( infoJson ), "info.json", "application/json" ) );

			foreach ( var (field, content, name, mime) in report.Files )
				parts.Add( MgdsForm.File( field, content, name, mime ) );

			return MgdsForm.Build( parts );
		}

		return (await _manager.Http.FormAsync( "POST", "/reports", Form, Token.IfSignedIn, cancel )).As<ReportReceipt>();
	}

	// --- answers, in the game --------------------------------------------------------------------

	/// <summary>The signed-in player's reports, with how many of your answers they have not read.</summary>
	public async Task<PlayerReports> GetMineAsync( CancellationToken cancel = default ) =>
		(await _manager.Http.JsonAsync( "GET", "/me/reports", null, Token.Required, cancel )).As<PlayerReports>();

	/// <summary>One of their reports with its conversation. Opening it marks your answers read.</summary>
	public async Task<PlayerReport> GetMineAsync( int id, CancellationToken cancel = default )
	{
		JsonNode answer = (await _manager.Http.JsonAsync( "GET", "/me/reports/" + id, null, Token.Required, cancel )).Json;
		PlayerReport report = MgdsJson.To<PlayerReport>( answer );
		report.Conversation = MgdsJson.To<List<ReportMessage>>( answer?["messages"] ) ?? new();
		return report;
	}

	/// <summary>Writes in the conversation on one of their reports. 409 once it is closed.</summary>
	public Task ReplyAsync( int id, string body, CancellationToken cancel = default ) =>
		_manager.Http.JsonAsync( "POST", $"/me/reports/{id}/messages", new JsonObject { ["body"] = body }, Token.Required, cancel );

	/// <summary>The player says it is sorted.</summary>
	public Task CloseAsync( int id, CancellationToken cancel = default ) =>
		_manager.Http.JsonAsync( "POST", $"/me/reports/{id}/close", null, Token.Required, cancel );
}
