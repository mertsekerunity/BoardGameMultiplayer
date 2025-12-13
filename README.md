# Multiplayer Stock Market Board Game

## Summary
Character-driven multiplayer stock market board game, for **3-6 players.** <br/>
Each round flows through: **Bidding → Character selection → main phase (buy / sell / ability) → Close sell & Tax resolution.**

---

## Gameplay Features <br/>
- **Closed draft / bidding system** <br/>
- **Open and close stock selling** mechanics <br/>
- **Bankruptcy & price ceiling** rules that reset market <br/>
- **Lottery, tax and manipulation** <br/>
- **Per-turn Undo system** <br/>

---

## Technical Features <br/>
- Built with **Unity 2022.3.55f1** + **Mirror** networking
- **Server-authoritative** turn system (all core logic runs on the host / server)
- Custom `CustomNetworkManager` and `NetPlayer` structure
- Clear **Command → Server → RPC / TargetRpc** flow
- Modular game state management via:
  - `StockMarketManager`
  - `PlayerManager`
  - `DeckManager`
  - `TurnManager`   
- **TargetRpc-based** prompt system for:
  - Local prompts (only the active player can see)
  - Global prompts / banners (announcements to everyone)
- End-of-round resolution pipeline (close sell, tax, market checks)

---

## Architecture
### Core Scene Objects
  - `CustomNetworkManager`
  - `GameManager`
  - `TurnManager`
  - `PlayerManager`
  - `StockMarketManager`
  - `DeckManager`
  - `UIManager` + panels
    - `BiddingPanel`
    - `PlayerPanel`
    - `MarketRow`
    - `PlayerAidPanel`
    - Various prompt / confirmation panels

### Flow: Player ↔ Server
- **Input (client)** → `NetPlayer` (Mirror **Commands**)  
- **Game logic (server)** → `TurnManager` + managers  
- **Sync back to clients** → `ClientRpc` / `TargetRpc` calls updating UI & state

---
 
## Build / Run
### Requirements
  - Unity **2022.3.55f1**
  - Mirror package (matching 2022 LTS; see `Packages/manifest.json` in project)

### How to run
1. Open the project in Unity.
2. Open the **Bootstrap** scene.
3. Choose:
   - **Host**: creates a lobby and acts as the server.
   - **Join**: connects to an existing host.

### Networking
- **Host**
  - On LAN: other players can connect using your local IP.
  - Over the internet: use **port forwarding** or a VPN solution (e.g. Hamachi) so clients can reach the host’s IP.
- **Client**
  - Enter the host’s IP address in the address field and click **Join**.

*(Relay / dedicated server support is not implemented yet, but the architecture is compatible with swapping transports.)*

---
 
## Future Improvements
- **Global event log panel**
  - Last N public events (bids, open buys / sells, tax, market triggers, etc.)
- **Richer lobby system**
  - Room list, room names, password protection
  - “Ready” state & start countdown
- **UI / UX polish**
  - Better feedback for connection state
  - Improved layout for 6-player games
  - Additional player aids and tooltips
