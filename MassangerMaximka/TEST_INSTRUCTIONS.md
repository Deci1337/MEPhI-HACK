# Hex P2P Transport - Test Instructions

## Quick Test (2 instances on one PC)

1. **Run first instance**
   ```powershell
   cd MassangerMaximka
   dotnet run -f net9.0-windows10.0.19041.0 -p MassangerMaximka
   ```

2. **Run second instance** (new terminal)
   ```powershell
   cd MassangerMaximka
   dotnet run -f net9.0-windows10.0.19041.0 -p MassangerMaximka
   ```

3. **Manual connect**
   - In second window: enter `127.0.0.1:45680` and click Connect
   - Peers list should show connection with [OK]
   - Select peer, type message, click Send
   - Both windows should show messages in Chat log

## Test on 2 PCs in LAN

1. Find PC1 IP: `ipconfig` (e.g. 192.168.1.10)
2. Run app on PC1
3. Run app on PC2
4. On PC2: enter `192.168.1.10:45680`, click Connect
5. Send messages both ways

## Discovery (auto-find peers)

- Both apps send UDP broadcast on port 45678
- Peers appear in list automatically if in same LAN
- If discovery fails, use manual connect

## Firewall

Windows may ask to allow the app. Allow both:
- Private networks (for discovery and connections)
- TCP 45680, UDP 45678
