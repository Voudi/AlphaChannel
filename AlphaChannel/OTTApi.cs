using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlphaChannel;

public class OTTApi : IDisposable
{
	private static readonly string _url = "https://opentogethertube.com";
	private static readonly string _wsURL = "wss://opentogethertube.com";
	private readonly HttpClient _httpClient = new();
	private ClientWebSocket? _wsocket;
	private string _token = string.Empty;
	private string _room = string.Empty;
	private readonly ControlWindow _controlWindow;
	private readonly List<Requests.Video> _queue = [];
	private bool _connectionReady;
	public List<Requests.Video> GetQueue => _queue;
	public string GetRoomURL => _url + "/room/" + _room;
	public bool IsInRoom => !string.IsNullOrEmpty(_room);
	private Requests.Video? _video;
	public string VideoUrl { get; private set; } = string.Empty;
	private bool _checkingURL;
	private bool _checkFailed;
	public bool LastCheckSuccessful => !_checkingURL && _video is not null && !_checkFailed;
	public bool IsChecking => _checkingURL && !_checkFailed;
	private bool _isNewRoom;
	private readonly JsonSerializerOptions _jsonOptions;
	public OTTApi(ControlWindow controlWindow)
	{
		_controlWindow = controlWindow;
		_jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
	}

	public void Dispose()
	{
		LeaveRoom().ContinueWith(t => _wsocket?.Dispose());
		_httpClient.Dispose();
		GC.SuppressFinalize(this);
	}

	public async void CheckURL(string url)
	{
		if (url.Equals(VideoUrl, StringComparison.Ordinal) || _checkingURL)
		{
			return;
		}

		_checkingURL = true;
		_checkFailed = false;
		_video = null;
		VideoUrl = url;

		try
		{
			Requests.PreviewAdd? previewAdd = await Requests.PreviewAdd.Execute(_httpClient, url);
			if (previewAdd != null && previewAdd.Success)
			{
				_video = previewAdd.Highlighted ?? previewAdd.Result.First();
				Services.Log.Debug("[OTT] URL Check succeeded: " + _video.Title);
				_checkingURL = false;
				return;
			}
		}
		catch (Exception)
		{ }
		Services.Log.Debug("[OTT] URL check failed");
		_checkFailed = true;
		await Task.Delay(1000);
		_checkingURL = false;
	}

	public async Task Initialize(string? roomId = null)
	{
		if (_wsocket != null)
		{
			await LeaveRoom();
		}

		try
		{
			Requests.Auth? auth = await Requests.Auth.Execute(_httpClient);
			if (auth != null)
			{
				_token = auth.Token;
			}
			else
			{
				return;
			}
			if (string.IsNullOrEmpty(roomId))
			{
				Requests.Generate? generateRoom = await Requests.Generate.Execute(_httpClient, _token);
				if (generateRoom != null && generateRoom.Success)
				{
					_room = generateRoom.Room;
					Services.Log.Debug("[OTT] Room Generated: " + _room);
				}
				else
				{
					return;
				}
			}
			else
			{
				_room = roomId;
			}
		}
		catch (Exception)
		{
			return;
		}

		_httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);

		_isNewRoom = string.IsNullOrEmpty(roomId);
		_ = ConnectWSS(_room, _token);
	}

	public async Task LeaveRoom()
	{
		try
		{
			_room = string.Empty;
			if (_wsocket != null)
			{
				await _wsocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
			}
		}
		catch { }
	}

	private async Task ConnectWSS(string room, string token)
	{
		try
		{
			var uri = new Uri(_wsURL + "/api/room/" + room);

			_wsocket = new();

			await _wsocket.ConnectAsync(uri, CancellationToken.None);

			var message = new
			{
				action = "auth",
				token
			};

			string jsonMessage = JsonSerializer.Serialize(message);
			byte[] bytes = Encoding.UTF8.GetBytes(jsonMessage);

			await _wsocket.SendAsync(
				new ArraySegment<byte>(bytes),
				WebSocketMessageType.Text,
				true,
				CancellationToken.None
			);

			_ = ListenWSS();
		}
		catch (Exception e)
		{
			Services.Log.Error("[OTT] WS Connection closed unexpectedly " + e.Message);
		}
	}

	private async Task ListenWSS()
	{
		try
		{
			byte[] buffer = new byte[65536];
			while (_wsocket != null && _wsocket.State == WebSocketState.Open)
			{
				WebSocketReceiveResult result = await _wsocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
				string response = Encoding.UTF8.GetString(buffer, 0, result.Count);
				if (response != null && response.Length > 5)
				{
					OnQueueReceived(response);
				}
			}
		}
		catch (Exception e)
		{
			Services.Log.Debug("[OTT] WS Connection closed: " + e.Message);
		}
		finally
		{
			_wsocket?.Dispose();
		}

		_connectionReady = false;

		Services.Log.Debug("[OTT] Left room");
	}

	private sealed record PushNextVideoRequest(
		[property: JsonPropertyName("type")] int Type,
		[property: JsonPropertyName("video")] Requests.Video Video);
	private bool _pushNextVideo;
	public async void PushNextVideo()
	{
		if (_connectionReady)
		{
			PushVideo();
		}
		else
		{
			_pushNextVideo = true;
		}
	}
	private void PushVideo()
	{
        Services.Log.Debug("Pushing next video");
		SendRequest(new
		{
			action = "req",
			request = new PushNextVideoRequest(14, _video!)
		});
	}

	private sealed record PlayPauseRequest(
		[property: JsonPropertyName("type")] int Type,
		[property: JsonPropertyName("state")] bool State);
	public async void PlayPauseVideo(bool play)
	{
		SendRequest(new
		{
			action = "req",
			request = new PlayPauseRequest(2, play)
		});
	}

	private sealed record SeekRequest(
		[property: JsonPropertyName("type")] int Type,
		[property: JsonPropertyName("value")] double Value);
	public async void Seek(int time)
	{
		SendRequest(new
		{
			action = "req",
			request = new SeekRequest(4, time)
		});
	}

	private async void SendRequest(object message)
	{
		if (_wsocket != null && _wsocket.State == WebSocketState.Open)
		{
			string jsonMessage = JsonSerializer.Serialize(message);

            Services.Log.Debug("Sending JSON: " + jsonMessage);
            
			byte[] bytes = Encoding.UTF8.GetBytes(jsonMessage);

			await _wsocket.SendAsync(
				new ArraySegment<byte>(bytes),
				WebSocketMessageType.Text,
				true,
				CancellationToken.None
			);
		}
	}

	private sealed record PushNextVideoEvent([property: JsonRequired] string Action, List<Requests.Video> Queue, Requests.Video CurrentSource, [property: JsonRequired] double PlaybackPosition, bool? IsPlaying, [property: JsonPropertyName("hls_url")] string HlsUrl);
	private sealed record QueueEvent([property: JsonRequired] string Action, [property: JsonRequired] List<Requests.Video> Queue);
	private sealed record PauseEvent([property: JsonRequired] string Action, [property: JsonRequired] double PlaybackPosition, [property: JsonRequired] bool IsPlaying);
	private sealed record PlayEvent([property: JsonRequired] string Action, [property: JsonRequired] bool IsPlaying);
	private sealed record SeekEvent([property: JsonRequired] string Action, [property: JsonRequired] double PlaybackPosition);

	private void OnQueueReceived(string response)
	{
		try
		{
			using var doc = JsonDocument.Parse(response);
			{
				if (doc.RootElement.TryGetProperty("action", out _))
				{
					if (doc.RootElement.TryGetProperty("currentSource", out _) && doc.RootElement.TryGetProperty("playbackPosition", out _))
					{
						PushNextVideoEvent message = JsonSerializer.Deserialize<PushNextVideoEvent>(response, _jsonOptions)!;
						if (message.Action == "sync")
						{
							_queue.Clear();
							if (message.Queue != null)
							{
								_queue.AddRange(message.Queue);
							}

							string url = "";
							if (message.CurrentSource != null && message.CurrentSource.Service != null)
							{
								switch (message.CurrentSource.Service)
								{
									case "youtube":
										url = $"https://youtu.be/{message.CurrentSource.Id}";
										break;
									case "vimeo":
										url = $"https://vimeo.com/{message.CurrentSource.Id}";
										break;
									case "direct":
										url = message.CurrentSource.Id;
										break;
									case "hls":
										url = message.CurrentSource.HlsUrl ?? message.CurrentSource.Id;
										break;
									default:
										break;
								}
							}

							if (string.IsNullOrEmpty(url))
							{
								return;
							}

							Services.Log.Debug("[OTT] Received new video: " + response);
							_video = message.CurrentSource;
							VideoUrl = url;
							double playbackPos = message.PlaybackPosition;
							if(message.IsPlaying.HasValue)
							{
								_controlWindow.OTTReceiveNewVideo(url, playbackPos, message.IsPlaying.Value);
							}
							else
							{
								_controlWindow.OTTReceiveNewVideo(url, playbackPos, true);
							}
							
							if (_isNewRoom)
							{
								_isNewRoom = false;
								PlayPauseVideo(true);
							}
							
						}
					}
					else if (doc.RootElement.TryGetProperty("queue", out _))
					{
						QueueEvent message = JsonSerializer.Deserialize<QueueEvent>(response, _jsonOptions)!;
						if (message.Action == "sync")
						{
							_queue.Clear();
							if (message.Queue != null)
							{
								_queue.AddRange(message.Queue);
							}

							Services.Log.Debug("[OTT] Received queue");
						}
					}
					else if (doc.RootElement.TryGetProperty("playbackPosition", out _) && doc.RootElement.TryGetProperty("isPlaying", out _))
					{
						PauseEvent message = JsonSerializer.Deserialize<PauseEvent>(response, _jsonOptions)!;
						if (message.Action == "sync" && !message.IsPlaying)
						{
							_controlWindow.OTTReceivePlayPause(false, message.PlaybackPosition);
							Services.Log.Debug("[OTT] Received pause signal at " + message.PlaybackPosition + " seconds");
						}

					}
					else if (doc.RootElement.TryGetProperty("isPlaying", out _))
					{
						PlayEvent message = JsonSerializer.Deserialize<PlayEvent>(response, _jsonOptions)!;
						if (message.Action == "sync" && message.IsPlaying)
						{
							_controlWindow.OTTReceivePlayPause(true, -1);
							Services.Log.Debug("[OTT] Received play signal");
						}
					}
					else if (doc.RootElement.TryGetProperty("playbackPosition", out _))
					{
						SeekEvent message = JsonSerializer.Deserialize<SeekEvent>(response, _jsonOptions)!;
						if (message.Action == "sync")
						{
							_controlWindow.OTTReceiveSeek(message.PlaybackPosition);

							Services.Log.Debug("[OTT] Received seek signal to " + message.PlaybackPosition);

							if (!_connectionReady)
							{
								_connectionReady = true;
								if (_pushNextVideo)
								{
									PushVideo();
								}
							}
						}
					}
				}
			}
		}
		catch (Exception)
		{
			Services.Log.Debug("[OTT] Notice: Could not parse response");
		}
	}

	public static class Requests
	{
		public record Auth(string Token)
		{
			public static string URL => "/api/auth/grant";

			public static async Task<Auth?> Execute(HttpClient client)
			{
				System.Net.Http.HttpResponseMessage result = await client.GetAsync(OTTApi._url + URL);

				if (result.IsSuccessStatusCode)
				{
					return await result.Content.ReadFromJsonAsync<Auth>();
				}
				else
				{
					Services.Log.Error("[OTT] Failed generating grant");
				}

				return null;
			}
		}
		public record Generate(bool Success, string Room)
		{
			public static string URL => "/api/room/generate";

			public static async Task<Generate?> Execute(HttpClient client, string token)
			{
				FormUrlEncodedContent parameters = new(new Dictionary<string, string> { { "token", token } });

				System.Net.Http.HttpResponseMessage result = await client.PostAsJsonAsync(OTTApi._url + URL, parameters);

				if (result.IsSuccessStatusCode)
				{
					return await result.Content.ReadFromJsonAsync<Generate>();
				}
				else
				{
					Services.Log.Error("[OTT] Failed generating room: " + result.Content);
				}

				return null;
			}
		}
		public record Video(
			[property: JsonPropertyName("service")] string Service,
			[property: JsonPropertyName("id")] string Id,
			[property: JsonPropertyName("title")] string Title,
			[property: JsonPropertyName("description")] string Description,
			[property: JsonPropertyName("mime")] string? Mime,
			[property: JsonPropertyName("thumbnail")] string? Thumbnail,
			[property: JsonPropertyName("length")] int Length,
			[property: JsonPropertyName("hls_url")] string? HlsUrl);
		public record PreviewAdd(bool Success, List<Video> Result, Video Highlighted)
		{
			public static string URL => "/api/data/previewAdd";

			public static async Task<PreviewAdd?> Execute(HttpClient client, string url)
			{
				url = Uri.EscapeDataString(url);
				Services.Log.Debug("Preview OTT API: " + OTTApi._url + URL + "?input=" + url);
				System.Net.Http.HttpResponseMessage result = await client.GetAsync(OTTApi._url + URL + "?input=" + url);

				string resstring = await result.Content.ReadAsStringAsync();
				Services.Log.Debug(resstring);
				if (result.IsSuccessStatusCode)
				{
					return await result.Content.ReadFromJsonAsync<PreviewAdd>();
				}
				else
				{
					Services.Log.Debug("[OTT] Failed adding URL");
				}

				return null;
			}
		}
		public record RemoveVideo(bool Success)
		{
			public static string URL => "/api/room/";

			public static async Task<RemoveVideo?> Execute(HttpClient client, string room, Video video)
			{
				var payload = new
				{
					service = video.Service,
					id = video.Id
				};

				HttpRequestMessage request = new()
				{
					Method = HttpMethod.Delete,
					RequestUri = new Uri(OTTApi._url + URL + room + "/queue"),
					Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
				};

				System.Net.Http.HttpResponseMessage result = await client.SendAsync(request);

				if (result.IsSuccessStatusCode)
				{
					return await result.Content.ReadFromJsonAsync<RemoveVideo>();
				}
				else
				{
					Services.Log.Error("[OTT] Failed removing video");
				}

				return null;
			}
		}
		public record AddVideo(bool Success)
		{
			public static string URL => "/api/room/";

			public static async Task<AddVideo?> Execute(HttpClient client, string room, Video video)
			{
				var payload = new
				{
					service = video.Service,
					id = video.Id
				};

				HttpRequestMessage request = new()
				{
					Method = HttpMethod.Post,
					RequestUri = new Uri(OTTApi._url + URL + room + "/queue"),
					Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
				};

				System.Net.Http.HttpResponseMessage result = await client.SendAsync(request);

				if (result.IsSuccessStatusCode)
				{
					return await result.Content.ReadFromJsonAsync<AddVideo>();
				}
				else
				{
					Services.Log.Error("[OTT] Failed adding video");
				}

				return null;
			}
		}
	}
}
