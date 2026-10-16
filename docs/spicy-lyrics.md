# Word-by-word lyrics (Spicy Lyrics)

The media popup's lyrics normally come from lrclib.net, line by line. With the Spicy Lyrics API set up, the popup asks it first and
lights up each word as it is sung (and falls back to lrclib when it has nothing, or is not reachable).

It needs three values, all free, none of them part of the program or of the repository:

| Setting (`settings.json`) | Environment variable | What it is |
| --- | --- | --- |
| `SpicyLyricsKey` | `RETROBAR_SPICY_KEY` | A key from <https://developers.spicylyrics.org> (a secret key, `sl_sk_...`). |
| `SpotifyClientId` | `RETROBAR_SPOTIFY_CLIENT_ID` | The client id of a Spotify developer app (<https://developer.spotify.com/dashboard>). |
| `SpotifyClientSecret` | `RETROBAR_SPOTIFY_CLIENT_SECRET` | Its client secret. |

`settings.json` is in `%LOCALAPPDATA%\RetroBar`. The Spicy Lyrics API finds lyrics by a Spotify track id, which Windows does not tell
the player, so the id is found with Spotify's search (client credentials: no user is signed in, nothing of the account is used).

Ids and lyrics found are kept in `%LOCALAPPDATA%\RetroBar` (`spotify-track-ids.json`, `spicy-lyrics\`), so a track is asked for once.

The terms of the API ask for the provider (and the uploader of community lyrics) to be shown wherever the lyrics are. The popup does
not show a credit line (it was taken out on purpose), so check their terms for what that means for your use.
