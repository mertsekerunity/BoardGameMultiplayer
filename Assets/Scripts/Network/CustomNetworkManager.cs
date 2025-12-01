using System.Collections.Generic;
using Mirror;
using UnityEngine;

public class CustomNetworkManager : NetworkManager
{
    public static CustomNetworkManager Instance { get; private set; }

    public int requiredPlayers;

    [HideInInspector] public string pendingPlayerName = "Player"; // TODO: remote name sync later

    private int nextPid = 0;
    private readonly Dictionary<NetworkConnectionToClient, NetPlayer> connToPlayer = new();

    public override void Awake()
    {
        base.Awake();
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
    }

    public void SetRequiredPlayers(int count)
    {
        //requiredPlayers = Mathf.Clamp(count, 2, 6);
        requiredPlayers = count;
    }

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
        np.pid = nextPid++;
        connToPlayer[conn] = np;

        // tell PlayerManager about this player (Server side)
        PlayerManager.Instance.RegisterNetworkPlayer(np.pid); // ask players to type name while joining

        Debug.Log($"[NET] Player joined. pid={np.pid}");

        if (GameManager.Instance != null)
        {
            GameManager.Instance.TryStartGame();
        }
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
        {
            if (kv.Value.pid == pid)
            {
                return kv.Value;
            }
        }
            
        return null;
    }

    public IReadOnlyCollection<NetPlayer> GetAllPlayers()
    {
        return connToPlayer.Values;
    }
}
