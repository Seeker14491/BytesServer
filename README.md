# BytesServer

A BepInEx mod for [Distance](https://survivethedistance.com/) that turns the game into an HTTP server that provides endpoints for converting between the game's `.bytes` format and XML.

## Building

No prebuilt plugin files are provided. You'll need an up-to-date version of the .NET SDK to build the plugin.

## Installation

1. Start with a clean game install. The mod disables Steam integration, so you're free to move or copy the installation to another location.
2. Install BepInEx 5.
3. Launch the game briefly by running `Distance.exe` so BepInEx generates its folder structure and config files.
4. (optional) Enable the BepInEx console by editing `BepInEx\config\BepInEx.cfg`, and under `[Logging.Console]`, setting `Enabled = true`.
5. Install the BytesServer plugin dll to the `BepInEx\plugins` folder.
6. Create a script to launch the game. On Windows, for example, create a `start.bat` file next to `Distance.exe` with these contents:
    ```
   Distance.exe -batchmode -nographics
   ```
   These arguments will launch the game without the unneeded graphical window.

## Running

You can use the script you created above to launch the game/server. A message stating the server is listening will be printed to the console when the server is ready. The server listens on port 18500 by default, but this can be changed by setting the `BYTES_SERVER_PORT` environment variable.

## HTTP Endpoints

These endpoints require making a POST request with the `.bytes` or XML data provided in the request body. The response body will consist of the converted data. The `/level-*` endpoints support conversion of level files, while the `/gameobject-*` endpoints support non-level `.bytes` file conversion (local leaderboards, replays, custom objects, profiles, `LevelInfos.bytes`).

- POST `/level-bytes-to-xml`
- POST `/level-xml-to-bytes`
- POST `/gameobject-bytes-to-xml`
- POST `/gameobject-xml-to-bytes`

There is a simple healthcheck endpoint that simply returns `OK`:

- GET `/ping`
