using System;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Newtonsoft.Json;

namespace HiAnimeToMAL
{
	internal class Program
	{
		static int Main(string[] args)
		{
			if (args.Length != 3)
			{
				Console.WriteLine("Usage: dotnet run [MAL Client Id] [MAL Client Secret] [WatchList Filename]");
				return -1;
			}

			string ClientId = args[0];
			string ClientSecret = args[1];
			string WatchlistFilename = args[2];

			Console.WriteLine("Reading watchlist...");
			Watchlist? Watchlist = Watchlist.FromFile(WatchlistFilename);
			if (Watchlist == null)
			{
				Console.WriteLine("Failed to read/parse watchlist!");
				return -2;
			}

			Console.WriteLine("Logging in with MAL...");
			MALAPI? API = MALAPI.CreateAndAuthorize(ClientId, ClientSecret);
			if (API == null)
			{
				Console.WriteLine("Failed to access My Anime List!");
				return -3;
			}

			Console.WriteLine("Adding shows to MAL watchlist...");

			//TODO: check which shows are already on watchlist and only update missing/changed ones
			ProcessSubList(API, Watchlist.Completed, "completed");
			ProcessSubList(API, Watchlist.OnHold, "on_hold");
			ProcessSubList(API, Watchlist.Watching, "watching");
			ProcessSubList(API, Watchlist.Dropped, "dropped");
			ProcessSubList(API, Watchlist.PlanToWatch, "plan_to_watch");

			Console.WriteLine("Completed successfully!");
			return 0;
		}
		
		private static void ProcessSubList(MALAPI API, List<Watchlist.Entry> SubList, string Status)
        {
            foreach (Watchlist.Entry Show in SubList)
			{
				bool Success = API.UpdateWatchlist(Show.MALId, Status);
				if (!Success) Console.WriteLine($"Failed to update/add anime \"{Show.Name}\"! Skipping...");
			}
        }
	}
	
	class Watchlist
	{
		public struct Entry
		{
			[JsonProperty(PropertyName = "link")]
			public string Link;
			[JsonProperty(PropertyName = "name")]
			public string Name;
			[JsonProperty(PropertyName = "mal_id")]
			public ulong MALId;
			[JsonProperty(PropertyName = "watchListType")]
			public int ListType;
		}

		public List<Entry> Watching = [];

		[JsonProperty(PropertyName = "On-Hold")]
		public List<Entry> OnHold = [];

		public List<Entry> Completed = [];

		[JsonProperty(PropertyName = "Plan to Watch")]
		public List<Entry> PlanToWatch = [];
		
		public List<Entry> Dropped = [];

		public static Watchlist? FromFile(string Path)
		{
			if (!File.Exists(Path)) return null;

			try
			{
				string? Content = File.ReadAllText(Path);
				if (Content == null) return default;

				return JsonConvert.DeserializeObject<Watchlist>(Content);
			}
			catch { }

			return null;
		}
	}

	public class MALAPI
	{
		string ClientId { get; }
		string AccessToken { get; }
		static readonly string RedirectURI = "http://127.0.0.1:9876/";

		private MALAPI(string ClientId, string AccessToken)
		{
			this.AccessToken = AccessToken;
			this.ClientId = ClientId;
		}

		public bool UpdateWatchlist(ulong AnimeId, string Status)
		{
			var Client = new HttpClient();
			Client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AccessToken);

			var Content = new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["status"] = Status
			});

			int MaxRetries = 3;
			int Retries = 0;

			while (Retries < MaxRetries)
			{
				try
				{
					HttpResponseMessage RawResponse = Client.PatchAsync($"https://api.myanimelist.net/v2/anime/{AnimeId}/my_list_status", Content).Result;

					if (!RawResponse.IsSuccessStatusCode)
					{
						string ResponseBody = RawResponse.Content.ReadAsStringAsync().Result;
						Console.WriteLine($"Failed to update MAL watchlist (id: {AnimeId}): " + ResponseBody);
					}

					return RawResponse.IsSuccessStatusCode;
				}
				catch { }

				Retries++;
				Thread.Sleep(500);
			}

			Console.WriteLine($"Failed to update MAL watchlist (id: {AnimeId}): Failed to connect to MAL API (rate limit?)");
			return false;
		}

		public static MALAPI? CreateAndAuthorize(string ClientId, string ClientSecret)
		{
			//obtain "AccessCode" from client/user
			HttpListener Listener = new HttpListener();
			Listener.Prefixes.Add(RedirectURI);
			Listener.Start();

			string CodeVerifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
				.TrimEnd('=')
				.Replace('+', '-')
				.Replace('/', '_');

			string AuthURL =
				"https://myanimelist.net/v1/oauth2/authorize" +
				$"?response_type=code" +
				$"&client_id={ClientId}" +
				$"&code_challenge={CodeVerifier}";

			//open browser with url
			Process.Start(new ProcessStartInfo
			{
				FileName = AuthURL,
				UseShellExecute = true
			});

			//after login, await callback from MAL with AccessCode
			string? AccessCode = null;
			while (AccessCode == null)
			{
				HttpListenerContext Context = Listener.GetContext();
				HttpListenerRequest Request = Context.Request;
				HttpListenerResponse Response = Context.Response;

				AccessCode = Request.QueryString["code"];

				Response.StatusCode = 200;
				Response.Close();
			}

			Listener.Stop();

			//exchange AccessCode for AccessToken
			var Client = new HttpClient();

			var PostContent = new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["client_id"] = ClientId,
				["client_secret"] = ClientSecret,
				["grant_type"] = "authorization_code",
				["code"] = AccessCode,
				["code_verifier"] = CodeVerifier
			});

			HttpResponseMessage RawPostResponse = Client.PostAsync("https://myanimelist.net/v1/oauth2/token", PostContent).Result;

			string PostResponse = RawPostResponse.Content.ReadAsStringAsync().Result;
			MalTokenResponse? Token = JsonConvert.DeserializeObject<MalTokenResponse>(PostResponse);

			if(Token == null || !RawPostResponse.IsSuccessStatusCode)
			{
				Console.WriteLine("Failed to get MAL AccessToken!\n" + PostResponse);
				return null;
			}

			return new MALAPI(ClientId, Token.AccessToken);
		}
		
		private class MalTokenResponse
		{
			[JsonProperty("token_type")]
			public string TokenType { get; set; } = "";

			[JsonProperty("expires_in")]
			public int ExpiresIn { get; set; }

			[JsonProperty("access_token")]
			public string AccessToken { get; set; } = "";

			[JsonProperty("refresh_token")]
			public string RefreshToken { get; set; } = "";
		}
	}
}