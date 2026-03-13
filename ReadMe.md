# HiAnime To MyAnimeList

This tool aims to add shows from your HiAnime watchlist to your MyAnimeList account.

Why, you might ask? As of now, HiAnime is down and I luckly have a back-up of my watchlist before HiAnime was taken offline. With this tool I can add my whole watchlist to MAL and then import it from MAL on other streaming sites.

## How it works

This tool reads the HiAnime exported json containing the watchlist data and adds them to your MAL watchlist via the MAL API.

## Usage

> Requirements:
> - a MyAnimeList account
> - HiAnime watchlist as json
> - C# .Net 8.0 SDK

1. Ensure you have exported your HiAnime watchlist as a json file.
2. Go to [myanimelist.net/apiconfig](https://myanimelist.net/apiconfig), login and create a client
3. Fill out the required data and set the `Redirect URL` to `http://127.0.0.1:9876/` (**important!!**)
4. Build this tool with the command `dotnet build`, or open the project in Visual Studio and press "Build"
5. Run this tool with the command `dotnet run [MAL ClientId] [MAL Client Secret] [Watchlist Path]`, or start it in Visual Studio (requires to add these parameters in the startup/debug settings)
6. [optional] Import your MAL watchlist on your new streaming site of choice. Enjoy!:) 

## ToDo

- Check which shows are already on the MAL watchlist before adding/updating -> reduces API calls