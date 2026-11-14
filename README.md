# Jellyfin 10 Play plugin (test build)

This is an early Jellyfin 12.4 plugin for browsing 10 Play using the provider's official device-code sign-in.

## What this build tests

1. Jellyfin loads the plugin.
2. The **10 Play** dashboard page can begin the device-code sign-in flow.
3. After approval at `10.com.au/activate`, Jellyfin saves the account session locally and shows a **10 Play** channel with Featured, Shows and (optionally) Live TV sections.
4. The **Featured** collections open shows, seasons, and episode entries from 10 Play.
5. Selecting an episode requests the provider's on-demand HLS session at playback time. Expired account tokens are refreshed using the saved refresh token.

Version 0.1.14 exposes Featured and the provider's full Shows category menu, connecting both to seasons, episodes, and the on-demand stream resolver. Big Brother is listed under Shows → Reality in the verified catalogue. Live TV and Extras are not connected yet. Playback requires a signed-in account and a working Jellyfin playback/transcoding setup. The expiry parser accepts integer, decimal and text token lifetimes.

## Install and test

1. Extract `Jellyfin.Plugin.TenPlay.dll` from `outputs/Jellyfin.Plugin.TenPlay-0.1.14-jellyfin12.4-test.zip` into a folder you create called 'TenPlay' in the JellyFin plugin directory, 
2. Restart Jellyfin.
3. In the Jellyfin dashboard, open **Plugins → 10 Play**.
4. Select **Sign in to 10 Play**.
5. Open `https://10.com.au/activate`, enter the displayed code, and approve the device in your own 10 Play account.
6. Wait for Jellyfin to report that sign-in completed, then open the 10 Play channel.

The provider tokens are held in Jellyfin's local plugin configuration. Treat the Jellyfin server and its configuration backups as sensitive.

This plugin is not affiliated with Network 10 or Jellyfin. It does not remove or bypass advertising; it will play only the stream the provider returns for the signed-in account.

## About Jellyfin's repository warning

This is a manually installed test build, not a published plugin repository. Jellyfin may show **“An error occurred while getting the plugin details from the repository”** on its plugin-details page. That message only concerns repository metadata and does not stop the installed plugin from loading or its Settings page from working.
