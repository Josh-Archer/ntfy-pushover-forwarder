# ntfy-pushover-forwarder

A lightweight, resilient .NET 9 Worker Service designed to bridge self-hosted [ntfy](https://ntfy.sh/) servers and [Pushover](https://pushover.net/). 

This project was created to solve the limitations of the free ntfy mobile app (such as push notification limits or unreliability on certain platforms) by seamlessly forwarding your internal `ntfy` streams to Pushover, utilizing Pushover's robust native delivery network and features.

## ✨ Features

- **Real-time Forwarding:** Uses Server-Sent Events (SSE) to listen to `ntfy` topics continuously with minimal overhead.
- **Topic-to-App Routing:** Map different `ntfy` topics (e.g., `media-updates`, `system-alerts`) to distinct Pushover API Tokens. This allows you to have separate icons and applications within your Pushover client for different notification categories.
- **Sound Mapping:** Translate standard `ntfy` tags (like `siren`, `warning`) or custom tags into native Pushover notification sounds.
- **Dynamic Attachments:** 
  - Automatically forwards images attached to `ntfy` messages.
  - Features an **Icon Fallback** system: Map tags (e.g., `film_projector` for Radarr) to specific URLs. If a message lacks an image attachment, the forwarder will download the mapped application logo and attach it to the Pushover notification so it appears beautifully on your lock screen.
- **Device Targeting:** Target specific devices in your Pushover account using `ntfy` tags like `device:iphone`.
- **Priority Mapping:** Automatically translates `ntfy` priorities (1-5) to the corresponding Pushover priorities (-2 to 2), including handling emergency alerts with retry/expire logic.

## 🚀 Deployment

This service is packaged as a Docker container and published to GHCR. A Helm chart is also provided for easy deployment to Kubernetes.

### Docker

```bash
docker run -d \
  -e Forwarder__NtfyUrl="https://ntfy.yourdomain.com" \
  -e Forwarder__NtfyToken="tk_your_ntfy_token" \
  -e Forwarder__PushoverUserKey="your_pushover_user_key" \
  -e Forwarder__PushoverDefaultToken="your_default_pushover_api_token" \
  -e Forwarder__Topics__0="system-alerts" \
  ghcr.io/josh-archer/ntfy-pushover-forwarder:latest
```

### Kubernetes (Helm)

The recommended way to deploy is using the provided Helm chart. 

1. Create a Kubernetes Secret containing your tokens:
```yaml
apiVersion: v1
kind: Secret
metadata:
  name: ntfy-secret
stringData:
  token: "tk_your_ntfy_token"
  pushover_user_key: "your_pushover_user_key"
  pushover_api_token: "your_default_pushover_api_token"
  # Optional: App-specific tokens mapped to topics
  pushover_api_token_media: "media_app_token"
```

2. Define your `values.yaml`:
```yaml
forwarder:
  ntfyUrl: "http://ntfy.monitoring.svc.cluster.local"
  topics:
    - media-updates
    - system-alerts
  
  soundMap:
    siren: siren
    film_projector: bike
    
  logoMap:
    film_projector: "https://raw.githubusercontent.com/Radarr/Radarr/develop/Logo/256.png"

existingSecret: "ntfy-secret"
secretKeys:
  ntfyToken: "token"
  pushoverUserKey: "pushover_user_key"
  pushoverDefaultToken: "pushover_api_token"
  pushoverTopicTokens:
    media-updates: "pushover_api_token_media"
```

3. Install the chart:
```bash
helm upgrade --install ntfy-forwarder oci://ghcr.io/josh-archer/ntfy-pushover-forwarder \
  --version 0.1.1 \
  -f values.yaml
```

## ⚙️ Configuration

Configuration is handled seamlessly via .NET `appsettings.json` or Environment Variables. 

### Core Settings
| Environment Variable | Description |
|---|---|
| `Forwarder__NtfyUrl` | (Required) The base URL of your ntfy server (e.g., `https://ntfy.example.com`). |
| `Forwarder__NtfyToken` | (Optional) Authentication token for ntfy. |
| `Forwarder__PushoverUserKey` | (Required) Your Pushover User Key. |
| `Forwarder__PushoverDefaultToken` | (Required) The fallback Pushover App API Token. |
| `Forwarder__Topics__0`... | Array of ntfy topics to listen to. |

### Advanced Mapping

You can define mappings using environment variables, but it's much easier using a mounted `appsettings.json` (which the Helm chart does automatically).

**Topic to App Tokens (`TopicTokens`):**
```json
"TopicTokens": {
  "media-updates": "pushover_token_for_media_app",
  "system-alerts": "pushover_token_for_system_app"
}
```

**Tag to Sound (`SoundMap`):**
Maps an ntfy tag string to a valid [Pushover Sound name](https://pushover.net/api#sounds).
```json
"SoundMap": {
  "siren": "siren",
  "success": "magic",
  "tv": "classical"
}
```

**Tag to Logo (`LogoMap`):**
Maps an ntfy tag string to a public URL. The forwarder downloads this image and attaches it to the Pushover payload.
```json
"LogoMap": {
  "film_projector": "https://raw.githubusercontent.com/Radarr/Radarr/develop/Logo/256.png",
  "tv": "https://raw.githubusercontent.com/Sonarr/Sonarr/develop/Logo/256.png"
}
```

## 🛠️ Development

Built with C# and .NET 9.

```bash
cd src
dotnet restore
dotnet run
```
