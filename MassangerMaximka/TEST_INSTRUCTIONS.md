# Hex P2P Transport - Test Instructions

## Two instances on one PC

**Instance 1** (default ports 45680/45678):
```powershell
cd MassangerMaximka
dotnet run -f net9.0-windows10.0.19041.0 -p MassangerMaximka
```

**Instance 2** (ports 45681/45679):
```powershell
cd MassangerMaximka
$env:HEX_TCP_PORT=45681; dotnet run -f net9.0-windows10.0.19041.0 -p MassangerMaximka
```

Or via command line:
```powershell
dotnet run -f net9.0-windows10.0.19041.0 -p MassangerMaximka -- --port 45681
```

**Connect:**
- In Instance 2: enter `127.0.0.1:45680`, click Connect (connects to Instance 1)
- In Instance 1: enter `127.0.0.1:45681`, click Connect (connects to Instance 2)
- Select peer, type message, click Send

## Test on 2 PCs in LAN

1. Find PC1 IP: `ipconfig` (e.g. 192.168.1.10)
2. Run app on PC1 (default ports)
3. Run app on PC2
4. On PC2: enter `192.168.1.10:45680`, click Connect
5. Send messages both ways

## Port mapping

| Instance | TCP Port | Discovery Port |
|----------|----------|----------------|
| 1 (default) | 45680 | 45678 |
| 2 (HEX_TCP_PORT=45681) | 45681 | 45679 |

## Firewall

Allow the app for Private networks. Ports: TCP 45680/45681, UDP 45678/45679.
