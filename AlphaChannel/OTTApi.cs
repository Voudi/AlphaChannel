using AlphaChannel;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

public class OTTApi
{
	private static readonly string URL = "https://opentogethertube.com";
	private static readonly string WSURL = "wss://opentogethertube.com";
    private readonly HttpClient _httpClient = new();
    private ClientWebSocket _wsocket;
    private bool _isInitialized;
    private string _token = string.Empty;
	private string _room = string.Empty;
    private bool _isDisposed = false;
    private readonly ControlWindow _controlWindow;
    private readonly List<Requests.Video> queue = [];
    public List<Requests.Video> GetQueue => queue;
    public string GetRoomURL => URL + "/room/" + _room;
    public bool IsInRoom => !string.IsNullOrEmpty(_room);
    private Requests.Video? _video;
    public string _videoURL { get; private set; }
    private bool _checkingURL = false;
    private bool _checkFailed = false;
    public bool LastCheckSuccessful => !_checkingURL && _video is not null && !_checkFailed;
    public bool IsChecking => _checkingURL && !_checkFailed;
    private readonly JsonSerializerOptions _jsonOptions;
    public OTTApi(ControlWindow controlWindow)
	{
        _controlWindow = controlWindow;
        _jsonOptions = new JsonSerializerOptions{ PropertyNameCaseInsensitive = true };
        _videoURL = string.Empty;
    }

	public void Dispose()
	{
        _isDisposed = true;
        if (_wsocket != null && _wsocket.State == WebSocketState.Open)
        {
            _wsocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
        }
        _wsocket?.Dispose();
    }

    public async void CheckURL(string url)
    {
        if (url.Equals(_videoURL) || _checkingURL)
            return;
        _checkingURL = true;
        _checkFailed = false;
        _video = null;
        _videoURL = url;

        try { 
            var PreviewAdd = await Requests.PreviewAdd.Execute(_httpClient, url);
            if (PreviewAdd != null && PreviewAdd.Success)
            {
                if(PreviewAdd.Highlighted != null)
                    _video = PreviewAdd.Highlighted;
                else
                    _video = PreviewAdd.Result.First();
                
                Services.Log.Debug("[OTT] URL Check succeeded: " + _video.title);
                _checkingURL = false;
                return;
            }
        }
        catch (Exception)
        {}
        Services.Log.Debug("[OTT] URL check failed");
        _checkFailed = true;
        await Task.Delay(1000);
        _checkingURL = false;
    }

	public async Task Initialize(string? roomId = null)
	{
        if(_wsocket != null)
        {
            await LeaveRoom();
        }

        try
        {
            var Auth = await Requests.Auth.Execute(_httpClient);
            if (Auth != null)
            {
                _token = Auth.Token;
            }
            else
            {
                return;
            }
            if (string.IsNullOrEmpty(roomId))
            {
                var GenerateRoom = await Requests.Generate.Execute(_httpClient, _token);
                if (GenerateRoom != null && GenerateRoom.Success)
                {
                    _room = GenerateRoom.Room;
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
            _isInitialized = false;
            return;
        }

        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);

        try
        {
            await ConnectWSS(_room, _token);
        }
        catch (Exception e)
        {
            Services.Log.Debug("[OTT] WS Connection closed: " + e.Message);
        }
        finally
        {
            _wsocket?.Dispose();
        }

        Services.Log.Debug("[OTT] Left room");
    }

    public async Task LeaveRoom()
    {
        try
        {
            _room = string.Empty;
            await _wsocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
        }
        catch{}        
    }

	private async Task ConnectWSS(string room, string token)
	{
        var uri = new Uri(WSURL + "/api/room/" + room);

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

        var buffer = new byte[65536];
        while (_wsocket.State == WebSocketState.Open)
        {
            var result = await _wsocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            string response = Encoding.UTF8.GetString(buffer, 0, result.Count);
            //Services.Log.Debug("OTT API RECEIVED: " + response);
            if (response != null && response.Length > 5)
                OnQueueReceived(response);
        }
    }

    private record PushNextVideoRequest(int type, Requests.Video video);
    public async void PushNextVideo()
    {
        SendRequest(new
            {
                action = "req",
                request = new PushNextVideoRequest(14, _video!)
            });
    }

    private record PlayPauseRequest(int type, bool state);
    public async void PlayPauseVideo(bool play)
    {
        SendRequest(new
            {
                action = "req",
                request = new PlayPauseRequest(2, play)
            });
    }
    private record SeekRequest(int type, double value);
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
        if(_wsocket.State == WebSocketState.Open)
        {
            string jsonMessage = JsonSerializer.Serialize(message);
            
            byte[] bytes = Encoding.UTF8.GetBytes(jsonMessage);

            await _wsocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None
            );
        }
    }

    private record PushNextVideoEvent([property: JsonRequired] string Action, List<Requests.Video> Queue, Requests.Video CurrentSource, [property: JsonRequired] double PlaybackPosition, string Hls_url);
    private record QueueEvent([property: JsonRequired] string Action, [property: JsonRequired] List<Requests.Video> Queue);
    private record PauseEvent([property: JsonRequired] string Action, [property: JsonRequired] double PlaybackPosition, [property: JsonRequired] bool IsPlaying);
    private record PlayEvent([property: JsonRequired] string Action, [property: JsonRequired] bool IsPlaying);
    private record SeekEvent([property: JsonRequired] string Action, [property: JsonRequired] double PlaybackPosition);
    private void OnQueueReceived(string response)
    {
        try
        {
            using var doc = JsonDocument.Parse(response);
            {
                if(doc.RootElement.TryGetProperty("action", out _)){
                    if(doc.RootElement.TryGetProperty("currentSource", out _) && doc.RootElement.TryGetProperty("playbackPosition", out _))
                    {
                        PushNextVideoEvent message = JsonSerializer.Deserialize<PushNextVideoEvent>(response, _jsonOptions)!;
                        if (message.Action == "sync")
                        {
                            queue.Clear();
                            if(message.Queue != null)
                            queue.AddRange(message.Queue);

                            var url = "";
                            if(message.CurrentSource != null && message.CurrentSource.service != null)
                            switch (message.CurrentSource.service)
                            {
                                case "youtube":
                                    url = $"https://youtu.be/{ message.CurrentSource.id }"; break;
                                case "vimeo":
                                    url = $"https://vimeo.com/{ message.CurrentSource.id }"; break;
                                case "direct":
                                    url = message.CurrentSource.id; break;
                                case "hls":
                                    url = message.CurrentSource.hls_url ?? message.CurrentSource.id; break;
                                default: break;
                            }
                            if(string.IsNullOrEmpty(url))
                                return;
                            _video = message.CurrentSource;
                            _videoURL = url;
                            _controlWindow.OTTReceiveNewVideo();

                            Services.Log.Debug("[OTT] Received new video");
                        }
                    }
                    else if(doc.RootElement.TryGetProperty("queue", out _))
                    {
                        QueueEvent message = JsonSerializer.Deserialize<QueueEvent>(response, _jsonOptions)!;
                        if (message.Action == "sync")
                        {
                            queue.Clear();
                            if(message.Queue != null)
                                queue.AddRange(message.Queue);
                            Services.Log.Debug("[OTT] Received queue");
                        }
                    }
                    else if(doc.RootElement.TryGetProperty("playbackPosition", out _) && doc.RootElement.TryGetProperty("isPlaying", out _))
                    {
                        PauseEvent message = JsonSerializer.Deserialize<PauseEvent>(response, _jsonOptions)!;
                        if (message.Action == "sync" && !message.IsPlaying)
                        {
                            _controlWindow.OTTReceivePlayPause(false, message.PlaybackPosition);
                            Services.Log.Debug("[OTT] Received pause signal at " + message.PlaybackPosition + " seconds");
                        }
                        
                    }
                    else if(doc.RootElement.TryGetProperty("isPlaying", out _))
                    {
                        PlayEvent message = JsonSerializer.Deserialize<PlayEvent>(response, _jsonOptions)!;
                        if (message.Action == "sync" && message.IsPlaying)
                        {
                            _controlWindow.OTTReceivePlayPause(true, -1);
                            Services.Log.Debug("[OTT] Received play signal");
                        }
                    }
                    else if(doc.RootElement.TryGetProperty("playbackPosition", out _))
                    {
                        SeekEvent message = JsonSerializer.Deserialize<SeekEvent>(response, _jsonOptions)!;
                        if (message.Action == "sync")
                        {
                            _controlWindow.OTTReceiveSeek(message.PlaybackPosition);
                            Services.Log.Debug("[OTT] Received seek signal");
                        }
                    }
                }   
            }
        }
        catch(Exception)
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
                var result = await client.GetAsync(OTTApi.URL + URL);

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
                var parameters = new FormUrlEncodedContent(new Dictionary<string, string> { { "token", token } });

                var result = await client.PostAsJsonAsync(OTTApi.URL + URL, parameters);

                if (result.IsSuccessStatusCode)
                {
                    var x = await result.Content.ReadFromJsonAsync<Generate>();
                    return x;
                }
                else
                {
                    Services.Log.Error("[OTT] Failed generating room");
                }

                return null;
            }
        }
        public record Video(string service, string id, string title, string description, string? mime, string? thumbnail, int length, string hls_url);
        public record PreviewAdd(bool Success, List<Video> Result, Video Highlighted)
        {
            public static string URL => "/api/data/previewAdd";

            public static async Task<PreviewAdd?> Execute(HttpClient client, string url)
            {
                url = Uri.EscapeDataString(url);
                Services.Log.Debug("Preview OTT API: " + OTTApi.URL + URL + "?input=" + url);
                var result = await client.GetAsync(OTTApi.URL + URL + "?input=" + url);
                
                var resstring = await result.Content.ReadAsStringAsync();
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
                    service = video.service,
                    id = video.id
                };

                var request = new HttpRequestMessage
                {
                    Method = HttpMethod.Delete,
                    RequestUri = new Uri(OTTApi.URL + URL + room + "/queue"),
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                };

                var result = await client.SendAsync(request);

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
                    service = video.service,
                    id = video.id
                };

                var request = new HttpRequestMessage
                {
                    Method = HttpMethod.Post,
                    RequestUri = new Uri(OTTApi.URL + URL + room + "/queue"),
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                };

                var result = await client.SendAsync(request);

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