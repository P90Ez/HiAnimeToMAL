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

			//read and parse json watchlist
			Console.WriteLine("Reading watchlist...");
			Watchlist? Watchlist = Watchlist.FromFile(WatchlistFilename);
			if (Watchlist == null)
			{
				Console.WriteLine("Failed to read/parse watchlist!");
				return -2;
			}
			Console.WriteLine($"Found {Watchlist.Completed.Count + Watchlist.Dropped.Count + Watchlist.OnHold.Capacity + Watchlist.PlanToWatch.Count + Watchlist.Watching.Count} shows on watchlist!");

			//get access to user's MAL
			Console.WriteLine("Logging in with MAL...");
			MALAPI? API = MALAPI.CreateAndAuthorize(ClientId, ClientSecret);
			if (API == null)
			{
				Console.WriteLine("Failed to access My Anime List!");
				return -3;
			}

			//get existing watchlist
			Console.WriteLine("Requesting user watchlist...");
			Dictionary<ulong, string> ExistingWatchlist = API.GetUserWatchlistShows();
			Console.WriteLine($"Got {ExistingWatchlist.Count} shows from user watchlist!");

			//process and update watchlist
			Console.WriteLine("Adding shows to MAL watchlist...");

			int Updates = 0;
			Updates += ProcessSubList(API, Watchlist.Completed, "completed", ExistingWatchlist);
			Updates += ProcessSubList(API, Watchlist.OnHold, "on_hold", ExistingWatchlist);
			Updates += ProcessSubList(API, Watchlist.Watching, "watching", ExistingWatchlist);
			Updates += ProcessSubList(API, Watchlist.Dropped, "dropped", ExistingWatchlist);
			Updates += ProcessSubList(API, Watchlist.PlanToWatch, "plan_to_watch", ExistingWatchlist);

			Console.WriteLine($"Completed successfully - updated {Updates} shows!");
			return 0;
		}
		
		/// <summary>
        /// Updates a part of the watchlist.
        /// </summary>
        /// <returns>Returns the number of updated items on this part of the list.</returns>
		private static int ProcessSubList(MALAPI API, List<Watchlist.Entry> SubList, string Status, Dictionary<ulong, string> ExistingWatchlist)
		{
			int UpdatedItems = 0;

			foreach (Watchlist.Entry Show in SubList)
			{
				if (Show.MALId != null)
				{
					//check if current show is already on watchlist with same status
					bool AlreadyOnWatchlist = ExistingWatchlist.ContainsKey((ulong)Show.MALId) && ExistingWatchlist[(ulong)Show.MALId] == Status;

					if (!AlreadyOnWatchlist)
					{
						bool Success = API.UpdateWatchlist((ulong)Show.MALId, Status);
						if (!Success) Console.WriteLine($"Failed to update/add anime \"{Show.Name}\"! Skipping...");
						else UpdatedItems++;
					}
				}
				else
                {
                    Console.WriteLine($"Show \"{Show.Name}\" does not have a valid MAL id attached - skipping...");
                }
			}

			return UpdatedItems;
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
			public ulong? MALId;
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
				if (Content == null) return null;

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

		/// <summary>
		/// Updates / Adds a show to the specified watchlist.
		/// </summary>
		/// <param name="AnimeId">Id of the show to update/add.</param>
		/// <param name="Status">Watch status - possible values are: watching, on_hold, plan_to_watch, completed, dropped</param>
		/// <returns>True on success, false otherwise.</returns>
		public bool UpdateWatchlist(ulong AnimeId, string Status)
		{
			HttpClient Client = new HttpClient();
			Client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AccessToken);

			FormUrlEncodedContent Content = new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["status"] = Status
			});

			int MaxRetries = 3;
			int Retries = 0;

			//retry for x times - otherwise tiny connection spikes would lead to errors 
			while (Retries < MaxRetries)
			{
				try
				{
					//send PATCH and get response
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

		/// <summary>
        /// Gets the shows from the users watchlist.
        /// </summary>
        /// <returns>A dictionary, with MALId as key and watch-status as value.</returns>
		public Dictionary<ulong, string> GetUserWatchlistShows()
		{
			//1. get watchlist from API
			HttpClient Client = new HttpClient();
			Client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AccessToken);

			List<MalUserAnimeListResponse.Datum> Shows = new List<MalUserAnimeListResponse.Datum>();

			string RequestURL = "https://api.myanimelist.net/v2/users/@me/animelist?fields=list_status&limit=100";

			int MaxRetries = 3;
			int Retries = 0;
			bool Failed = true;
			string LastErrorMessage = "";

			//pagination -> possibly multiple pages with data -> request until no more pages
			//retry for x times - otherwise tiny connection spikes would lead to errors 
			while (RequestURL != "" && Retries < MaxRetries)
			{
				try
				{
					//send GET and get response
					HttpResponseMessage RawResponse = Client.GetAsync(RequestURL).Result;

					string ResponseBody = RawResponse.Content.ReadAsStringAsync().Result;

					if (RawResponse.IsSuccessStatusCode)
					{
						// -> request successful -> parse data and add data to collection
						MalUserAnimeListResponse? ParsedResponse = JsonConvert.DeserializeObject<MalUserAnimeListResponse>(ResponseBody);

						if (ParsedResponse != null)
						{
							Shows.AddRange(ParsedResponse.Data);

							//check if there are more pages with data -> request the next page
							if (ParsedResponse.Pagination != null && ParsedResponse.Pagination.Next != null)
							{
								RequestURL = ParsedResponse.Pagination.Next;
							}
							else
							{
								RequestURL = "";
							}

							Failed = false;
						}
					}
					else
					{
						LastErrorMessage = ResponseBody;
					}
				}
				catch { }

				if (Failed)
				{
					Retries++;
					Thread.Sleep(500);
				}
				else
				{
					Retries = 0;
				}
			}
			
			if(Failed && Retries >= MaxRetries)
            {
				Console.WriteLine("Failed to get full user watchlist - there might be partial data available. Error: " + LastErrorMessage);
            }

			//2. parse collected shows
			Dictionary<ulong, string> Output = new Dictionary<ulong, string>();

			foreach (MalUserAnimeListResponse.Datum Show in Shows)
			{
				Output.Add(Show.Node.Id, Show.ListStatus.Status);
			}

			return Output;
		}
		
		private class MalUserAnimeListResponse
		{
			//removed non-required fields
			
            [JsonProperty("data")]
			public List<Datum> Data { get; set; }

			[JsonProperty("paging")]
			public Paging? Pagination { get; set; }

			public class Datum
			{
				[JsonProperty("node")]
				public Node Node { get; set; }

				[JsonProperty("list_status")]
				public ListStatus ListStatus { get; set; }
			}

			public class ListStatus
			{
				[JsonProperty("status")]
				public string Status { get; set; }
			}

			public class Node
			{
				[JsonProperty("id")]
				public ulong Id { get; set; }

				[JsonProperty("title")]
				public string Title { get; set; }
			}

			public class Paging
			{
				[JsonProperty("next")]
				public string? Next { get; set; }
			}
        }

		/// <summary>
        /// Authorizes the client to access a users watchlist (opens a browser pop-up) and creates a MALAPI object.
        /// </summary>
        /// <param name="ClientId">MAL client id.</param>
        /// <param name="ClientSecret">MAL client secret.</param>
        /// <returns>MALAPI object on success, otherwise null.</returns>
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

			if (Token == null || !RawPostResponse.IsSuccessStatusCode)
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