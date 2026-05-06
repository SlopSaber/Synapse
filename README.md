# Synapse
EXSII featured a truly interactive live event, enabled by Synapse, a cutting-edge mod that brings players from around the world together to engage with innovative new maps in real-time.
Participants compete against each other on a shared leaderboard and can communicate seamlessly using the in-game chat feature.
### https://exsii.totalbs.dev/

## Features
- Takeover the main menu with your own prefabs during the countdown to build hype.
- A countdown and custom banner from the main menu, along with notifications from anywhere in-game that your event is live.
- A 1-hour grace period to automatically download required mods for an event.
- Multiple divisions so all players can enjoy their preferred difficulty.
- A fully featured chatroom where players can interact with each other, complete with moderation tools like banning malicious users, as well as toggleable options like a profanity filter or opting-out of chat entirely.
- Replace the lobby with a custom prefab themed around your event, which also includes custom cinematics that can play as an intro or outro.
- Download and synchronously start maps for all players to experience maps at the same time.
- An event leaderboard where players can compete with each other, as well as the ability to run tournament formats which can eliminate players each round.
- All dockerized to be easily portable.
- Seamlessly runs, even with 1900+ players, as shown during EXSII.

## Setup for event hosts
#### Interested in running an event using Synapse? Contact me and I can help config and list your event using the official API.

By default, Synapse will check `https://synapse.totalbs.dev/api/v1/directory` for an active listing.
This can be changed by going to `UserData/Synapse.json` and editing the URL to point to your own API.

The backend for Synapse is made up of two projects, the listing and the server.
- The listing is a simple API that the client mod will ping everytime the game is started. It contains necessary information such where to connect to the server, when the event starts, and any required assets needed to join.
- The server is what the clients actually connect to. It runs the entire event.

The recommended way to run the server is using docker.
Example `docker-compose.yml`:
```yml
services:
  synapse-listing:
    image: ghcr.io/aeroluna/synapse-listing
    container_name: synapse-listing
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - ASPNETCORE_URLS=http://synapse-listing:1000
      - ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
    volumes:
      - ./synapse/listing/wwwroot:/app/wwwroot
      - ./synapse/listing/appsettings.Production.json:/app/appsettings.Production.json
    restart: unless-stopped

  synapse-server:
    image: ghcr.io/aeroluna/synapse-server
    container_name: synapse-server
    depends_on:
      - synapse-listing
    environment:
      - DOTNET_ENVIRONMENT=Production
    volumes:
      - ./synapse/server/appsettings.Production.json:/app/appsettings.Production.json
      - ./synapse/server/config:/config
    ports:
      - 1001:1001
    stdin_open: true 
    tty: true
```

The configuration is done using two `appsettings.ENVIRONMENT.json` for the listing and server. This will typically be `appsettings.Production.json`. Example configs can be found at `Synapse.Listing/appsettings.json.sample` and `Synapse.Server/appsettings.json.sample`

Although assets can be hosted from `wwwroot/` using the Listing project, it is recommended to use a CDN. Example assets can be found in the `SampleAssets` directory.

Listing appsettings:
```json5
{
  "Listing": {
    "Title": "Extra Sensory II", // Name shown to players
    "Time": "2025-01-26T22:00:00Z", // UTC time to start the event
    "IpAddress": "127.0.0.1:1001", // IP address of the server. Clients should be able to connect to this. IP:PORT
    "BannerImage": "http://localhost:5033/images/banner.png", // Banner image shown in menu 200x500.
    "BannerColor": "#341552", // Hex code of banner in main menu
    "GameVersion": "1.29.1,1.34.2,1.37.1,1.39.1,1.40.0", // Game versions that can connect. Comma separated exact versions
    "Divisions": [ // Divisions for event. If empty, no divisions will be used.
      {
        "Name": "Casual",
        "Description": "For those looking to enjoy the game at a more casual pace, However, you may still find later maps challenging. Notes per second is lower, but note gimmicks are unaffected."
      },
      {
        "Name": "Experienced",
        "Description": "Experience the maps as they were originally intended. Higher notes per second for those looking to put their skills to the test"
      }
    ],
    "Takeover": { // Main menu takeover during lead up to event
      "DisableDust": true, // Disables global dust when true
      "DisableLogo": true, // Disables default logo when true
      "CountdownTMP": "Logo/EXSII_COUNTDOWN", // Path to TextMeshPro to update with countdown
      "Bundles": [
        {
          "GameVersion": "1.29.1", // Comma separated game versions that use this bundle
          "Url": "https://localhost:5033/bundle/takeover_windows2019_3913474686",
          "Hash": 3913474686 // CRC hash of bundle. Bundle will not load if this is wrong
        },
        {
          "GameVersion": "1.34.2,1.37.1,1.39.1,1.40.0",
          "Url": "https://localhost:5033/bundle/takeover_windows2021_2910218729",
          "Hash": 2910218729
        }
      ]
    },
    "Lobby": {
      "DisableDust": true, // Disables global dust when true
      "DisableSmoke": true, // Disables global smoke when true
      "DepthTextureMode": 1, // Bitmask to force camera depth texture mode. https://docs.unity3d.com/ScriptReference/DepthTextureMode.html
      "Bundles": [
        {
          "GameVersion": "1.29.1",
          "Url": "https://localhost:5033/bundle/bundle_windows2019_853015874",
          "Hash": 853015874
        },
        {
          "GameVersion": "1.34.2,1.37.1,1.39.1,1.40.0",
          "Url": "https://localhost:5033/bundle/bundle_windows2021_1876858522",
          "Hash": 1876858522
        }
      ]
    },
    "RequiredMods": [ // Mods that will be downloaded when trying to join event. These will also be downloaded an hour early. Be sure to include all mod dependencies as well
      {
        "GameVersion": "1.29.1",
        "Mods": [
          {
            "Id": "Vivify",
            "Version": "^1.0.1", // Version range to check for. If player's version does not match, will try to install
            "Url": "https://localhost:5033/mods/Vivify-1.0.1%2B1.29.1-bs1.29.1-d32ee3d.zip",
            "Hash": "2e3250b635f5d8c5711ba8d7fd996b4a" // MD5 hash of zip
          }
        ]
      }
    ]
  }
}
```
Server appsettings:
```json5
{
  "Port": 1001, // Port to listen on
  "Listing": "http://synapse-listing:1000/api/v1/directory", // URL of the listing
  "MaxPlayers": -1, // Max players allowed to connect. Set to -1 for infinite
  "Directory": "/config", // Where to save persistent files such as scores/logs
  "Auth": {
    "Test": { // Used by TestClient
      "Enabled": false
    },
    "Steam": {
      "Enabled": true,
      "APIKey": "" // Generate an API key at https://steamcommunity.com/dev
    },
    "Oculus": {
      "Enabled": true
    }
  },
  "Event": {
    "Title": "Extra Sensory II", // Name used for logs
    "Format": "Showcase", // Currently supported: [None, Showcase]
    "Intro": { // Event goes through three stages, Intro, Play, and Finish
      "Motd": "<color=#ff6464><size=120%><mspace=0.6em>//// <b><color=#ffffff>CONNECTION ESTABLISHED</color></b> ////", // MOTDs are displayed when moving between maps/stages and when joining
      "Intermission": "00:05:00", // Wait period before starting the intro
      "Duration": "00:01:00", // Duration of intro until changing stages
      "Url": "http://localhost:5033/images/intro.png" // Image shown while waiting for intro. 400x700
    },
    "Finish": {
      "Motd": "<color=#ff6464><size=120%><mspace=0.6em>//// <b><color=#ffffff>CONNECTION TERMINATED</color></b> ////<br><color=#ffffff>YOUR COOPERATION IS APPRECIATED",
      "Url": "http://localhost:5033/images/finish.png" // Image shown after outro. 400x700
    },
    "Maps": [
      {
        "Name": "Breezer", // Name shown on the leaderboard
        "AltCoverUrl": "https://localhost:5033/maps/alternative_breezer.png", // Alternative cover to show. Setting this will show "???" before playing the song
        "Motd": "<size=120%><#FFFFFF>[<#FF2121><b>ERROR</b><#FFFFFF> @ <#3171E8>02:18:23<#FFFFFF> | Vivify] Map file <#45B543>'breezer.zip'<#FFFFFF> has been breached by unknown source",
        "Intermission": "00:10:00", // Wait period before map start
        "Duration": "00:10:00", // Duration of song before changing maps. Should be more than the map's duration to make sure scores can be submitted before ending
        "Ruleset": {
          "AllowResubmission": true, // Allows players to retry for a better score
          "Modifiers": ["noEnergy"] // Modifiers to use. Synapse adds the "noEnergy" modifier, a special modifier where the energy bar is disabled
        },
        "Keys": [ // Should be a key for every division. This tells the game which difficulty to use
          {
            "Characteristic": "Standard",
            "Difficulty": 1 // 0 = Easy, 1 = Normal, 2 = Hard, 3 = Expert, 4 = Expert+
          },
          {
            "Characteristic": "Standard",
            "Difficulty": 2
          }
        ],
        "Downloads": [
          {
            "GameVersion": "1.29.1", // Comma separated game versions
            "Url": "https://localhost:5033/maps/Breezer_2019_3d3304c.zip",
            "Hash": "3d3304c27e4cd48cb0f9da768ce833a2" // MD5 hash of zip file
          },
          {
            "GameVersion": "1.34.2,1.37.1,1.39.1,1.40.0",
            "Url": "https://localhost:5033/maps/Breezer_2021_2c170c1.aes",
            "Hash": "2c170c14544b25b055029d2ca67b932c",
            "Key": "66B85F44B087F8474E7B1183FA3365A0CECE3B9DA7E09D42407A934E26D2F530" // optional key to decrypt AES-256 encrypted zips
          }
        ]
      }
    ]
  }
}
```

## Commands
Roles can grant the following permissions:
- 1 = Coordinator: Can control the flow of the event, i.e. start/stop maps manually, change motd, etc.
- 2 = Moderator: Can moderate other users, i.e. ban or kick users
- 4 = NoQualify: Users with this permission are unable to qualify
Example `roles.json`:
```json5
[
  {
    "name": "coordinator",
    "priority": 99, // Can only affect users with a lower priority than themselves
    "color": "red", // Username color in chat. See https://digitalnativestudios.com/textmeshpro/docs/rich-text/#color. Will use role with highest priority
    "permission": 1 // Bitmask of permissions
  }
]
```
Example `admins.json`:
```json5
[
  {
    "roles": [
      "moderator",
      "mapper",
      "coordinator"
    ],
    "id": "76561198301904113_Steam",
    "username": "Aeroluna"
  }
]
```
All users that connect will be given an ID in the following format: `PLATFORMID_PLATFORM`, e.g. `76561172306506184_Steam` or `2026218196532328_OculusRift`.

Commands be sent through the server, or from a client with appropriate permissions by beginning a chat message with `/`, e.g. `/say hello!`

Commands which need an ID/username can use the start of a name instead. i.e. `kick aero` will find the user `Aeroluna`.
Commands that use options can be combined, i.e. `-e -f` is the same as `-ef`.

Nested lists represent subcommands, e.g. `scores backup reload`.
### Client
- `motd [message]` Prints the motd again or sets a new one. Allows rich text. Requires `coordinator` to set an motd
- `roll [min] [max]` Rolls a random number. Rolls between 1-100 with no parameters, and between 1-MAX with one parameter, and MIN-MAX with two parameters.
- `tell [player] [message]` (`t`, `whisper`, `w`) Privately message another player. Messages will still be logged by the server.
- `who [options] [player]` Prints how many players are currently connected. May specify a name to find all players whose name starts with that name. `-e` to print more names. `-v` to print IDs (requires `moderator`).
- `ping` Prints current latency between client and server. (Client only)
### Message
- `say [message]` Sends a priority message to everyone with the format `[Server] MESSAGE`. Allows rich text. Requires `coordinator`.
- `sayraw [message]` Sends a priority message without formatting. Allows rich text. Requires `coordinator`.
### Users
- `allow [player]` Adds a user to the whitelist. Requires `moderator`.
- `ban [player] [reason] [time]` Bans a user. Optionally set a reason and/or duration. Requires `moderator`.
- `banip [player]` Bans a user by ip. Requires `moderator`.
- `kick [player]` Kicks a user. Requires `moderator`.
- `blacklist` Requires `moderator`.
  - `reload` Reloads `blacklist.json` from disk.
  - `list` Lists currently banned users.
  - `add [id] [username]` Manually add an ID/username to the blacklist.
  - `remove [options] [username]` Remove a user from the blacklist. `-i` to search by ID instead.
- `bannedips` Requires `moderator`.
  - `reload` Reloads `bannedips.json` from disk.
  - `list` Lists currently banned ips.
  - `add [ip]` Manually add an IP to the blacklist.
  - `remove [ip]` Remove an IP from the blacklist.
- `roles`  Requires `coordinator`.
  - `reload` Reloads `roles.json` and `admins.json` from disk.
  - `list` Lists all admins and their roles.
  - `listroles` Lists all roles.
  - `add [options] [username] [role]` Add a role to a user. `-i` to search by ID instead.
  - `remove [options] [username] [role]` Remove a role to a user. `-i` to search by ID instead.
- `whitelist`  Requires `moderator`.
  - `reload` Reloads `whitelist.json` from disk.
  - `list` Lists all whitelisted users.
  - `add [id] [username]` Manually add an ID/username to the whitelist.
  - `remove [options] [username] [role]` Remove a user from the whitelist. `-i` to search by ID instead.
### Scores
- `scores` Requires `coordinator`. Refer to divisions by index, i.e. 0 = Casual, 1 = Experienced. 
  - `refresh [map index]` Resends map's leaderboard to all players. Uses current map index if not specified.
  - `remove [options] [division] [map index] [username]` Removes a score. Uses current map index if not specified. `-i` to search by ID instead.
  - `drop [division] [map index]` Drops all scores for a map. Uses current map index if not specified.
  - `resubmit [map index]` Resubmit scores for the map to the tournament format.
  - `list [options] [division] [map index]` List all submitted scores. Uses current map index if not specified. `-v` to print the scores, `-e` to print more.
  - `test` Submit fake scores for the current map.
  - `backup`
    - `reload` Reload score backups from disk.
### Event
- `event` Requires `coordinator`.
  - `status` Displays the current status of the event.
  - `start [seconds]` Starts the intermission for the current stage. Uses time from config if not specified.
  - `play [seconds]` Plays the current stage. Uses time from config if not specified.
  - `stop` Stops the current stage. Will kick users out of intros, outros and levels.
  - `stage [stage index]` Changes the stage. Can use `n` or `p` instead of an index for "next" or "previous" respectively.
  - `index [options] [map index]` Changes the map. Can use `n` or `p` instead of an index for "next" or "previous" respectively. `-s` to additionally submit scores to the tournament format. `-a` to auto-start the next with the time from the config.

## TestClient
Synapse.TestClient is a simple client designed to emulate chatting and setting scores. Input the URL to the listing in the `appsettings.ENVIRONMENT.json`.
Comes with the following commands:
- `stop` Disconnect all clients and close.
- `deploy [count]` Deploys a specific amount of clients to connect to the server.
- `score` Command all clients to submit a random score for the current map.
- `send` Command all clients to start sending random chat messages.
- `roll` Command all clients to send the roll command to the server.
