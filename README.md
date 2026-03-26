# Teams Streaming Call Bot

This project is a starter ASP.NET Core service for a Microsoft Teams calling bot that receives call notifications on `/api/calls`, tracks active calls, and archives the raw webhook payloads for inspection.

## What this project does

- Accepts Microsoft Graph call notifications on `/api/calls`
- Tracks active calls in memory and exposes them with `GET /api/calls`
- Persists incoming notification payloads under `captures/signaling/`
- Detects when audio/video modalities are requested and records a media-capture handoff note

## What this project does not do yet

- Receive raw audio/video frames on macOS
- Initialize the Windows-only application-hosted media platform automatically
- Join or answer calls through Microsoft Graph

Receiving real-time media from Teams calls requires the Graph communications media platform on a supported Windows host. The webhook on `/api/calls` is only the signaling/control-plane entry point.

## Configuration

Configuration lives in `appsettings.json` under `TeamsCallBot`.

Do not keep secrets in source control. Set the app ID, app secret, and certificate thumbprint through environment variables or user secrets:

```bash
dotnet user-secrets init
dotnet user-secrets set "TeamsCallBot:AadAppId" "<your-aad-app-id>"
dotnet user-secrets set "TeamsCallBot:AadAppSecret" "<your-aad-app-secret>"
dotnet user-secrets set "TeamsCallBot:CertificateThumbprint" "<your-certificate-thumbprint>"
```

## Run locally

```bash
dotnet run
```

The root endpoint returns basic service metadata. The call webhook is:

```text
POST /api/calls
```

## Example notification test

```bash
curl -X POST http://localhost:5233/api/calls \
  -H "Content-Type: application/json" \
  -d '{
    "value": [
      {
        "id": "notification-1",
        "changeType": "updated",
        "tenantId": "tenant-1",
        "resource": "/communications/calls/call-123",
        "resourceData": {
          "id": "call-123",
          "state": "established",
          "requestedModalities": ["audio", "video"]
        }
      }
    ]
  }'
```

## Next step for real media capture

Implement the Windows media host inside `MediaCaptureCoordinator` and attach the Microsoft Graph communications media sockets there. That is the boundary where audio and video frame handling belongs.