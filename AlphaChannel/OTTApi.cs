using AlphaChannel;
using Newtonsoft.Json.Bson;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Policy;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

public class OTTApi
{
	private static readonly string URL = "https://opentogethertube.com";
	private static readonly string WSURL = "wss://opentogethertube.com";
    private readonly HttpClient _httpClient = new HttpClient();
    private readonly ClientWebSocket _wsocket = new ClientWebSocket();
    private string _token = string.Empty;
	private string _room = string.Empty;
    private bool _isDisposed = false;
    private ControlWindow _controlWindow;

    public bool initialized = false;
    private readonly List<Requests.Video> queue = [];
    public List<Requests.Video> GetQueue => queue;
    public string GetRoomURL => URL + "/room/" + _room;



    private Requests.Video? _video;
    private string _videoURL = string.Empty;
    private bool _checkingURL = false;
    private bool _checkFailed = false;
    public bool LastCheckSuccessful => !_checkingURL && _video is not null && !_checkFailed;
    public bool IsChecking => _checkingURL && !_checkFailed;
    public OTTApi(ControlWindow controlWindow)
	{
        _controlWindow = controlWindow;
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
        Services.Log.Debug("Checking URL for sync..." + url);
        _checkFailed = false;
        _video = null;
        _videoURL = url;

        var PreviewAdd = await Requests.PreviewAdd.Execute(_httpClient, url);
        if (PreviewAdd != null && PreviewAdd.Success)
        {
            _video = PreviewAdd.Result.First();
            Services.Log.Debug("Success! Video is " + _video.title);
        }
        else
        {
            Services.Log.Debug("Check failed...");
            _checkFailed = true;
            await Task.Delay(1000);
        }
        _checkingURL = false;
    }

	public async void Login()
	{
        var Auth = await Requests.Auth.Execute(_httpClient);
		if (Auth != null) {
            _token = Auth.Token;
        }
		else
		{
			//Throw Exception!
		}
        initialized = true;

        var GenerateRoom = await Requests.Generate.Execute(_httpClient, _token);
        if (GenerateRoom != null && GenerateRoom.Success)
        {
            _room = GenerateRoom.Room;
            Services.Log.Debug("OTT Room Generated: " + _room);
        }
        else
        {
            //Throw Exception!
        }

        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);

        while (!_isDisposed)
        {
            try
            {
                await ConnectWSS(_room, _token);
            }
            catch (Exception)
            {
                //For whatever reason
            }
            if(!_isDisposed)
                await Task.Delay(5000);
        }
    }

	private async Task ConnectWSS(string room, string token)
	{
        var uri = new Uri(WSURL + "/api/room/" + room);

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
            Services.Log.Debug("RECEIVED: " + response);
            if (response != null)
                OnQueueReceived(response);
        }
    }

    private record Queue(string action, List<Requests.Video> queue);
    private record Stop(string action, bool isPlaying, int playbackPosition, string? currentSource, int playbackSpeed);
    private void OnQueueReceived(string response)
    {
        try
        {
            var message = JsonSerializer.Deserialize<Queue>(response, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (message?.action == "sync")
            {
                queue.Clear();
                queue.AddRange(message.queue);
                Services.Log.Debug("Received Queue with " + message.queue.Count + " videos!");
            }

            //Ignore Received Msg
        }
        catch (Exception)
        {
            try
            {
                var message = JsonSerializer.Deserialize<Stop>(response, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (message?.action == "sync" && message?.isPlaying == false)
                {
                    Services.Log.Debug("Received Stop!");
                }

                //Ignore Received Msg
            }
            catch (Exception)
            {

                //Ignore Received Msg
            }
            //Ignore Received Msg
        }
    }

    private record PlayRequest(int type, Requests.Video video);
    public async void ForcePlayVideo()
    {
        if(_wsocket.State == WebSocketState.Open && _video is not null)
        {
            var message = new
            {
                action = "req",
                request = new PlayRequest(14, _video)
            };

            string jsonMessage = JsonSerializer.Serialize(message);
            
            Services.Log.Debug("SENDING: " + jsonMessage);
            byte[] bytes = Encoding.UTF8.GetBytes(jsonMessage);

            await _wsocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None
            );
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

                return null;
            }
        }
        public record Video(string service, string id, string title, string description, string? mime, string? thumbnail, int length);
        public record PreviewAdd(bool Success, List<Video> Result)
        {
            public static string URL => "/api/data/previewAdd";

            public static async Task<PreviewAdd?> Execute(HttpClient client, string url)
            {
                url = Uri.EscapeDataString(url);

                var result = await client.GetAsync(OTTApi.URL + URL + "?input=" + url);

                if (result.IsSuccessStatusCode)
                {
                    return await result.Content.ReadFromJsonAsync<PreviewAdd>();
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

                return null;
            }
        }
    }
}