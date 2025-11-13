using System.Collections.Generic;
using Mirror;
using UnityEngine;

public class CustomNetworkManager : NetworkManager
{
    private int nextPid = 0;
    private readonly Dictionary<NetworkConnectionToClient, NetPlayer> connToPlayer = new();

    public override void OnStartServer()
    {
        base.OnStartServer();
        nextPid = 0;
        connToPlayer.Clear();
    }

    public override void OnStopServer()
    {
        base.OnStopServer();
        nextPid = 0;
        connToPlayer.Clear();
    }

    public override void OnServerAddPlayer(NetworkConnectionToClient conn)
    {
        // Spawn the player prefab (base does this) and grab our NetPlayer
        base.OnServerAddPlayer(conn);

        var np = conn.identity.GetComponent<NetPlayer>();
        np.pid = nextPid++;                    // authoritative pid
        connToPlayer[conn] = np;

        Debug.Log($"[NET] Player joined. pid={np.pid}");
    }

    public override void OnServerDisconnect(NetworkConnectionToClient conn)
    {
        if (connToPlayer.TryGetValue(conn, out var np))
        {
            Debug.Log($"[NET] Player pid={np.pid} disconnected.");
            connToPlayer.Remove(conn);
            // TODO later: if it’s the active player, safely advance turn or pause
        }

        base.OnServerDisconnect(conn);
    }

    public NetPlayer GetPlayerByPid(int pid)
    {
        foreach (var kv in connToPlayer)
            if (kv.Value.pid == pid) return kv.Value;
        return null;
    }

    public IReadOnlyCollection<NetPlayer> GetAllPlayers()
    {
        return connToPlayer.Values;
    }
}
