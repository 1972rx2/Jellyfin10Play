# Jellyfin 10 Play plugin (test build)

This is an early Jellyfin 12.4 plugin for browsing 10 Play using the provider's official device-code sign-in.

## What this build tests

1. Jellyfin loads the plugin.
2. The **10 Play** dashboard page can begin the device-code sign-in flow.
3. After approval at `10.com.au/activate`, Jellyfin saves the account session locally and shows a **10 Play** channel with Featured, Shows and (optionally) Live TV sections.
4. The **Featured** collections open shows, seasons, and episode entries from 10 Play.
5. Selecting an episode requests the provider's on-demand HLS session at playback time. Expired account tokens are refreshed using the saved refresh token.

Version 0.1.19 exposes Featured and the provider's full Shows category menu, connecting both to seasons, episodes, and the on-demand stream resolver. Featured includes 24 blue category tiles with consistent text sizing, bundled with the plugin and linked to their matching collections. The 10 Play logo appears in the plugin catalog and settings page. Live TV and Extras are not connected yet. Playback requires a signed-in account and a working Jellyfin playback/transcoding setup. The expiry parser accepts integer, decimal and text token lifetimes.

## Install and test

1. Create a folder called `TenPlay` in the Jellyfin plugin directory.
2. Extract **all contents** of `outputs/Jellyfin.Plugin.TenPlay-0.1.19-jellyfin12.4-plugin-logo.zip` into that folder. Keep `logo.png` and the `Images/FeaturedTiles` folder beside the DLL.
3. Restart Jellyfin.
4. In the Jellyfin dashboard, open **Plugins → 10 Play**.
5. Select **Sign in to 10 Play**.
6. Open `https://10.com.au/activate`, enter the displayed code, and approve the device in your own 10 Play account.
7. Wait for Jellyfin to report that sign-in completed, then open the 10 Play channel.

The provider tokens are held in Jellyfin's local plugin configuration. Treat the Jellyfin server and its configuration backups as sensitive.

This plugin is not affiliated with Network 10 or Jellyfin. It does not remove or bypass advertising; it will play only the stream the provider returns for the signed-in account.
## Install from Jellyfin's Plugin Catalog

Add this URL under **Dashboard → Plugins → Repositories**:

https://raw.githubusercontent.com/1972rx2/Jellyfin10Play/main/manifest.json

The current catalogue entry is version 0.1.19.0 and targets Jellyfin 12.4.0.0. It includes the 10 Play catalog/settings logo, Featured tiles, and a bundled metadata file that explicitly identifies the plugin assembly Jellyfin should load.


