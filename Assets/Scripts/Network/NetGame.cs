using Mirror;
using System.Collections.Generic;
using UnityEngine;

public class NetGame : NetworkBehaviour
{
    public static NetGame Instance;               // convenience (not for authority)
    private void Awake() => Instance = this;

    // Each connection gets a NetPlayer (Mirror spawns it automatically).
    // We’ll assign a stable, server-side playerId and broadcast it.

    [SyncVar] public int playerCount;             // simple counter
    public readonly SyncList<int> connectedPids = new SyncList<int>();

    public override void OnStartServer()
    {
        base.OnStartServer();
        //NetworkManager.singleton.onServerAddPlayer += OnServerAddPlayerHook;
        //NetworkManager.singleton.onServerDisconnect += OnServerDisconnectHook;
    }

    public override void OnStopServer()
    {
        //NetworkManager.singleton.onServerAddPlayer -= OnServerAddPlayerHook;
        //NetworkManager.singleton.onServerDisconnect -= OnServerDisconnectHook;
    }

    private void OnServerAddPlayerHook(NetworkConnectionToClient conn)
    {
        // Mirror already spawned a NetPlayer as conn.identity
        var netPlayer = conn.identity.GetComponent<NetPlayer>();
        netPlayer.pid = playerCount++;
        connectedPids.Add(netPlayer.pid);

        // Optionally echo the pid down specifically to that client for any local-only init:
        TargetReceivePid(conn, netPlayer.pid);
    }

    private void OnServerDisconnectHook(NetworkConnectionToClient conn)
    {
        var netPlayer = conn.identity?.GetComponent<NetPlayer>();
        if (netPlayer != null) connectedPids.Remove(netPlayer.pid);
    }

    [TargetRpc] // runs on that one client
    private void TargetReceivePid(NetworkConnection conn, int assignedPid)
    {
        UIManager.Instance?.SetLocalPlayerId(assignedPid);
    }
}
